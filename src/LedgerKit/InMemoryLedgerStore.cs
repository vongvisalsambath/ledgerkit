namespace LedgerKit;

/// <summary>A ledger kept in memory, for tests, demos and short-lived work. Safe to use from several threads.</summary>
public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, LedgerAccount> accounts = new(StringComparer.Ordinal);
    private readonly List<JournalEntry> entries = [];
    private readonly Dictionary<string, JournalEntry> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, JournalEntry> byId = [];
    private readonly HashSet<Guid> reversed = [];

    /// <inheritdoc />
    public Task AddAccountAsync(LedgerAccount account, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (accounts.TryGetValue(account.Code, out var existing))
            {
                if (existing != account) throw new LedgerException($"An account {Rules.Quote(account.Code)} already exists with a different name, type or currency.");
                return Task.CompletedTask;
            }
            accounts.Add(account.Code, account);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LedgerAccount?> FindAccountAsync(string code, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(accounts.GetValueOrDefault(code));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LedgerAccount>> AccountsAsync(CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<LedgerAccount>>(accounts.Values.OrderBy(a => a.Code, StringComparer.Ordinal).ToList());
    }

    /// <inheritdoc />
    public Task<PostResult> AppendAsync(JournalEntry entry, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (byKey.TryGetValue(entry.IdempotencyKey, out var existing))
            {
                if (existing.ContentHash != entry.ContentHash)
                    throw new LedgerException($"The idempotency key {Rules.Quote(entry.IdempotencyKey)} was already used for a different entry.");
                return Task.FromResult(new PostResult(existing.Id, true));
            }
            if (entry.Reverses is Guid r && !reversed.Add(r)) throw new LedgerException("That entry has already been reversed.");
            entries.Add(entry);
            byKey.Add(entry.IdempotencyKey, entry);
            byId.Add(entry.Id, entry);
            return Task.FromResult(new PostResult(entry.Id, false));
        }
    }

    /// <inheritdoc />
    public Task<JournalEntry?> FindEntryAsync(Guid id, CancellationToken ct = default)
    {
        lock (gate) return Task.FromResult(byId.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task<decimal> BalanceAsync(string account, DateOnly? asOf = null, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult(entries.Where(e => asOf is null || e.Date <= asOf).SelectMany(e => e.Lines).Where(l => l.Account == account).Sum(l => l.Amount));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LedgerLine>> LinesAsync(string account, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<LedgerLine>>(entries
                .Select((e, order) => (e, order))
                .Where(x => x.e.Date >= from && x.e.Date <= to)
                .OrderBy(x => x.e.Date).ThenBy(x => x.order)
                .SelectMany(x => x.e.Lines.Where(l => l.Account == account)
                    .Select(l => new LedgerLine(x.e.Id, l.LineNo, x.e.Date, l.Account, l.Amount, l.Currency, x.e.Description, x.e.Reference, l.Memo)))
                .ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        lock (gate)
        {
            var lines = entries.Where(e => asOf is null || e.Date <= asOf).SelectMany(e => e.Lines).ToLookup(l => l.Account, StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<TrialBalanceRow>>(accounts.Values.OrderBy(a => a.Code, StringComparer.Ordinal)
                .Select(a => new TrialBalanceRow(a, lines[a.Code].Where(l => l.Amount > 0).Sum(l => l.Amount), -lines[a.Code].Where(l => l.Amount < 0).Sum(l => l.Amount)))
                .ToList());
        }
    }
}
