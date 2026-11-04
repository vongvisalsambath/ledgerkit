using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LedgerKit.Reconciliation;

namespace LedgerKit.Cli;

/// <summary>Writes reconciliation reports. Every piece of text from a statement or the ledger is treated as hostile.</summary>
internal static class Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string M(decimal d) => d.ToString("#,0.00######", Inv);

    public static string Text(IReadOnlyList<ReconciliationReport> reports)
    {
        var sb = new StringBuilder();
        foreach (var r in reports)
        {
            var s = r.Statement;
            sb.AppendLine($"{(r.Reconciled ? "RECONCILED" : "NOT RECONCILED")}  statement {Safe.Text(s.Id)} ({Safe.Text(s.Iban ?? s.OtherAccountId)}) with account {Safe.Text(r.Account.Code)}, {s.From:yyyy-MM-dd} to {s.To:yyyy-MM-dd}, {s.Currency}");
            foreach (var note in s.Notes) sb.AppendLine("  note: " + Safe.Text(note));
            sb.AppendLine();
            sb.AppendLine($"  Balances            statement          ledger      difference");
            if (s.Opening is decimal o) sb.AppendLine($"  Opening       {M(o),15} {M(r.LedgerOpening),15} {M(o - r.LedgerOpening),15}");
            if (s.Closing is decimal c) sb.AppendLine($"  Closing       {M(c),15} {M(r.LedgerClosing),15} {M(c - r.LedgerClosing),15}");
            if (r.Difference.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  Why they differ");
                foreach (var d in r.Difference) sb.AppendLine($"    {M(d.Amount),15}  {d.What}");
            }

            Section(sb, $"Matched ({r.Matched.Count})", r.Matched.Select(m =>
                $"{m.Bank.Date:yyyy-MM-dd} {M(m.Bank.Amount),14}  {Safe.Text(m.Bank.Id, 40)}  ↔  {string.Join(", ", m.Ledger.Select(l => $"{l.Date:yyyy-MM-dd} {Safe.Text(l.Reference ?? l.Description, 40)}"))}  ({Safe.Text(m.Explanation)})"));
            Section(sb, $"On the statement, not in the ledger ({r.UnmatchedBank.Count})", r.UnmatchedBank.Select(b =>
                $"{b.Date:yyyy-MM-dd} {M(b.Amount),14}  {Safe.Text(b.Id, 40)}  {Safe.Text(b.EndToEndId ?? "", 40)}  {Safe.Text(b.Counterparty ?? "", 40)}  {Safe.Text(b.Description ?? "", 60)}"));
            Section(sb, $"In the ledger, not on the statement ({r.UnmatchedLedger.Count})", r.UnmatchedLedger.Select(l =>
                $"{l.Date:yyyy-MM-dd} {M(l.Amount),14}  {Safe.Text(l.Reference ?? "", 40)}  {Safe.Text(l.Description, 60)}  entry {l.EntryId}"));
            Section(sb, $"Needs a decision ({r.Questions.Count})", r.Questions.Select(q =>
                $"{q.Bank.Date:yyyy-MM-dd} {M(q.Bank.Amount),14}  {Safe.Text(q.Bank.Id, 40)}: {Safe.Text(q.Explanation)} Candidates: {string.Join("; ", q.Candidates.Select(l => $"{l.Date:yyyy-MM-dd} {M(l.Amount)} {Safe.Text(l.Reference ?? l.Description, 40)}"))}"));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("  " + title);
        foreach (var l in list) sb.AppendLine("    " + l);
    }

    public static string Json(IReadOnlyList<ReconciliationReport> reports)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Default };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Serialize(reports.Select(r => new
        {
            reconciled = r.Reconciled,
            account = r.Account.Code,
            statement = new { r.Statement.Id, r.Statement.Iban, r.Statement.Currency, from = r.Statement.From, to = r.Statement.To, opening = r.Statement.Opening, closing = r.Statement.Closing, notes = r.Statement.Notes },
            ledger = new { opening = r.LedgerOpening, closing = r.LedgerClosing },
            closingDifference = r.ClosingDifference,
            difference = r.Difference,
            matched = r.Matched.Select(m => new { rule = m.Rule, bank = m.Bank, ledger = m.Ledger, m.Explanation }),
            unmatchedBank = r.UnmatchedBank,
            unmatchedLedger = r.UnmatchedLedger,
            questions = r.Questions,
        }), options) + Environment.NewLine;
    }

    public static string Csv(IReadOnlyList<ReconciliationReport> reports) => ReconciliationCsv.Write(reports);
}
