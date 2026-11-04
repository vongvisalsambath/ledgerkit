using LedgerKit.PostgreSql;
using Npgsql;
using Xunit;

namespace LedgerKit.Tests;

/// <summary>The same behaviour, whichever store keeps the ledger.</summary>
public abstract class LedgerBehaviour
{
    protected abstract ILedgerStore NewStore();

    /// <summary>Codes and keys unique to one test, so tests sharing a database don't meet.</summary>
    protected readonly string U = Guid.NewGuid().ToString("N")[..10];

    private async Task<Ledger> Ready()
    {
        var ledger = new Ledger(NewStore());
        await ledger.AddAccountAsync(new($"{U}-bank", "Bank: Demo USD", AccountType.Asset, "USD"));
        await ledger.AddAccountAsync(new($"{U}-sales", "Sales", AccountType.Income, "USD"));
        await ledger.AddAccountAsync(new($"{U}-rent", "Rent", AccountType.Expense, "USD"));
        await ledger.AddAccountAsync(new($"{U}-khr", "Cash: riel", AccountType.Asset, "KHR"));
        return ledger;
    }

    private NewEntry Sale(string key, decimal amount, DateOnly? date = null, string? reference = null) =>
        new($"{U}-{key}", date ?? new(2026, 11, 4), "Sale", [NewLine.Debit($"{U}-bank", amount), NewLine.Credit($"{U}-sales", amount)], reference);

    [Fact]
    public async Task ABalancedEntryIsPostedAndChangesBalances()
    {
        var ledger = await Ready();
        var r = await ledger.PostAsync(Sale("s1", 1500.25m));
        Assert.False(r.AlreadyPosted);
        Assert.Equal(1500.25m, await ledger.BalanceAsync($"{U}-bank"));
        Assert.Equal(-1500.25m, await ledger.BalanceAsync($"{U}-sales"));
        Assert.Equal(0m, await ledger.BalanceAsync($"{U}-bank", new DateOnly(2026, 11, 3)));
        var entry = await ledger.Store.FindEntryAsync(r.EntryId);
        Assert.NotNull(entry);
        Assert.Equal([1500.25m, -1500.25m], entry.Lines.Select(l => l.Amount));
        Assert.All(entry.Lines, l => Assert.Equal("USD", l.Currency));
    }

    [Fact]
    public async Task PostingTheSameEntryAgainRecordsItOnce()
    {
        var ledger = await Ready();
        var first = await ledger.PostAsync(Sale("s1", 10m));
        var again = await ledger.PostAsync(Sale("s1", 10.00m)); // 10 and 10.00 are the same amount
        Assert.True(again.AlreadyPosted);
        Assert.Equal(first.EntryId, again.EntryId);
        Assert.Equal(10m, await ledger.BalanceAsync($"{U}-bank"));
    }

    [Fact]
    public async Task AKeyCantBeReusedForADifferentEntry()
    {
        var ledger = await Ready();
        await ledger.PostAsync(Sale("s1", 10m));
        var e = await Assert.ThrowsAsync<LedgerException>(() => ledger.PostAsync(Sale("s1", 11m)));
        Assert.Contains("already used for a different entry", e.Message);
        Assert.Equal(10m, await ledger.BalanceAsync($"{U}-bank"));
    }

