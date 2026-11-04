using System.Globalization;

namespace LedgerKit.Reconciliation;

/// <summary>How reconciliation matches lines.</summary>
public sealed record ReconcileOptions
{
    /// <summary>How many days apart a bank line and a ledger line may be dated and still match by amount (0 to 31). Default 3.</summary>
    public int DateWindowDays { get; init; } = 3;

    /// <summary>Whether lines without a common reference may match by amount and date. Default true.</summary>
    public bool MatchByAmountAndDate { get; init; } = true;
}

/// <summary>Why lines were matched.</summary>
public enum MatchRule
{
    /// <summary>The bank's end-to-end ID or reference equals the ledger entry's reference, and the amounts are equal.</summary>
    Reference,
    /// <summary>Several ledger lines with the bank line's reference add up to its amount.</summary>
    ReferenceGroup,
    /// <summary>Same amount and same date, and as many bank lines as ledger lines: they are interchangeable.</summary>
    SameAmountAndDay,
    /// <summary>Same amount, dates within the window, and no other candidate on either side.</summary>
    AmountAndDate,
}

/// <summary>Lines that were matched, and why.</summary>
public sealed record Match(MatchRule Rule, BankLine Bank, IReadOnlyList<LedgerLine> Ledger, string Explanation);

/// <summary>A bank line that could match more than one way, or matches a reference with a different amount. A person decides.</summary>
public sealed record Question(BankLine Bank, IReadOnlyList<LedgerLine> Candidates, string Explanation);

/// <summary>One part of the difference between the statement and the ledger.</summary>
public sealed record DifferencePart(string What, decimal Amount);

/// <summary>The result of reconciling one statement with one ledger account.</summary>
public sealed record ReconciliationReport(
    LedgerAccount Account,
    BankStatement Statement,
    IReadOnlyList<Match> Matched,
    IReadOnlyList<BankLine> UnmatchedBank,
    IReadOnlyList<LedgerLine> UnmatchedLedger,
    IReadOnlyList<Question> Questions,
    decimal LedgerOpening,
    decimal LedgerClosing,
    IReadOnlyList<DifferencePart> Difference)
{
    /// <summary>Statement closing balance minus ledger closing balance; null if the statement has no closing balance.</summary>
    public decimal? ClosingDifference => Statement.Closing is decimal c ? c - LedgerClosing : null;

    /// <summary>True when every line matched, nothing needs a decision, and the closing balances agree.</summary>
    public bool Reconciled => UnmatchedBank.Count == 0 && UnmatchedLedger.Count == 0 && Questions.Count == 0 && ClosingDifference is null or 0m;
}

/// <summary>Matches a bank statement against a ledger account.</summary>
public static class Reconciler
{
    /// <summary>Reconciles <paramref name="statement"/> with ledger account <paramref name="accountCode"/> (the bank account, an asset).</summary>
    public static async Task<ReconciliationReport> ReconcileAsync(Ledger ledger, string accountCode, BankStatement statement, ReconcileOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(statement);
        options ??= new ReconcileOptions();
        if (options.DateWindowDays is < 0 or > 31) throw new LedgerException("The date window must be 0 to 31 days.");
        var account = await ledger.Store.FindAccountAsync(accountCode, ct).ConfigureAwait(false) ?? throw new LedgerException($"There is no account {Rules.Quote(accountCode)}.");
        if (account.Currency != statement.Currency)
            throw new LedgerException($"The statement is in {statement.Currency}, but account {account.Code} holds {account.Currency}.");
        var lines = await ledger.Store.LinesAsync(account.Code, statement.From.AddDays(-options.DateWindowDays), statement.To.AddDays(options.DateWindowDays), ct).ConfigureAwait(false);
        var opening = await ledger.Store.BalanceAsync(account.Code, statement.From.AddDays(-1), ct).ConfigureAwait(false);
        var closing = await ledger.Store.BalanceAsync(account.Code, statement.To, ct).ConfigureAwait(false);
        return Reconcile(account, statement, lines, opening, closing, options);
    }

