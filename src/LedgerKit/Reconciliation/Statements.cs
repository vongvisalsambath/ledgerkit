using Iso20022Kit;

namespace LedgerKit.Reconciliation;

/// <summary>One booked line of a bank statement. Money in is positive.</summary>
public sealed record BankLine(
    string Id,
    DateOnly Date,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? Reference,
    string? EndToEndId,
    string? Counterparty,
    string? Description);

/// <summary>A statement for one account and period, as reconciliation needs it.</summary>
public sealed record BankStatement(
    string Id,
    string? Iban,
    string? OtherAccountId,
    string Currency,
    DateOnly From,
    DateOnly To,
    decimal? Opening,
    decimal? Closing,
    IReadOnlyList<BankLine> Lines,
    IReadOnlyList<string> Notes);

/// <summary>The statements read from a file, or why they couldn't be.</summary>
public sealed record ImportResult(IReadOnlyList<BankStatement> Statements, IReadOnlyList<string> Problems)
{
    /// <summary>True when the file was read and every statement could be used.</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>Reads bank statements for reconciliation.</summary>
public static class StatementImport
{
    /// <summary>
    /// Reads an ISO 20022 camt.053.001.08 statement with Iso20022Kit. A message that fails the schema or the statement's
    /// own checks (its balances must follow from its entries) is refused: reconciling against a wrong statement would
    /// only move the error. Pending entries are left out, because they aren't booked yet.
    /// </summary>
    public static ImportResult FromCamt053(Stream xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return Convert(Iso20022.ReadBankStatement(xml));
    }

    /// <summary>Reads a camt.053 statement from text. Prefer the stream overload for files.</summary>
    public static ImportResult FromCamt053(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        return Convert(Iso20022.ReadBankStatement(xml));
    }

    private static ImportResult Convert(ReadResult<Iso20022Kit.Model.BankStatement> read)
    {
        if (!read.IsValid || read.Value is null)
            return new([], read.Report.Errors.Select(e => $"The statement isn't valid ({e.Code}{(e.Line is int l ? $", line {l}" : "")}): {e.Message}").Take(20).ToList());

        var statements = new List<BankStatement>();
        var problems = new List<string>();
        foreach (var s in read.Value.Statements)
        {
            var notes = new List<string>();
            var booked = s.Entries.Where(e => e.Status == "BOOK").ToList();
            var pending = s.Entries.Count - booked.Count;
            if (pending > 0) notes.Add($"{pending} entr{(pending == 1 ? "y isn't" : "ies aren't")} booked yet (status {string.Join(", ", s.Entries.Where(e => e.Status != "BOOK").Select(e => e.Status).Distinct())}) and {(pending == 1 ? "was" : "were")} left out.");

            var currencies = booked.Select(e => e.Amount.Currency)
                .Concat(new[] { s.Opening?.Amount.Currency, s.Closing?.Amount.Currency, s.Account.Currency }.OfType<string>())
                .Distinct(StringComparer.Ordinal).ToList();
            if (currencies.Count != 1)
            {
                problems.Add(currencies.Count == 0
                    ? $"Statement {s.Id} has no currency: no balances and no booked entries."
                    : $"Statement {s.Id} mixes currencies ({string.Join(", ", currencies)}); a ledger account holds one.");
                continue;
            }

            var lines = new List<BankLine>();
            var undated = 0;
            for (var i = 0; i < booked.Count; i++)
            {
                var e = booked[i];
                var date = e.BookingDate ?? e.ValueDate;
                if (date is null) { undated++; continue; }
                var e2e = e.EndToEndId is null || e.EndToEndId.Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase) ? null : e.EndToEndId;
                lines.Add(new BankLine(e.Reference ?? $"{s.Id}/{i + 1}", date.Value, e.ValueDate, e.Amount.Amount, e.Amount.Currency, e.Reference, e2e, e.Counterparty,
                    e.Description ?? (e.RemittanceInformation.Count > 0 ? string.Join(" ", e.RemittanceInformation) : null)));
            }
            if (undated > 0) problems.Add($"Statement {s.Id} has {undated} booked entr{(undated == 1 ? "y" : "ies")} with no booking or value date.");

            var duplicates = lines.Where(l => l.Reference is not null).GroupBy(l => l.Reference!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0) notes.Add($"The bank's reference {string.Join(", ", duplicates.Take(5))} appears on more than one line.");

            var from = Day(s.From) ?? s.Opening?.Date ?? lines.Select(l => (DateOnly?)l.Date).Min();
            var to = Day(s.To) ?? s.Closing?.Date ?? lines.Select(l => (DateOnly?)l.Date).Max();
            if (from is null || to is null) { problems.Add($"Statement {s.Id} has no period: no dates on it at all."); continue; }
            if (to < from) { problems.Add($"Statement {s.Id} ends ({to:yyyy-MM-dd}) before it starts ({from:yyyy-MM-dd})."); continue; }

            statements.Add(new BankStatement(s.Id, s.Account.Iban, s.Account.Other, currencies[0], from.Value, to.Value, s.Opening?.Amount.Amount, s.Closing?.Amount.Amount, lines, notes));
        }
        if (statements.Count == 0 && problems.Count == 0) problems.Add("The message holds no statements.");
        return new(statements, problems);
    }

    private static DateOnly? Day(DateTimeOffset? d) => d is null ? null : DateOnly.FromDateTime(d.Value.DateTime);
}
