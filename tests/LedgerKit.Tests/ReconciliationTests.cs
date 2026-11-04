using LedgerKit.Reconciliation;
using Xunit;

namespace LedgerKit.Tests;

public sealed class ReconciliationTests
{
    private static readonly LedgerAccount Bank = new("1010", "Bank: Demo EUR", AccountType.Asset, "EUR");
    private static readonly DateOnly Nov4 = new(2026, 11, 4);

    private static BankLine B(string id, decimal amount, DateOnly? date = null, string? reference = null, string? e2e = null) =>
        new(id, date ?? Nov4, null, amount, "EUR", reference, e2e, null, null);

    private static LedgerLine L(decimal amount, DateOnly? date = null, string? reference = null) =>
        new(Guid.NewGuid(), 1, date ?? Nov4, "1010", amount, "EUR", "x", reference, null);

    private static BankStatement S(params BankLine[] lines) =>
        new("S1", null, null, "EUR", Nov4, Nov4, 0m, lines.Sum(l => l.Amount), lines, []);

    private static ReconciliationReport Run(BankStatement s, params LedgerLine[] ledger) =>
        Reconciler.Reconcile(Bank, s, ledger, 0m, ledger.Where(l => l.Date <= s.To).Sum(l => l.Amount));

    [Fact]
    public async Task TheSampleStatementAgainstALedger()
    {
        await using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "statement-eur.xml"));
        var import = StatementImport.FromCamt053(file);
        Assert.True(import.Ok, string.Join(" ", import.Problems));
        var st = Assert.Single(import.Statements);
        Assert.Equal(("EUR", "DE89370400440532013000", Nov4, Nov4, 10000m, 11200m), (st.Currency, st.Iban, st.From, st.To, st.Opening, st.Closing));
        Assert.Equal([1500m, -250.50m, -49.50m], st.Lines.Select(l => l.Amount));
        Assert.Equal("ORDER-7781", st.Lines[0].EndToEndId);

        var ledger = new Ledger(new InMemoryLedgerStore());
        await ledger.AddAccountAsync(Bank);
        await ledger.AddAccountAsync(new("3000", "Equity", AccountType.Equity, "EUR"));
        await ledger.AddAccountAsync(new("4000", "Sales", AccountType.Income, "EUR"));
        await ledger.AddAccountAsync(new("6100", "Rent", AccountType.Expense, "EUR"));
        await ledger.AddAccountAsync(new("2100", "Cheques issued", AccountType.Liability, "EUR"));
        await ledger.PostAsync(new("open", new(2026, 11, 1), "Opening balance", [NewLine.Debit("1010", 10000m), NewLine.Credit("3000", 10000m)]));
        // the sale was booked on the 3rd; the bank credited it on the 4th
        await ledger.PostAsync(new("order-7781", new(2026, 11, 3), "Order 7781", [NewLine.Debit("1010", 1500m), NewLine.Credit("4000", 1500m)], "ORDER-7781"));
        await ledger.PostAsync(new("rent-nov", Nov4, "Rent November", [NewLine.Debit("6100", 250.50m), NewLine.Credit("1010", 250.50m)], "RENT-NOV"));
        // a cheque the bank hasn't cashed yet
        await ledger.PostAsync(new("chq-1001", Nov4, "Cheque 1001", [NewLine.Debit("2100", 300m), NewLine.Credit("1010", 300m)], "CHQ-1001"));

        var r = await Reconciler.ReconcileAsync(ledger, "1010", st);
        Assert.Equal(["ORDER-7781", "RENT-NOV"], r.Matched.Select(m => m.Ledger[0].Reference));
        Assert.All(r.Matched, m => Assert.Equal(MatchRule.Reference, m.Rule));
        Assert.Equal(-49.50m, Assert.Single(r.UnmatchedBank).Amount);              // the bank's fee: not in the ledger yet
        Assert.Equal("CHQ-1001", Assert.Single(r.UnmatchedLedger).Reference);      // the cheque: not cashed yet
        Assert.Empty(r.Questions);
        Assert.False(r.Reconciled);
        Assert.Equal(11500m, r.LedgerOpening);
        Assert.Equal(10949.50m, r.LedgerClosing);
        Assert.Equal(250.50m, r.ClosingDifference);
        Assert.Equal(
            [("Difference already there when the statement starts (opening balances)", -1500m),
             ("On the statement, not matched in the ledger", -49.50m),
             ("In the ledger, not matched on the statement", 300m),
             ("Matched, but dated outside the statement's period in the ledger", 1500m)],
            r.Difference.Select(d => (d.What, d.Amount)));
        Assert.Equal(r.ClosingDifference, r.Difference.Sum(d => d.Amount));
    }

    [Fact]
    public void AnInvalidStatementIsRefused()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "statement-eur.xml")).Replace("<Amt Ccy=\"EUR\">11200.00</Amt>", "<Amt Ccy=\"EUR\">11300.00</Amt>");
        var import = StatementImport.FromCamt053(xml);
        Assert.False(import.Ok);
        Assert.Contains(import.Problems, p => p.Contains("CAMT053-BALANCE", StringComparison.Ordinal));
        Assert.Empty(import.Statements);
        Assert.False(StatementImport.FromCamt053("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>").Ok);
        Assert.False(StatementImport.FromCamt053("not xml").Ok);
    }

    [Fact]
    public void ReferenceWithTheSameAmountMatches_ADifferentAmountIsAQuestion()
    {
        var r = Run(S(B("b1", 100m, e2e: "INV-1"), B("b2", 50m, reference: "INV-2")), L(100m, reference: "inv-1"), L(45m, reference: "INV-2"));
        Assert.Equal(MatchRule.Reference, Assert.Single(r.Matched).Rule);
        var q = Assert.Single(r.Questions);
        Assert.Contains("for 45.00, not 50.00", q.Explanation);
        Assert.Empty(r.UnmatchedLedger); // the 45.00 is held with the question, not reported twice
    }

    [Fact]
    public void SeveralLedgerLinesCanMakeUpOneBankLine()
    {
        var r = Run(S(B("b1", 300m, e2e: "BATCH-9")), L(100m, reference: "BATCH-9"), L(200m, reference: "BATCH-9"));
        var m = Assert.Single(r.Matched);
        Assert.Equal(MatchRule.ReferenceGroup, m.Rule);
        Assert.Equal(2, m.Ledger.Count);
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void InterchangeableLinesArePaired()
    {
        var r = Run(S(B("b1", 4.5m), B("b2", 4.5m), B("b3", 4.5m)), L(4.5m), L(4.5m), L(4.5m));
        Assert.Equal(3, r.Matched.Count);
        Assert.All(r.Matched, m => Assert.Equal(MatchRule.SameAmountAndDay, m.Rule));
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void AmountAndDateMatchOnlyWhenUnique()
    {
        var r = Run(S(B("b1", 99m)), L(99m, Nov4.AddDays(-2)));
        Assert.Equal(MatchRule.AmountAndDate, Assert.Single(r.Matched).Rule);
        Assert.Contains("2 days apart", r.Matched[0].Explanation);

        // two ledger lines could be it: nothing tells them apart, so a person decides
        var two = Run(S(B("b1", 99m)), L(99m, Nov4.AddDays(-1)), L(99m, Nov4.AddDays(-2)));
        Assert.Empty(two.Matched);
        Assert.Equal(2, Assert.Single(two.Questions).Candidates.Count);

        // outside the window: no match
        var far = Run(S(B("b1", 99m)), L(99m, Nov4.AddDays(-5)));
        Assert.Empty(far.Matched);
        Assert.Single(far.UnmatchedBank);

        // a ledger line that names a different payment never matches by amount
        var named = Run(S(B("b1", 99m, e2e: "INV-1")), L(99m, reference: "INV-2"));
        Assert.Empty(named.Matched);
    }

    [Fact]
    public void MatchingByAmountCanBeSwitchedOff()
    {
        var r = Reconciler.Reconcile(Bank, S(B("b1", 99m)), [L(99m)], 0m, 99m, new ReconcileOptions { MatchByAmountAndDate = false });
        Assert.Empty(r.Matched);
    }

    [Fact]
    public async Task WrongCurrencyOrWindowIsRefused()
    {
        var ledger = new Ledger(new InMemoryLedgerStore());
        await ledger.AddAccountAsync(new("1020", "Bank USD", AccountType.Asset, "USD"));
        await Assert.ThrowsAsync<LedgerException>(() => Reconciler.ReconcileAsync(ledger, "1020", S()));
        await ledger.AddAccountAsync(Bank);
        await Assert.ThrowsAsync<LedgerException>(() => Reconciler.ReconcileAsync(ledger, "1010", S(), new ReconcileOptions { DateWindowDays = 400 }));
        await Assert.ThrowsAsync<LedgerException>(() => Reconciler.ReconcileAsync(ledger, "nope", S()));
    }

    [Fact]
    public void EveryLineIsAccountedForOnce_AndTheDifferenceAlwaysAddsUp()
    {
        var random = new Random(20261104);
        for (var round = 0; round < 3000; round++)
        {
            var amounts = new[] { 5m, 10m, 12.34m, 99m, 100m, -5m, -12.34m, -250m };
            var refs = new[] { null, null, "A", "B", "C" };
            DateOnly Day() => Nov4.AddDays(random.Next(-4, 5));
            var bank = Enumerable.Range(0, random.Next(0, 8)).Select(i => B($"b{i}", amounts[random.Next(amounts.Length)], Nov4.AddDays(random.Next(0, 3)), e2e: refs[random.Next(refs.Length)])).ToArray();
            var ledgerLines = bank.Where(_ => random.NextDouble() < 0.6).Select(b => L(b.Amount, b.Date.AddDays(random.Next(-3, 4)), random.NextDouble() < 0.7 ? b.EndToEndId : null))
                .Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => L(amounts[random.Next(amounts.Length)], Day(), refs[random.Next(refs.Length)])))
                .OrderBy(_ => random.Next()).ToArray();
            var st = new BankStatement("S", null, null, "EUR", Nov4, Nov4.AddDays(2), 1000m, 1000m + bank.Sum(b => b.Amount), bank, []);
            var opening = 1000m + ledgerLines.Where(l => l.Date < st.From).Sum(l => l.Amount) + (random.NextDouble() < 0.3 ? 7m : 0m);
            var closing = opening + ledgerLines.Where(l => l.Date >= st.From && l.Date <= st.To).Sum(l => l.Amount);
            var r = Reconciler.Reconcile(Bank, st, ledgerLines, opening, closing);

            // each bank line is matched, unmatched or a question, exactly once
            var seen = r.Matched.Select(m => m.Bank).Concat(r.UnmatchedBank).Concat(r.Questions.Select(q => q.Bank)).ToList();
            Assert.Equal(bank.Length, seen.Count);
            Assert.Equal(bank.Length, seen.Distinct().Count());
            // a ledger line is used at most once
            var used = r.Matched.SelectMany(m => m.Ledger).Concat(r.UnmatchedLedger).Concat(r.Questions.SelectMany(q => q.Candidates).Distinct()).ToList();
            Assert.Equal(used.Count, used.Distinct().Count());
            // matched amounts agree
            Assert.All(r.Matched, m => Assert.Equal(m.Bank.Amount, m.Ledger.Sum(l => l.Amount)));
            // the explanation adds up to the difference
            Assert.Equal(r.ClosingDifference, r.Difference.Sum(d => d.Amount));
        }
    }
}
