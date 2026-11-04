using System.Globalization;
using LedgerKit.Reconciliation;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace LedgerKit.Web.Pages;

public partial class Home
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxLedgerLines = 5000;
    private const long MaxStatementBytes = 5 * 1024 * 1024;
    private static readonly LedgerAccount Bank = new("1010", "Bank", AccountType.Asset, "EUR");

    private const string SampleLedger = """
        2026-11-01,,10000.00,Opening balance
        2026-11-03,ORDER-7781,1500.00,Order 7781: 2 bikes
        2026-11-04,RENT-NOV,-250.50,Rent November
        2026-11-04,CHQ-1001,-300.00,Cheque 1001 to a supplier
        """;

    private string statementXml = "";
    private string ledgerCsv = SampleLedger;
    private int window = 3;
    private List<string> problems = [];
    private List<ReconciliationReport> reports = [];

    protected override void OnInitialized()
    {
        statementXml = Sample();
        Changed();
    }

    private static string Sample()
    {
        using var s = typeof(Home).Assembly.GetManifestResourceStream("sample-statement.xml")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void Reset()
    {
        statementXml = Sample();
        ledgerCsv = SampleLedger;
        window = 3;
        Changed();
    }

    private async Task OpenStatement(InputFileChangeEventArgs e)
    {
        if (e.File.Size > MaxStatementBytes)
        {
            problems = ["That file is over 5 MB; this page reads statements up to 5 MB. The ledgerkit command reads up to 50 MB."];
            reports = [];
            return;
        }
        using var reader = new StreamReader(e.File.OpenReadStream(MaxStatementBytes));
        statementXml = await reader.ReadToEndAsync();
        Changed();
    }

    private void Changed()
    {
        problems = [];
        reports = [];
        if (string.IsNullOrWhiteSpace(statementXml)) { problems.Add("Paste a camt.053 statement, or open one."); return; }
        if (statementXml.Length > MaxStatementBytes) { problems.Add("The statement is over 5 MB."); return; }

        var import = StatementImport.FromCamt053(statementXml);
        problems.AddRange(import.Problems);
        var lines = ReadLedger(ledgerCsv, problems);
        if (problems.Count > 0) return;

        foreach (var st in import.Statements)
        {
            var account = Bank with { Currency = st.Currency };
            var relevant = lines.Select(l => l with { Currency = st.Currency }).ToList();
            var opening = relevant.Where(l => l.Date < st.From).Sum(l => l.Amount);
            var closing = relevant.Where(l => l.Date <= st.To).Sum(l => l.Amount);
            var inWindow = relevant.Where(l => l.Date >= st.From.AddDays(-window) && l.Date <= st.To.AddDays(window)).ToList();
            reports.Add(Reconciler.Reconcile(account, st, inWindow, opening, closing, new ReconcileOptions { DateWindowDays = window }));
        }
    }

    /// <summary>date,reference,amount,description: the description is the rest of the line, so it may contain commas.</summary>
    private static List<LedgerLine> ReadLedger(string csv, List<string> problems)
    {
        var lines = new List<LedgerLine>();
        var rows = csv.Split('\n');
        if (rows.Length > MaxLedgerLines) { problems.Add($"The ledger has more than {MaxLedgerLines:N0} lines."); return lines; }
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(row)) continue;
            var cells = row.Split(',', 4);
            var where = $"Ledger line {i + 1}";
            if (row.Length > 500) { problems.Add($"{where} is longer than 500 characters."); continue; }
            if (cells.Length < 3) { problems.Add($"{where} needs a date, a reference (or nothing) and an amount, separated by commas."); continue; }
            if (!DateOnly.TryParseExact(cells[0].Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var date)) { problems.Add($"{where}: the date should look like 2026-11-04."); continue; }
            if (!decimal.TryParse(cells[2].Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var amount) || amount == 0 || Math.Abs(amount) > Rules.MaxAmount)
            { problems.Add($"{where}: the amount should be a number like 1500.00 or -250.50, not zero."); continue; }
            var reference = cells[1].Trim();
            var description = cells.Length > 3 ? cells[3].Trim() : "";
            lines.Add(new LedgerLine(Guid.NewGuid(), 1, date, "1010", amount, "EUR", description.Length == 0 ? "(no description)" : description, reference.Length == 0 ? null : reference, null));
            if (problems.Count >= 20) break;
        }
        return lines;
    }

    private async Task Download()
    {
        if (reports.Count == 0) return;
        await JS.InvokeVoidAsync("ledgerkitDownload", "reconciliation.csv", ReconciliationCsv.Write(reports));
    }

    private static string M(decimal d) => d.ToString("#,0.00######", Inv);
}
