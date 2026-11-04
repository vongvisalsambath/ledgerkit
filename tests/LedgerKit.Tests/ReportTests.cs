using LedgerKit.Cli;
using LedgerKit.Reconciliation;
using Xunit;

namespace LedgerKit.Tests;

/// <summary>Text from statements and ledgers reaches terminals and spreadsheets; neither may be able to act on it.</summary>
public sealed class ReportTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("+1+2", "\"'+1+2\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tcmd", "\"'\\u0009cmd\"")]
    [InlineData("Rent, November", "\"Rent, November\"")]
    [InlineData("line\r\nbreak", "\"line\\u000D\\u000Abreak\"")]
    public void CsvCellsCantBecomeFormulas(string text, string cell) => Assert.Equal(cell, Report.Cell(text));

    [Fact]
    public void TerminalEscapesAndDirectionMarksArePrintedAsCodes()
    {
        Assert.Equal("Fee \\u009B31m\\u202Etxt \\u001B]0;x\\u0007", Safe.Text("Fee \u009B31m\u202Etxt \u001B]0;x\u0007"));
        Assert.Equal(201, Safe.Text(new string('x', 5000)).Length);
    }

    [Fact]
    public void AReportOfHostileTextHasNoControlCharacters()
    {
        var account = new LedgerAccount("1010", "Bank", AccountType.Asset, "EUR");
        var evil = "\u001B[2J\u202Eevil\u0085";
        var bank = new BankLine(evil, new DateOnly(2026, 11, 4), null, 5m, "EUR", evil, evil, evil, evil);
        var ledgerLine = new LedgerLine(Guid.NewGuid(), 1, new DateOnly(2026, 11, 4), "1010", -3m, "EUR", evil, evil, evil);
        var st = new BankStatement(evil, evil, null, "EUR", new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4), 0m, 5m, [bank], [evil]);
        var r = Reconciler.Reconcile(account, st, [ledgerLine], 0m, -3m);
        foreach (var output in new[] { Report.Text([r]), Report.Csv([r]), Report.Json([r]) })
            Assert.DoesNotContain(output, c => (char.IsControl(c) && c is not '\n' and not '\r') || c is '\u202E' or '\u0085');
    }
}
