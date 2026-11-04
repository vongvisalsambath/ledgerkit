using System.Globalization;
using System.Text;

namespace LedgerKit.Reconciliation;

/// <summary>Writes reconciliation reports as CSV, one row per line, safe to open in a spreadsheet.</summary>
public static class ReconciliationCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The CSV header and rows: matched, bank only, ledger only, and lines that need a decision.</summary>
    public static string Write(IEnumerable<ReconciliationReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var sb = new StringBuilder("statement,status,rule,bank_id,bank_date,bank_amount,ledger_date,ledger_amount,ledger_reference,ledger_entry,explanation\r\n");
        foreach (var r in reports)
        {
            foreach (var m in r.Matched) foreach (var l in m.Ledger) Row(sb, r, "matched", m.Rule.ToString(), m.Bank, l, m.Explanation);
            foreach (var b in r.UnmatchedBank) Row(sb, r, "bank only", "", b, null, b.Description ?? "");
            foreach (var l in r.UnmatchedLedger) Row(sb, r, "ledger only", "", null, l, l.Description);
            foreach (var q in r.Questions)
                if (q.Candidates.Count == 0) Row(sb, r, "decide", "", q.Bank, null, q.Explanation);
                else foreach (var l in q.Candidates) Row(sb, r, "decide", "", q.Bank, l, q.Explanation);
        }
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, ReconciliationReport r, string status, string rule, BankLine? b, LedgerLine? l, string explanation) =>
        sb.Append(string.Join(',',
            Cell(r.Statement.Id), Cell(status), Cell(rule), Cell(b?.Id), b is null ? "" : b.Date.ToString("yyyy-MM-dd", Inv), b is null ? "" : Number(b.Amount),
            l is null ? "" : l.Date.ToString("yyyy-MM-dd", Inv), l is null ? "" : Number(l.Amount), Cell(l?.Reference), l is null ? "" : l.EntryId.ToString(), Cell(explanation))).Append("\r\n");

    private static string Number(decimal d) => d.ToString("0.00######", Inv);

    /// <summary>
    /// A text cell: quoted, with control and direction characters written as \uXXXX, and an apostrophe before a leading
    /// =, +, -, @, tab or carriage return so a spreadsheet shows the text instead of running it as a formula (CSV injection).
    /// </summary>
    public static string Cell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        var n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (++n > 500) { sb.Append('…'); break; }
            var c = r.Value;
            sb.Append(Rune.IsControl(r) || c is 0x061C or 0x200B or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF ? $"\\u{c:X4}" : r.ToString());
        }
        var text = sb.ToString();
        if (text[0] is '=' or '+' or '-' or '@' || s[0] is '\t' or '\r') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
