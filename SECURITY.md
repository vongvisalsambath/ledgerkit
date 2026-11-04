# Security

A ledger is a record that people rely on, and bank statements come from outside. LedgerKit protects the record from mistakes and from tampering, and treats statements and input files as hostile.

## Reporting a problem

Please report vulnerabilities privately through GitHub's **Security → Report a vulnerability** on this repository, not in a public issue.

## Connect as a role that can only read and append

In PostgreSQL, the role that owns a table can disable its triggers, and a superuser can skip them. So the application should connect as a role that can only `SELECT` and `INSERT`. Create the schema once with an owner role (`ledgerkit init`), then:

```sql
create role ledgerkit_app login password '…';
grant connect on database yourdb to ledgerkit_app;
grant usage on schema ledgerkit to ledgerkit_app;
grant select, insert on ledgerkit.account, ledgerkit.entry, ledgerkit.line to ledgerkit_app;
```

Checked by hand on PostgreSQL 16, such a role can add accounts, post and reconcile. The database refuses it when it tries to:
- update, delete or truncate rows;
- disable or add triggers;
- drop tables;
- set `session_replication_role = replica`, which would skip triggers.

## What is protected, and how

| Threat | Protection | Tested by |
|---|---|---|
| **Unbalanced or malformed entries** | Checked before they reach the store: they must balance in each currency, use existing accounts in their own currency, have no more decimals than the currency, contain no zero amounts, and stay within limits (1,000 lines, 1e15 per line). PostgreSQL checks again at commit, with a deferred constraint trigger. | `BadEntriesAreRefusedWithAReason` (both stores); `AnUnbalancedEntryWrittenDirectlyIsRefusedAtCommit`, `TheDatabaseRefusesWhatTheLibraryWouldHaveCaught` |
| **Changing history** | No update or delete in the API. Database triggers refuse `UPDATE`, `DELETE` and `TRUNCATE` on all three tables. Each entry stores its line count, so lines can't be added to a posted entry. Corrections are reversals, at most one per entry. | `PostedRowsCantBeUpdatedDeletedOrTruncated`, `LinesCantBeAddedToAPostedEntry`, `AReversalCancelsAnEntryOnceOnly` |
| **Double posting** | Idempotency keys are unique in the database. A retry with the same content returns the first entry; different content with the same key is refused, judged by a SHA-256 fingerprint of the content. | `ConcurrentRetriesOfOneEntryRecordItOnce` (16 at once), `ConcurrentReversalsOfOneEntryLeaveOne` |
| **SQL injection** | Every query uses parameters; no SQL is built from input. Account codes and keys are restricted to letters, digits and a few punctuation marks. | `TextThatLooksLikeSqlIsStoredAsText`; the "bad key" case |
| **Misleading text** | Control characters and direction-changing characters (such as a right-to-left override) are refused in descriptions, references, memos and names. Text from statements is printed with such characters as `‮`-style codes. | `BadEntriesAreRefusedWithAReason` (bidi, control), `AReportOfHostileTextHasNoControlCharacters` |
| **Hostile statements** | Read by Iso20022Kit: DTDs and external entities are refused (no XXE), with depth, size and issue limits. Statements that fail the schema or their own balance check are refused. Files over 50 MB are refused before reading. | `AnInvalidStatementIsRefused`; manual CLI checks (XXE, 60 MB file, `/etc/shadow`) |
| **Wrong matches** | Matching never guesses. Ambiguous candidates and same-reference-different-amount cases go to a person, with their candidates held aside so they can't also match something else. A ledger line whose reference names another payment never matches by amount. | the matching tests; `EveryLineIsAccountedForOnce_AndTheDifferenceAlwaysAddsUp` (3,000 random statements) |
| **Spreadsheet formulas in CSV reports** | Text cells starting with `=`, `+`, `-` or `@`, or a tab or carriage return, get a leading apostrophe; cells are quoted and controls escaped. | `CsvCellsCantBecomeFormulas` |
| **Terminal escapes** | All text from statements, ledgers and the command line is printed with controls escaped and cut to a fixed length. JSON output escapes non-ASCII. | `TerminalEscapesAndDirectionMarksArePrintedAsCodes`; manual check with a C1 control in a statement |
| **Leaking the database password** | The connection string is read only from `LEDGERKIT_DB`, never from arguments. Errors show the server's message or a generic one, never the connection string. | manual checks with a wrong password and an unreachable server |
| **Untrusted JSON input** | Unknown properties are refused, nesting is limited to 8 levels, numbers must fit `decimal`, and each line needs exactly one positive debit or credit. | manual CLI checks |
| **The website** | The demo page runs everything in the browser; nothing is uploaded, and `connect-src 'self'` keeps it that way. Blazor writes every value as text. Statements are read by the same Iso20022Kit checks, up to 5 MB, and ledger input is limited to 5,000 lines of 500 characters. The page can't be framed. Unfingerprinted loader files are served with `no-cache`, so a deploy never leaves visitors with stale ones. | `site/test/e2e.cjs`: a DTD with an external entity, non-XML, an inconsistent statement, markup in statements and ledgers, 5,001 lines, phone layout, headers |
| **Supply chain** | One dependency outside .NET: Npgsql 10.0.3, at an exact version. Iso20022Kit is built from a pinned commit. The browser tests use Playwright at an exact version with a lockfile. CI runs `dotnet list package --vulnerable` and `npm audit`, and pins GitHub Actions and the PostgreSQL image. | CI |

## What it doesn't do

- It doesn't authenticate users or decide who may post: that belongs to the application around it.
- A role with `INSERT` can post any balanced entry. The ledger can show an entry, but it can't judge whether it is honest.
- Reconciliation compares; it doesn't post corrections. Bank fees found on a statement must be posted by a person or a rule you write.
