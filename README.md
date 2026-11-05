# LedgerKit

**Website: [ledgerkit-rose.vercel.app](https://ledgerkit-rose.vercel.app)** · runs in your browser; nothing is uploaded.

A double-entry ledger for .NET and PostgreSQL that refuses unbalanced or edited entries, and bank reconciliation against ISO 20022 **camt.053** statements. Reconciliation says what matched and why, what didn't, what needs a person to decide, and exactly why the balances differ.

```
$ ledgerkit reconcile samples/statement.xml --account 1010 --ledger samples/ledger.json
NOT RECONCILED  statement DE89-2026-11-04 (DE89370400440532013000) with account 1010, 2026-11-04 to 2026-11-04, EUR

  Balances            statement          ledger      difference
  Opening             10,000.00       11,500.00       -1,500.00
  Closing             11,200.00       10,949.50          250.50

  Why they differ
          -1,500.00  Difference already there when the statement starts (opening balances)
             -49.50  On the statement, not matched in the ledger
             300.00  In the ledger, not matched on the statement
           1,500.00  Matched, but dated outside the statement's period in the ledger

  Matched (2)
    2026-11-04       1,500.00  REF-20261104-0001  ↔  2026-11-03 ORDER-7781  (reference ORDER-7781 and amount 1,500.00)
    2026-11-04        -250.50  REF-20261104-0002  ↔  2026-11-04 RENT-NOV  (reference RENT-NOV and amount -250.50)

  On the statement, not in the ledger (1)
    2026-11-04         -49.50  3      Monthly account fee

  In the ledger, not on the statement (1)
    2026-11-04        -300.00  CHQ-1001  Cheque 1001 to a supplier  entry cb5ebaab-…
```

In this sample, the bank charged a fee the books don't have yet, and a cheque hasn't been cashed. The sale was booked a day before the bank credited it. Each line of "Why they differ" is one of those, and together they add up to the difference exactly.

## Install

On NuGet: [LedgerKit](https://www.nuget.org/packages/LedgerKit) · [LedgerKit.PostgreSql](https://www.nuget.org/packages/LedgerKit.PostgreSql) · [LedgerKit.Cli](https://www.nuget.org/packages/LedgerKit.Cli). For .NET 8 and .NET 10.

```sh
dotnet add package LedgerKit
dotnet add package LedgerKit.PostgreSql   # the PostgreSQL store
dotnet tool install --global LedgerKit.Cli   # the `ledgerkit` command
```

## The ledger

```csharp
using LedgerKit;
using LedgerKit.PostgreSql;

await using var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("LEDGERKIT_DB"));
await PostgresLedgerStore.EnsureSchemaAsync(db);          // once, as a role that may create the schema
var ledger = new Ledger(new PostgresLedgerStore(db));      // or new InMemoryLedgerStore()

await ledger.AddAccountAsync(new("1010", "Bank: ACLEDA USD", AccountType.Asset, "USD"));
await ledger.AddAccountAsync(new("4000", "Sales", AccountType.Income, "USD"));

var posted = await ledger.PostAsync(new NewEntry(
    IdempotencyKey: "invoice-445",
    Date: new DateOnly(2026, 11, 4),
    Description: "Invoice 445 paid",
    Lines: [NewLine.Debit("1010", 1000m), NewLine.Credit("4000", 1000m)],
    Reference: "INV-445"));                                // what the bank will show; reconciliation matches on it

await ledger.ReverseAsync(posted.EntryId, new DateOnly(2026, 11, 5), "invoice-445-void");
```

What the ledger guarantees:

- **Every entry balances** in each currency it touches. Its lines use existing accounts in their own currency, with no more decimals than the currency has (2 for USD, 0 for JPY, 3 for KWD…). Anything else is refused with a reason, such as "The entry doesn't balance in USD: debits exceed credits by 0.01."
- **Posting is idempotent.** The same entry posted again with the same key is recorded once, even when retries arrive concurrently. The same key with different content is refused.
- **Nothing is changed or deleted.** A mistake is corrected by a reversal. An entry can be reversed once, and a reversal can't itself be reversed.
- **Text is plain.** Descriptions, references and memos can't contain control characters or characters that change the direction of text.

In PostgreSQL, the database enforces these rules itself, through constraints and triggers, so they also hold for any program that writes to the tables directly:
- `UPDATE`, `DELETE` and `TRUNCATE` are refused;
- each entry must balance and match its accounts' currencies when its transaction commits;
- each entry records how many lines it has, so lines can't be added to it later.

The schema is in [`src/LedgerKit.PostgreSql/schema.sql`](src/LedgerKit.PostgreSql/schema.sql).

## Reconciliation

```csharp
using LedgerKit.Reconciliation;

await using var file = File.OpenRead("statement.xml");
var import = StatementImport.FromCamt053(file);            // read and checked by Iso20022Kit
foreach (var statement in import.Statements)
{
    var report = await Reconciler.ReconcileAsync(ledger, "1010", statement);
    // report.Matched, report.UnmatchedBank, report.UnmatchedLedger, report.Questions, report.Difference, report.Reconciled
}
```

A statement is read by [Iso20022Kit](https://github.com/vongvisalsambath/iso20022kit), which checks it against the official schema and its own arithmetic: opening balance plus entries must equal the closing balance. If it fails, it is refused, because reconciling against a wrong statement would only move the error. Entries that aren't booked yet (`PDNG`) are left out, with a note.

Lines are matched in this order. Each match records which rule matched it and why:

1. **Reference.** The bank's end-to-end ID or reference equals the ledger entry's reference, and the amounts are equal.
2. **Reference group.** Several ledger lines with that reference add up to the bank line, as when one transfer pays several invoices.
3. **Same amount and day.** For example, three coffees of 4.50 on each side. They are interchangeable, so they're paired in order.
4. **Amount and date.** The amounts are equal and the dates are within a window (3 days by default). This is used only when the pairing is unique on both sides, and never against a ledger line whose reference names a different payment.

Nothing is guessed. A reference that matches with a different amount, or several equally good candidates, becomes a **question** for a person, and its candidates are held aside so they can't also match something else.

The difference between the closing balances is then explained in at most four parts:
- a difference already there at the opening;
- statement lines not in the ledger;
- ledger lines not on the statement;
- matches that the ledger dated outside the statement's period.

The parts always add up to the difference. A test checks this on 3,000 random statements.

## Command line

```
ledgerkit init                                   create the ledgerkit schema
ledgerkit accounts
ledgerkit add-account CODE NAME TYPE CURRENCY
ledgerkit post entries.json                      posting the same file again is safe
ledgerkit balance CODE [--as-of DATE]
ledgerkit trial-balance [--as-of DATE]
ledgerkit reverse ENTRY-ID --date DATE --key KEY
ledgerkit reconcile statement.xml --account CODE [--window DAYS] [--format text|json|csv]
```

The database connection string comes from the `LEDGERKIT_DB` environment variable, never from an argument, so the password doesn't appear in process lists. To try the read-only commands without a database, give `--ledger ledger.json` (see [`samples/`](samples)). `reconcile` exits with 0 when everything matched, 1 when not, and 2 for a usage, file or database error.

## Website

`site/LedgerKit.Web` is a Blazor WebAssembly page that runs the same reconciliation in the browser:
- give it a camt.053 statement and the bank account's ledger lines as `date,reference,amount,description`;
- it shows the report and offers it as CSV;
- nothing is uploaded.

```
dotnet publish site/LedgerKit.Web -c Release -o out      # static site in out/wwwroot
cd site/test && npm ci && npx playwright install chromium
node e2e.cjs ../../out/wwwroot                             # the site in Chromium, under the production headers
```

## Building

`dotnet build` builds everything; Iso20022Kit comes from NuGet. The tests need a PostgreSQL database they may change:

```
export LEDGERKIT_TEST_DB="Host=localhost;Username=…;Password=…;Database=ledgerkit_test"
dotnet test
```

The same behaviour tests run against the in-memory store and PostgreSQL. The database tests also go around LedgerKit with raw SQL, to check that the database refuses on its own.

## Security

See [SECURITY.md](SECURITY.md), including the database role to connect as.

## Licence

Apache-2.0 (see [LICENSE](LICENSE)). The sample statement is Iso20022Kit's made-up example. Written with the help of Claude (Anthropic).