    [Fact]
    public async Task ConcurrentRetriesOfOneEntryRecordItOnce()
    {
        var ledger = await Ready();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => ledger.PostAsync(Sale("race", 5m)))));
        Assert.Single(results.Select(r => r.EntryId).Distinct());
        Assert.Equal(1, results.Count(r => !r.AlreadyPosted));
        Assert.Equal(5m, await ledger.BalanceAsync($"{U}-bank"));
    }

    [Fact]
    public async Task AReversalCancelsAnEntryOnceOnly()
    {
        var ledger = await Ready();
        var sale = await ledger.PostAsync(Sale("s1", 99.99m));
        var rev = await ledger.ReverseAsync(sale.EntryId, new DateOnly(2026, 11, 5), $"{U}-rev1");
        Assert.Equal(0m, await ledger.BalanceAsync($"{U}-bank"));
        Assert.Equal(99.99m, await ledger.BalanceAsync($"{U}-bank", new DateOnly(2026, 11, 4)));
        var reversal = await ledger.Store.FindEntryAsync(rev.EntryId);
        Assert.Equal(sale.EntryId, reversal!.Reverses);
        Assert.StartsWith("Reversal of: Sale", reversal.Description);

        Assert.Contains("already been reversed", (await Assert.ThrowsAsync<LedgerException>(() => ledger.ReverseAsync(sale.EntryId, new DateOnly(2026, 11, 5), $"{U}-rev2"))).Message);
        Assert.Contains("itself a reversal", (await Assert.ThrowsAsync<LedgerException>(() => ledger.ReverseAsync(rev.EntryId, new DateOnly(2026, 11, 6), $"{U}-rev3"))).Message);
        var later = await ledger.PostAsync(Sale("s2", 1m));
        Assert.Contains("before the entry", (await Assert.ThrowsAsync<LedgerException>(() => ledger.ReverseAsync(later.EntryId, new DateOnly(2026, 11, 1), $"{U}-rev4"))).Message);
        // retrying the same reversal is fine
        Assert.True((await ledger.ReverseAsync(sale.EntryId, new DateOnly(2026, 11, 5), $"{U}-rev1")).AlreadyPosted);
    }

    [Fact]
    public async Task ConcurrentReversalsOfOneEntryLeaveOne()
    {
        var ledger = await Ready();
        var sale = await ledger.PostAsync(Sale("s1", 7m));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await ledger.ReverseAsync(sale.EntryId, new DateOnly(2026, 11, 5), $"{U}-rev{i}"); return true; }
            catch (LedgerException) { return false; }
        })));
        Assert.Equal(1, attempts.Count(ok => ok));
        Assert.Equal(0m, await ledger.BalanceAsync($"{U}-bank"));
    }

    [Theory]
    [InlineData("unbalanced", "doesn't balance in USD")]
    [InlineData("zero", "the amount is zero")]
    [InlineData("decimals", "USD has 2 decimal places")]
    [InlineData("unknown account", "there is no account")]
    [InlineData("one line", "at least two lines")]
    [InlineData("mixed currencies", "doesn't balance in KHR")]
    [InlineData("too large", "more than 1,000,000,000,000,000")]
    [InlineData("bidi in description", "text-direction character (U+202E)")]
    [InlineData("control in memo", "control or text-direction character (U+001B)")]
    [InlineData("bad key", "idempotency key")]
    [InlineData("spaces", "starts or ends with a space")]
    public async Task BadEntriesAreRefusedWithAReason(string what, string reason)
    {
        var ledger = await Ready();
        string bank = $"{U}-bank", sales = $"{U}-sales";
        var d = new DateOnly(2026, 11, 4);
        NewEntry entry = what switch
        {
            "unbalanced" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 100m), NewLine.Credit(sales, 99.99m)]),
            "zero" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 0m), NewLine.Credit(sales, 0m)]),
            "decimals" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 1.005m), NewLine.Credit(sales, 1.005m)]),
            "unknown account" => new($"{U}-x", d, "Sale", [NewLine.Debit("nope", 1m), NewLine.Credit(sales, 1m)]),
            "one line" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 1m)]),
            "mixed currencies" => new($"{U}-x", d, "Exchange", [NewLine.Debit($"{U}-khr", 4100m), NewLine.Credit(bank, 1m), NewLine.Debit(bank, 1m)]),
            "too large" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 2_000_000_000_000_000m), NewLine.Credit(sales, 2_000_000_000_000_000m)]),
            "bidi in description" => new($"{U}-x", d, "Refund \u202E0001", [NewLine.Debit(bank, 1m), NewLine.Credit(sales, 1m)]),
            "control in memo" => new($"{U}-x", d, "Sale", [NewLine.Debit(bank, 1m, "\u001B[31m"), NewLine.Credit(sales, 1m)]),
            "bad key" => new("'; drop table ledgerkit.entry; --", d, "Sale", [NewLine.Debit(bank, 1m), NewLine.Credit(sales, 1m)]),
            "spaces" => new($"{U}-x", d, " Sale", [NewLine.Debit(bank, 1m), NewLine.Credit(sales, 1m)]),
            _ => throw new ArgumentException(what),
        };
        var e = await Assert.ThrowsAsync<LedgerException>(() => ledger.PostAsync(entry));
        Assert.Contains(e.Problems, p => p.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(0m, await ledger.BalanceAsync(bank));
    }

    [Fact]
    public async Task AMultiCurrencyEntryBalancesInEachCurrency()
    {
        var ledger = await Ready();
        await ledger.AddAccountAsync(new($"{U}-fx", "FX clearing: riel", AccountType.Asset, "KHR"));
        await ledger.AddAccountAsync(new($"{U}-fxusd", "FX clearing: USD", AccountType.Asset, "USD"));
        await ledger.PostAsync(new($"{U}-fx1", new(2026, 11, 4), "Bought riel", [
            NewLine.Debit($"{U}-khr", 410_000m), NewLine.Credit($"{U}-fx", 410_000m),
            NewLine.Debit($"{U}-fxusd", 100m), NewLine.Credit($"{U}-bank", 100m)]));
        var tb = await ledger.TrialBalanceAsync();
        foreach (var g in tb.Where(r => r.Account.Code.StartsWith(U, StringComparison.Ordinal)).GroupBy(r => r.Account.Currency))
            Assert.Equal(0m, g.Sum(r => r.Balance));
    }

    [Fact]
    public async Task AccountsAreCheckedAndCantBeRedefined()
    {
        var ledger = await Ready();
        await ledger.AddAccountAsync(new($"{U}-bank", "Bank: Demo USD", AccountType.Asset, "USD")); // the same again: fine
        await Assert.ThrowsAsync<LedgerException>(() => ledger.AddAccountAsync(new($"{U}-bank", "Bank: Demo USD", AccountType.Asset, "EUR")));
        await Assert.ThrowsAsync<LedgerException>(() => ledger.AddAccountAsync(new("bad code!", "x", AccountType.Asset, "USD")));
        await Assert.ThrowsAsync<LedgerException>(() => ledger.AddAccountAsync(new($"{U}-y", "x", AccountType.Asset, "XYZ")));
        await Assert.ThrowsAsync<LedgerException>(() => ledger.AddAccountAsync(new($"{U}-z", "x", (AccountType)99, "USD")));
        Assert.Equal("USD", (await ledger.Store.FindAccountAsync($"{U}-bank"))!.Currency);
    }

    [Fact]
    public async Task LinesComeBackInDateAndPostingOrder()
    {
        var ledger = await Ready();
        await ledger.PostAsync(Sale("b", 2m, new(2026, 11, 5), "R2"));
        await ledger.PostAsync(Sale("a", 1m, new(2026, 11, 4), "R1"));
        await ledger.PostAsync(Sale("c", 3m, new(2026, 11, 5), "R3"));
        var lines = await ledger.Store.LinesAsync($"{U}-bank", new(2026, 11, 1), new(2026, 11, 30));
        Assert.Equal(["R1", "R2", "R3"], lines.Select(l => l.Reference));
        Assert.Equal([1m, 2m, 3m], lines.Select(l => l.Amount));
    }
}