    /// <summary>
    /// The matching itself, without a store. <paramref name="ledgerLines"/> are the account's lines from the statement's
    /// start minus the date window to its end plus the window; <paramref name="ledgerOpening"/> is the ledger balance at the
    /// end of the day before the statement starts, <paramref name="ledgerClosing"/> at the end of its last day.
    /// </summary>
    public static ReconciliationReport Reconcile(LedgerAccount account, BankStatement statement, IReadOnlyList<LedgerLine> ledgerLines, decimal ledgerOpening, decimal ledgerClosing, ReconcileOptions? options = null)
    {
        options ??= new ReconcileOptions();
        var window = options.DateWindowDays;
        var bankLeft = statement.Lines.ToList();
        var ledgerLeft = ledgerLines.Where(l => l.Account == account.Code).ToList();
        var matched = new List<Match>();
        var questions = new List<Question>();

        // 1. references
        foreach (var b in statement.Lines)
        {
            var refs = Refs(b);
            if (refs.Count == 0) continue;
            var candidates = ledgerLeft.Where(l => l.Reference is not null && refs.Contains(l.Reference.Trim())).ToList();
            if (candidates.Count == 0) continue;
            var exact = candidates.Where(l => l.Amount == b.Amount).ToList();
            if (exact.Count == 1)
                Take(MatchRule.Reference, b, exact, $"reference {exact[0].Reference} and amount {Money(b.Amount)}");
            else if (exact.Count > 1)
                Ask(b, exact, $"{exact.Count} ledger lines have reference {exact[0].Reference} and amount {Money(b.Amount)}.");
            else if (candidates.Sum(l => l.Amount) == b.Amount)
                Take(MatchRule.ReferenceGroup, b, candidates, $"{candidates.Count} ledger lines with reference {candidates[0].Reference} add up to {Money(b.Amount)}");
            else
                Ask(b, candidates, $"The ledger has reference {candidates[0].Reference}, but for {Money(candidates.Sum(l => l.Amount))}, not {Money(b.Amount)}.");
        }

        // 2. interchangeable lines: same amount and day, as many on each side
        foreach (var group in bankLeft.ToList().GroupBy(b => (b.Amount, b.Date)))
        {
            var ledgerSame = ledgerLeft.Where(l => l.Amount == group.Key.Amount && l.Date == group.Key.Date && !Conflicts(group.First(), l)).ToList();
            var bankSame = group.Where(b => ledgerSame.All(l => !Conflicts(b, l))).ToList();
            if (bankSame.Count < 2 || bankSame.Count != ledgerSame.Count || bankSame.Count != group.Count()) continue;
            for (var i = 0; i < bankSame.Count; i++)
                Take(MatchRule.SameAmountAndDay, bankSame[i], [ledgerSame[i]], $"{bankSame.Count} lines of {Money(group.Key.Amount)} on {group.Key.Date:yyyy-MM-dd} on each side");
        }

        // 3. amount and date, only when the pairing is unique both ways
        if (options.MatchByAmountAndDate)
        {
            bool changed;
            do
            {
                changed = false;
                var candidatesOf = bankLeft.ToDictionary(b => b, b => ledgerLeft.Where(l => l.Amount == b.Amount && Math.Abs(l.Date.DayNumber - b.Date.DayNumber) <= window && !Conflicts(b, l)).ToList());
                foreach (var (b, cs) in candidatesOf)
                {
                    if (cs.Count != 1) continue;
                    var l = cs[0];
                    if (candidatesOf.Count(kv => kv.Value.Contains(l)) != 1) continue;
                    var days = Math.Abs(l.Date.DayNumber - b.Date.DayNumber);
                    Take(MatchRule.AmountAndDate, b, [l], $"amount {Money(b.Amount)}, {(days == 0 ? "same day" : days == 1 ? "1 day apart" : $"{days} days apart")}, and no other candidate");
                    changed = true;
                    break;
                }
            } while (changed);

            // what is left with candidates is ambiguous: decide every question first, then hold the candidates aside
            var ambiguous = bankLeft
                .Select(b => (b, cs: ledgerLeft.Where(l => l.Amount == b.Amount && Math.Abs(l.Date.DayNumber - b.Date.DayNumber) <= window && !Conflicts(b, l)).ToList()))
                .Where(x => x.cs.Count > 0)
                .ToList();
            foreach (var (b, cs) in ambiguous)
                Ask(b, cs, $"Several lines of {Money(b.Amount)} within {window} day{(window == 1 ? "" : "s")} could match; nothing tells them apart.");
        }

        // ledger lines outside the statement's period belong to the periods around it unless they matched
        var unmatchedLedger = ledgerLeft.Where(l => l.Date >= statement.From && l.Date <= statement.To).ToList();
        var questioned = questions.SelectMany(q => q.Candidates).ToHashSet();

        var parts = new List<DifferencePart>();
        if (statement.Opening is decimal open && open != ledgerOpening)
            parts.Add(new("Difference already there when the statement starts (opening balances)", open - ledgerOpening));
        var bankOnly = bankLeft.Sum(b => b.Amount) + questions.Sum(q => q.Bank.Amount);
        if (bankOnly != 0) parts.Add(new("On the statement, not matched in the ledger", bankOnly));
        var ledgerOnly = unmatchedLedger.Sum(l => l.Amount) + questioned.Where(l => l.Date >= statement.From && l.Date <= statement.To).Sum(l => l.Amount);
        if (ledgerOnly != 0) parts.Add(new("In the ledger, not matched on the statement", -ledgerOnly));
        var timing = matched.SelectMany(m => m.Ledger).Where(l => l.Date < statement.From || l.Date > statement.To).Sum(l => l.Amount);
        if (timing != 0) parts.Add(new("Matched, but dated outside the statement's period in the ledger", timing));

        return new ReconciliationReport(account, statement, matched, bankLeft, unmatchedLedger, questions, ledgerOpening, ledgerClosing, parts);

        void Take(MatchRule rule, BankLine b, List<LedgerLine> ls, string why)
        {
            matched.Add(new Match(rule, b, ls, why));
            bankLeft.Remove(b);
            foreach (var l in ls) ledgerLeft.Remove(l);
        }

        // a bank line a person must decide on; its candidates are held aside so they can't also match something else
        void Ask(BankLine b, List<LedgerLine> cs, string why)
        {
            questions.Add(new Question(b, cs, why));
            bankLeft.Remove(b);
            foreach (var l in cs) ledgerLeft.Remove(l);
        }
    }

    private static HashSet<string> Refs(BankLine b)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(b.EndToEndId)) refs.Add(b.EndToEndId.Trim());
        if (!string.IsNullOrWhiteSpace(b.Reference)) refs.Add(b.Reference.Trim());
        return refs;
    }

    /// <summary>A ledger line whose reference names a different payment than the bank line's can't match it by amount.</summary>
    private static bool Conflicts(BankLine b, LedgerLine l)
    {
        if (string.IsNullOrWhiteSpace(l.Reference)) return false;
        var refs = Refs(b);
        return refs.Count > 0 && !refs.Contains(l.Reference.Trim());
    }

    private static string Money(decimal d) => d.ToString("#,0.00######", CultureInfo.InvariantCulture);
}
