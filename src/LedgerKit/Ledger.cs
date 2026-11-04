namespace LedgerKit;

/// <summary>Where a ledger is kept. Implementations only store and query: <see cref="Ledger"/> checks everything first.</summary>
public interface ILedgerStore
{
    /// <summary>Adds an account. Adding the same account again does nothing; adding a different one with the same code throws <see cref="LedgerException"/>.</summary>
    Task AddAccountAsync(LedgerAccount account, CancellationToken ct = default);
    /// <summary>The account with this code, or null.</summary>
    Task<LedgerAccount?> FindAccountAsync(string code, CancellationToken ct = default);
    /// <summary>Every account, by code.</summary>
    Task<IReadOnlyList<LedgerAccount>> AccountsAsync(CancellationToken ct = default);
    /// <summary>
    /// Appends a checked entry, atomically. If an entry with the same idempotency key exists, returns it with
    /// <see cref="PostResult.AlreadyPosted"/> when its content is the same, and throws <see cref="LedgerException"/> when it differs.
    /// Throws <see cref="LedgerException"/> if the entry reverses one that is already reversed.
    /// </summary>
    Task<PostResult> AppendAsync(JournalEntry entry, CancellationToken ct = default);
    /// <summary>The entry with this ID, or null.</summary>
    Task<JournalEntry?> FindEntryAsync(Guid id, CancellationToken ct = default);
    /// <summary>The sum of an account's lines up to and including <paramref name="asOf"/> (all of them if null); debit-positive.</summary>
    Task<decimal> BalanceAsync(string account, DateOnly? asOf = null, CancellationToken ct = default);
    /// <summary>An account's lines dated from <paramref name="from"/> to <paramref name="to"/>, in date and posting order.</summary>
    Task<IReadOnlyList<LedgerLine>> LinesAsync(string account, DateOnly from, DateOnly to, CancellationToken ct = default);
    /// <summary>Debits and credits of every account up to <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(DateOnly? asOf = null, CancellationToken ct = default);
}

/// <summary>
/// A double-entry ledger. Every entry is checked before it reaches the store: it must balance in each currency, use
/// existing accounts in their own currency, and respect each currency's decimals. Entries are never changed or deleted;
/// <see cref="ReverseAsync"/> corrects one.
/// </summary>
public sealed class Ledger(ILedgerStore store)
{
    /// <summary>The store this ledger uses.</summary>
    public ILedgerStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Adds an account, after checking it.</summary>
    public async Task AddAccountAsync(LedgerAccount account, CancellationToken ct = default)
    {
        var problems = Rules.Check(account);
        if (problems.Count > 0) throw new LedgerException(problems);
        await Store.AddAccountAsync(account, ct).ConfigureAwait(false);
    }

    /// <summary>Posts an entry. Posting the same entry again with the same key returns the first one, so retries are safe.</summary>
    public async Task<PostResult> PostAsync(NewEntry entry, CancellationToken ct = default) => await PostAsync(entry, null, ct).ConfigureAwait(false);

    private async Task<PostResult> PostAsync(NewEntry entry, Guid? reverses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var accounts = new Dictionary<string, LedgerAccount?>(StringComparer.Ordinal);
        foreach (var code in (entry.Lines ?? []).Where(l => l?.Account is not null).Select(l => l.Account).Distinct(StringComparer.Ordinal).Take(Rules.MaxLines + 1))
            accounts[code] = await Store.FindAccountAsync(code, ct).ConfigureAwait(false);
        var problems = Rules.Check(entry, code => accounts.GetValueOrDefault(code));
        if (problems.Count > 0) throw new LedgerException(problems);

        var lines = entry.Lines!.Select((l, i) => new JournalLine(i + 1, l.Account, l.Amount, accounts[l.Account]!.Currency, l.Memo)).ToList();
        var posted = new JournalEntry(Guid.NewGuid(), entry.IdempotencyKey, entry.Date, entry.Description, entry.Reference, reverses, lines,
            Rules.Hash(entry.Date, entry.Description, entry.Reference, reverses, lines));
        return await Store.AppendAsync(posted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Corrects an entry by posting its opposite, dated <paramref name="date"/>. An entry can be reversed once, and a
    /// reversal can't itself be reversed: post a new entry instead.
    /// </summary>
    public async Task<PostResult> ReverseAsync(Guid entryId, DateOnly date, string idempotencyKey, string? description = null, CancellationToken ct = default)
    {
        var original = await Store.FindEntryAsync(entryId, ct).ConfigureAwait(false) ?? throw new LedgerException($"There is no entry {entryId}.");
        if (original.Reverses is not null) throw new LedgerException("This entry is itself a reversal; post a new entry instead of reversing it.");
        if (date < original.Date) throw new LedgerException($"A reversal can't be dated before the entry it reverses ({original.Date:yyyy-MM-dd}).");
        var lines = original.Lines.Select(l => new NewLine(l.Account, -l.Amount, l.Memo)).ToList();
        var text = description ?? Shorten("Reversal of: " + original.Description, Rules.MaxDescription);
        return await PostAsync(new NewEntry(idempotencyKey, date, text, lines, original.Reference), original.Id, ct).ConfigureAwait(false);
    }

    /// <summary>An account's balance as of a date, debit-positive.</summary>
    public Task<decimal> BalanceAsync(string account, DateOnly? asOf = null, CancellationToken ct = default) => Store.BalanceAsync(account, asOf, ct);

    /// <summary>A trial balance: debits must equal credits in each currency.</summary>
    public Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(DateOnly? asOf = null, CancellationToken ct = default) => Store.TrialBalanceAsync(asOf, ct);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
}
