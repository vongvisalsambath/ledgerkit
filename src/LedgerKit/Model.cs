namespace LedgerKit;

/// <summary>What kind of account: assets and expenses grow with debits; liabilities, equity and income with credits.</summary>
public enum AccountType
{
    /// <summary>What the business owns, such as a bank account. Debits increase it.</summary>
    Asset,
    /// <summary>What it owes. Credits increase it.</summary>
    Liability,
    /// <summary>The owners' share. Credits increase it.</summary>
    Equity,
    /// <summary>Money earned. Credits increase it.</summary>
    Income,
    /// <summary>Money spent. Debits increase it.</summary>
    Expense,
}

/// <summary>An account in the chart of accounts. Every account holds one currency.</summary>
/// <param name="Code">A short code, such as "1010": letters, digits, '.', '_' and '-', at most 32 characters.</param>
/// <param name="Name">What the account is for, such as "Bank: ACLEDA USD".</param>
/// <param name="Type">Asset, liability, equity, income or expense.</param>
/// <param name="Currency">ISO 4217 code, such as "USD" or "KHR".</param>
public sealed record LedgerAccount(string Code, string Name, AccountType Type, string Currency)
{
    /// <summary>True for accounts whose balance is normally a credit (liabilities, equity, income).</summary>
    public bool CreditNormal => Type is AccountType.Liability or AccountType.Equity or AccountType.Income;
}

/// <summary>One line of an entry to post. A positive amount is a debit, a negative amount a credit.</summary>
public sealed record NewLine(string Account, decimal Amount, string? Memo = null)
{
    /// <summary>A debit of <paramref name="amount"/> (which must be positive).</summary>
    public static NewLine Debit(string account, decimal amount, string? memo = null) => new(account, amount, memo);

    /// <summary>A credit of <paramref name="amount"/> (which must be positive).</summary>
    public static NewLine Credit(string account, decimal amount, string? memo = null) => new(account, -amount, memo);
}

/// <summary>An entry to post. Its debits must equal its credits in each currency.</summary>
/// <param name="IdempotencyKey">A key unique to this entry, such as an invoice or payment ID. Posting the same entry with the same key twice records it once.</param>
/// <param name="Date">The accounting date.</param>
/// <param name="Description">What happened.</param>
/// <param name="Lines">At least two lines.</param>
/// <param name="Reference">The reference the bank will show, such as an end-to-end ID: reconciliation matches on it.</param>
public sealed record NewEntry(string IdempotencyKey, DateOnly Date, string Description, IReadOnlyList<NewLine> Lines, string? Reference = null);

/// <summary>A posted entry. Entries are never changed or deleted: a mistake is corrected by reversing the entry.</summary>
public sealed record JournalEntry(
    Guid Id,
    string IdempotencyKey,
    DateOnly Date,
    string Description,
    string? Reference,
    Guid? Reverses,
    IReadOnlyList<JournalLine> Lines,
    string ContentHash);

/// <summary>A posted line. Positive amounts are debits.</summary>
public sealed record JournalLine(int LineNo, string Account, decimal Amount, string Currency, string? Memo);

/// <summary>A posted line with what it needs from its entry, as returned when listing an account.</summary>
public sealed record LedgerLine(Guid EntryId, int LineNo, DateOnly Date, string Account, decimal Amount, string Currency, string Description, string? Reference, string? Memo);

/// <summary>The result of posting: the entry's ID, and whether it had already been posted with the same key.</summary>
public sealed record PostResult(Guid EntryId, bool AlreadyPosted);

/// <summary>One account in a trial balance. <see cref="Balance"/> is debit-positive.</summary>
public sealed record TrialBalanceRow(LedgerAccount Account, decimal Debits, decimal Credits)
{
    /// <summary>Debits minus credits.</summary>
    public decimal Balance => Debits - Credits;
}

/// <summary>Thrown when an account or entry is refused. <see cref="Problems"/> says why, in plain English.</summary>
public sealed class LedgerException(IReadOnlyList<string> problems)
    : Exception(problems.Count == 1 ? problems[0] : $"{problems.Count} problems: " + string.Join(" ", problems))
{
    /// <summary>Every reason, one sentence each.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;

    internal LedgerException(string problem) : this([problem]) { }
}