public sealed class InMemoryLedgerTests : LedgerBehaviour
{
    protected override ILedgerStore NewStore() => store;
    private readonly InMemoryLedgerStore store = new();
}

/// <summary>Set LEDGERKIT_TEST_DB to a PostgreSQL connection string (a database these tests may change) to run these.</summary>
[Collection("postgres")]
public sealed class PostgresLedgerTests(PostgresFixture db) : LedgerBehaviour
{
    protected override ILedgerStore NewStore() => new PostgresLedgerStore(db.DataSource);
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class PostgresFixture : IAsyncLifetime
{
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var cs = Environment.GetEnvironmentVariable("LEDGERKIT_TEST_DB")
            ?? throw new InvalidOperationException("Set LEDGERKIT_TEST_DB to a PostgreSQL connection string for a database the tests may change.");
        DataSource = NpgsqlDataSource.Create(cs);
        // test runs sharing the database (say, .NET 8 and .NET 10 in parallel) take turns: this lock is held until DisposeAsync
        turn = await DataSource.OpenConnectionAsync();
        await using (var wait = new NpgsqlCommand("select pg_advisory_lock(7166511)", turn)) { wait.CommandTimeout = 600; await wait.ExecuteNonQueryAsync(); }
        await using (var cmd = DataSource.CreateCommand("drop schema if exists ledgerkit cascade")) await cmd.ExecuteNonQueryAsync();
        await PostgresLedgerStore.EnsureSchemaAsync(DataSource);
        await PostgresLedgerStore.EnsureSchemaAsync(DataSource); // running it again changes nothing
    }

    private NpgsqlConnection? turn;

    public async Task DisposeAsync()
    {
        if (turn is not null) await turn.DisposeAsync(); // closing the connection releases the lock
        await DataSource.DisposeAsync();
    }
}
