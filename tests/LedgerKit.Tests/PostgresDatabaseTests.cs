using LedgerKit.PostgreSql;
using Npgsql;
using Xunit;

namespace LedgerKit.Tests;

/// <summary>The database keeps the ledger's rules even for SQL that goes around LedgerKit.</summary>
[Collection("postgres")]
public sealed class PostgresDatabaseTests(PostgresFixture db)
{
    private readonly string u = Guid.NewGuid().ToString("N")[..10];

    private async Task<(Ledger ledger, Guid entry)> Posted()
    {
        var ledger = new Ledger(new PostgresLedgerStore(db.DataSource));
        await ledger.AddAccountAsync(new($"{u}-bank", "Bank", AccountType.Asset, "USD"));
        await ledger.AddAccountAsync(new($"{u}-sales", "Sales", AccountType.Income, "USD"));
        await ledger.AddAccountAsync(new($"{u}-khr", "Cash: riel", AccountType.Asset, "KHR"));
        var r = await ledger.PostAsync(new($"{u}-1", new(2026, 11, 4), "Sale", [NewLine.Debit($"{u}-bank", 100m), NewLine.Credit($"{u}-sales", 100m)]));
        return (ledger, r.EntryId);
    }

    private async Task<PostgresException> Refused(string sql, params object[] args)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new() { Value = a });
        return await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        });
    }

    /// <summary>Several statements in one transaction, committed at the end.</summary>
    private async Task<PostgresException> RefusedBatch(params (string Sql, object[] Args)[] commands)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var batch = new NpgsqlBatch(conn, tx);
        foreach (var (sql, args) in commands)
        {
            var c = new NpgsqlBatchCommand(sql);
            foreach (var a in args) c.Parameters.Add(new() { Value = a });
            batch.BatchCommands.Add(c);
        }
        return await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await batch.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        });
    }

    private const string InsertEntry = "insert into ledgerkit.entry (id, idempotency_key, content_hash, entry_date, description, line_count) values ($1, $2, repeat('A', 64), '2026-11-04', 'Direct', 2)";

    [Fact]
    public async Task PostedRowsCantBeUpdatedDeletedOrTruncated()
    {
        var (ledger, entry) = await Posted();
        Assert.Contains("append-only", (await Refused("update ledgerkit.line set amount = 1000 where entry_id = $1", entry)).MessageText);
        Assert.Contains("append-only", (await Refused("delete from ledgerkit.line where entry_id = $1", entry)).MessageText);
        Assert.Contains("append-only", (await Refused("update ledgerkit.entry set description = 'edited' where id = $1", entry)).MessageText);
        Assert.Contains("append-only", (await Refused("delete from ledgerkit.entry where id = $1", entry)).MessageText);
        Assert.Contains("append-only", (await Refused("update ledgerkit.account set currency = 'EUR' where code = $1", $"{u}-bank")).MessageText);
        Assert.Contains("append-only", (await Refused("truncate ledgerkit.line, ledgerkit.entry, ledgerkit.account")).MessageText);
        Assert.Equal(100m, await ledger.BalanceAsync($"{u}-bank"));
    }

    [Fact]
    public async Task AnUnbalancedEntryWrittenDirectlyIsRefusedAtCommit()
    {
        await Posted();
        var id = Guid.NewGuid();
        var e = await RefusedBatch(
            (InsertEntry, [id, $"{u}-direct"]),
            ("insert into ledgerkit.line (entry_id, line_no, account, amount, currency) values ($1, 1, $2, 1000, 'USD'), ($1, 2, $3, -1, 'USD')", [id, $"{u}-bank", $"{u}-sales"]));
        Assert.Contains("does not balance", e.MessageText);
    }

    [Fact]
    public async Task LinesCantBeAddedToAPostedEntry()
    {
        var (_, entry) = await Posted();
        var e = await Refused("insert into ledgerkit.line (entry_id, line_no, account, amount, currency) values ($1, 3, $2, 50, 'USD'), ($1, 4, $3, -50, 'USD')", entry, $"{u}-bank", $"{u}-sales");
        Assert.Contains("cannot be added to a posted entry", e.MessageText);
    }

    [Fact]
    public async Task ALineMustBeInItsAccountsCurrency()
    {
        await Posted();
        var id = Guid.NewGuid();
        var e = await RefusedBatch(
            (InsertEntry, [id, $"{u}-cur"]),
            ("insert into ledgerkit.line (entry_id, line_no, account, amount, currency) values ($1, 1, $2, 5, 'USD'), ($1, 2, $3, -5, 'USD')", [id, $"{u}-khr", $"{u}-sales"]));
        Assert.Contains("different currency", e.MessageText);
    }

    [Fact]
    public async Task AnEntryWithoutLinesIsRefused()
    {
        await Posted();
        var e = await Refused("insert into ledgerkit.entry (id, idempotency_key, content_hash, entry_date, description, line_count) values ($1, $2, repeat('A', 64), '2026-11-04', 'x', 2)",
            Guid.NewGuid(), $"{u}-empty");
        Assert.Contains("posted with 2 lines but has 0", e.MessageText);
    }

    [Fact]
    public async Task TextThatLooksLikeSqlIsStoredAsText()
    {
        var (ledger, _) = await Posted();
        const string text = "Robert'); drop table ledgerkit.entry; -- $1 \\x";
        var r = await ledger.PostAsync(new($"{u}-sql", new(2026, 11, 4), text, [NewLine.Debit($"{u}-bank", 1m, text[..30]), NewLine.Credit($"{u}-sales", 1m)], "O'Brien"));
        var e = await ledger.Store.FindEntryAsync(r.EntryId);
        Assert.Equal(text, e!.Description);
        Assert.Equal("O'Brien", e.Reference);
        Assert.Equal(text[..30], e.Lines[0].Memo);
    }

    [Fact]
    public async Task TheDatabaseRefusesWhatTheLibraryWouldHaveCaught()
    {
        // a store used without Ledger, with an entry that skipped the checks
        var (ledger, _) = await Posted();
        var bad = new JournalEntry(Guid.NewGuid(), $"{u}-raw", new(2026, 11, 4), "x", null, null,
            [new(1, $"{u}-bank", 10m, "USD", null), new(2, $"{u}-sales", -9m, "USD", null)], new string('A', 64));
        var e = await Assert.ThrowsAsync<LedgerException>(() => ledger.Store.AppendAsync(bad));
        Assert.Contains("does not balance", e.Message);
        Assert.Equal(100m, await ledger.BalanceAsync($"{u}-bank"));
    }
}
