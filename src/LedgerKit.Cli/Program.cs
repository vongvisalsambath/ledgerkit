using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LedgerKit;
using LedgerKit.Cli;
using LedgerKit.PostgreSql;
using LedgerKit.Reconciliation;
using Npgsql;

var inv = CultureInfo.InvariantCulture;
const long MaxFileBytes = 50L * 1024 * 1024;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Help();
    var (command, positional, options) = Parse(args);
    return command switch
    {
        "init" => await Init(),
        "accounts" => await Accounts(),
        "add-account" => await AddAccount(positional),
        "post" => await Post(positional),
        "balance" => await Balance(positional),
        "trial-balance" => await TrialBalance(),
        "reverse" => await Reverse(positional),
        "reconcile" => await Reconcile(positional),
        _ => throw new UsageException($"Unknown command {Safe.Text(command)}."),
    };

    async Task<int> Init()
    {
        NoPositional(0);
        await using var db = Database();
        await PostgresLedgerStore.EnsureSchemaAsync(db);
        Console.WriteLine("The ledgerkit schema is ready.");
        return 0;
    }

    async Task<int> Accounts()
    {
        NoPositional(0);
        var (ledger, dispose) = await Open();
        await using var _ = dispose;
        foreach (var a in await ledger.Store.AccountsAsync())
            Console.WriteLine($"{Safe.Text(a.Code),-12} {a.Type.ToString().ToLowerInvariant(),-10} {a.Currency}  {Safe.Text(a.Name)}");
        return 0;
    }

    async Task<int> AddAccount(List<string> p)
    {
        NoPositional(4);
        if (!Enum.TryParse<AccountType>(p[2], ignoreCase: true, out var type) || !Enum.IsDefined(type) || int.TryParse(p[2], out _))
            throw new UsageException($"The account type must be asset, liability, equity, income or expense, not {Safe.Text(p[2])}.");
        await using var db = Database();
        await new Ledger(new PostgresLedgerStore(db)).AddAccountAsync(new LedgerAccount(p[0], p[1], type, p[3]));
        Console.WriteLine($"Account {Safe.Text(p[0])} is ready.");
        return 0;
    }

    async Task<int> Post(List<string> p)
    {
        NoPositional(1);
        var entries = ReadJson<List<EntryJson>>(p[0]) ?? throw new UsageException("The file holds no entries.");
        await using var db = Database();
        var ledger = new Ledger(new PostgresLedgerStore(db));
        int posted = 0, again = 0;
        foreach (var (e, i) in entries.Select((e, i) => (e, i)))
        {
            try
            {
                var r = await ledger.PostAsync(e.ToEntry());
                if (r.AlreadyPosted) again++; else posted++;
            }
            catch (LedgerException ex)
            {
                Console.Error.WriteLine($"Entry {i + 1} ({Safe.Text(e.Key)}) was refused; the entries before it were posted.");
                foreach (var problem in ex.Problems) Console.Error.WriteLine("  " + Safe.Text(problem));
                return 1;
            }
        }
        Console.WriteLine($"{posted} posted, {again} already posted.");
        return 0;
    }

    async Task<int> Balance(List<string> p)
    {
        NoPositional(1);
        var (ledger, dispose) = await Open();
        await using var _ = dispose;
        var account = await ledger.Store.FindAccountAsync(p[0]) ?? throw new UsageException($"There is no account {Safe.Text(p[0])}.");
        var asOf = Date("as-of");
        var balance = await ledger.BalanceAsync(account.Code, asOf);
        var natural = account.CreditNormal ? -balance : balance;
        Console.WriteLine($"{Safe.Text(account.Code)} {Safe.Text(account.Name)}: {Money(natural)} {account.Currency}{(asOf is null ? "" : $" on {asOf:yyyy-MM-dd}")}" +
            (natural < 0 ? $" ({(account.CreditNormal ? "a debit" : "a credit")} balance)" : ""));
        return 0;
    }

    async Task<int> TrialBalance()
    {
        NoPositional(0);
        var (ledger, dispose) = await Open();
        await using var _ = dispose;
        var rows = await ledger.TrialBalanceAsync(Date("as-of"));
        Console.WriteLine($"{"Account",-12} {"Debits",18} {"Credits",18}  Currency");
        foreach (var r in rows) Console.WriteLine($"{Safe.Text(r.Account.Code),-12} {Money(r.Debits),18} {Money(r.Credits),18}  {r.Account.Currency}");
        var off = rows.GroupBy(r => r.Account.Currency).Where(g => g.Sum(r => r.Balance) != 0).ToList();
        Console.WriteLine(off.Count == 0 ? "Debits equal credits in every currency." : "Out of balance in " + string.Join(", ", off.Select(g => g.Key)) + ".");
        return off.Count == 0 ? 0 : 1;
    }

    async Task<int> Reverse(List<string> p)
    {
        NoPositional(1);
        if (!Guid.TryParse(p[0], out var id)) throw new UsageException($"{Safe.Text(p[0])} isn't an entry ID.");
        var date = Date("date") ?? throw new UsageException("Give the reversal's date with --date YYYY-MM-DD.");
        var key = Option("key") ?? throw new UsageException("Give the reversal an idempotency key with --key.");
        await using var db = Database();
        var r = await new Ledger(new PostgresLedgerStore(db)).ReverseAsync(id, date, key, Option("description"));
        Console.WriteLine(r.AlreadyPosted ? $"Already reversed by {r.EntryId}." : $"Reversed by {r.EntryId}.");
        return 0;
    }

    async Task<int> Reconcile(List<string> p)
    {
        NoPositional(1);
        var account = Option("account") ?? throw new UsageException("Name the ledger's bank account with --account.");
        var window = Option("window") is string w
            ? int.TryParse(w, NumberStyles.None, inv, out var n) && n <= 31 ? n : throw new UsageException("--window is a number of days from 0 to 31.")
            : 3;
        var format = Option("format") ?? "text";
        if (format is not ("text" or "json" or "csv")) throw new UsageException("--format is text, json or csv.");

        ImportResult import;
        await using (var file = OpenFile(p[0])) import = StatementImport.FromCamt053(file);
        if (!import.Ok)
        {
            Console.Error.WriteLine($"{Safe.Text(p[0])} can't be reconciled:");
            foreach (var problem in import.Problems) Console.Error.WriteLine("  " + Safe.Text(problem));
            return 1;
        }

        var (ledger, dispose) = await Open();
        await using var _ = dispose;
        var reports = new List<ReconciliationReport>();
        foreach (var statement in import.Statements)
            reports.Add(await Reconciler.ReconcileAsync(ledger, account, statement, new ReconcileOptions { DateWindowDays = window }));

        Console.Write(format switch { "json" => Report.Json(reports), "csv" => Report.Csv(reports), _ => Report.Text(reports) });
        return reports.All(r => r.Reconciled) ? 0 : 1;
    }

    // ---- helpers ----

    void NoPositional(int expected)
    {
        if (positional.Count != expected)
            throw new UsageException($"'{command}' takes {(expected == 0 ? "no" : expected.ToString(inv))} argument{(expected == 1 ? "" : "s")} besides its options; see 'ledgerkit --help'.");
    }

    string? Option(string name) => options.GetValueOrDefault(name);

    DateOnly? Date(string name) =>
        Option(name) is not string s ? null
        : DateOnly.TryParseExact(s, "yyyy-MM-dd", inv, DateTimeStyles.None, out var d) ? d
        : throw new UsageException($"--{name} is a date like 2026-11-04, not {Safe.Text(s)}.");

    async Task<(Ledger, IAsyncDisposable)> Open()
    {
        if (Option("ledger") is string path)
        {
            var file = ReadJson<LedgerFileJson>(path) ?? throw new UsageException("The ledger file is empty.");
            var memory = new Ledger(new InMemoryLedgerStore());
            foreach (var a in file.Accounts ?? []) await memory.AddAccountAsync(a.ToAccount());
            foreach (var e in file.Entries ?? []) await memory.PostAsync(e.ToEntry());
            return (memory, new Nothing());
        }
        var db = Database();
        return (new Ledger(new PostgresLedgerStore(db)), db);
    }

    T? ReadJson<T>(string path)
    {
        using var file = OpenFile(path);
        try
        {
            return JsonSerializer.Deserialize<T>(file, Json.Options);
        }
        catch (JsonException e)
        {
            throw new UsageException($"{Safe.Text(path)} isn't valid: {Safe.Text(e.Message)}");
        }
    }
}
catch (UsageException e)
{
    Console.Error.WriteLine("error: " + e.Message);
    Console.Error.WriteLine("Run 'ledgerkit --help' for the commands.");
    return 2;
}
catch (LedgerException e)
{
    foreach (var problem in e.Problems) Console.Error.WriteLine("refused: " + Safe.Text(problem));
    return 1;
}
catch (PostgresException e)
{
    // the server's own message, never the connection details
    Console.Error.WriteLine($"database error {e.SqlState}: {Safe.Text(e.MessageText)}");
    return 2;
}
catch (NpgsqlException)
{
    Console.Error.WriteLine("Couldn't use the database: check that it is running and that LEDGERKIT_DB is right.");
    return 2;
}

static NpgsqlDataSource Database()
{
    var cs = Environment.GetEnvironmentVariable("LEDGERKIT_DB");
    if (string.IsNullOrWhiteSpace(cs))
        throw new UsageException("Set LEDGERKIT_DB to the database's connection string (it is read from the environment so the password never appears in a process list), or use --ledger file.json.");
    try
    {
        return NpgsqlDataSource.Create(cs);
    }
    catch (ArgumentException)
    {
        throw new UsageException("LEDGERKIT_DB isn't a valid PostgreSQL connection string.");
    }
}

static FileStream OpenFile(string path)
{
    FileStream f;
    try
    {
        f = File.OpenRead(path);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        throw new UsageException($"Can't read {Safe.Text(path)}.");
    }
    if (f.Length > MaxFileBytes)
    {
        f.Dispose();
        throw new UsageException($"{Safe.Text(path)} is larger than 50 MB.");
    }
    return f;
}

static (string, List<string>, Dictionary<string, string>) Parse(string[] args)
{
    var positional = new List<string>();
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var known = new HashSet<string>(StringComparer.Ordinal) { "as-of", "date", "key", "description", "account", "window", "format", "ledger" };
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var name = args[i][2..];
            if (!known.Contains(name)) throw new UsageException($"Unknown option {Safe.Text(args[i])}.");
            if (i + 1 >= args.Length) throw new UsageException($"--{name} needs a value.");
            if (!options.TryAdd(name, args[++i])) throw new UsageException($"--{name} is given twice.");
        }
        else positional.Add(args[i]);
    }
    return (args[0], positional, options);
}

static string Money(decimal d) => d.ToString("#,0.00######", CultureInfo.InvariantCulture);

static int Help()
{
    Console.WriteLine("""
        ledgerkit: a double-entry ledger in PostgreSQL, reconciled with ISO 20022 camt.053 bank statements.

          ledgerkit init                                   create the ledgerkit schema
          ledgerkit accounts                               list the accounts
          ledgerkit add-account CODE NAME TYPE CURRENCY    TYPE: asset, liability, equity, income or expense
          ledgerkit post entries.json                      post entries; posting one again is safe
          ledgerkit balance CODE [--as-of DATE]
          ledgerkit trial-balance [--as-of DATE]
          ledgerkit reverse ENTRY-ID --date DATE --key KEY [--description TEXT]
          ledgerkit reconcile statement.xml --account CODE [--window DAYS] [--format text|json|csv]

        The database comes from LEDGERKIT_DB (a PostgreSQL connection string). To try the read-only commands
        without a database, give --ledger ledger.json instead (see samples/).

        Exit codes: 0 done (reconcile: everything matched), 1 refused or not reconciled, 2 usage, file or database error.
        """);
    return 0;
}

namespace LedgerKit.Cli
{
    internal sealed class UsageException(string message) : Exception(message);

    internal sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Makes text from a statement, a ledger or the command line safe to print to a terminal.</summary>
    internal static class Safe
    {
        public static string Text(string? s, int max = 200)
        {
            if (s is null) return "";
            var sb = new StringBuilder();
            var n = 0;
            foreach (var r in s.EnumerateRunes())
            {
                if (++n > max) { sb.Append('…'); break; }
                var c = r.Value;
                var hidden = Rune.IsControl(r) || c is 0x061C or 0x200B or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF;
                sb.Append(hidden ? $"\\u{c:X4}" : r.ToString());
            }
            return sb.ToString();
        }
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            MaxDepth = 8,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
    }

    internal sealed record LedgerFileJson(
        [property: JsonPropertyName("accounts")] List<AccountJson>? Accounts,
        [property: JsonPropertyName("entries")] List<EntryJson>? Entries);

    internal sealed record AccountJson(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("currency")] string Currency)
    {
        public LedgerAccount ToAccount() =>
            Enum.TryParse<AccountType>(Type, ignoreCase: true, out var t) && Enum.IsDefined(t) && !int.TryParse(Type, out _)
                ? new LedgerAccount(Code, Name, t, Currency)
                : throw new UsageException($"Account {Safe.Text(Code)}: the type must be asset, liability, equity, income or expense.");
    }

    internal sealed record EntryJson(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("date")] DateOnly Date,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("reference")] string? Reference,
        [property: JsonPropertyName("lines")] List<LineJson> Lines)
    {
        public NewEntry ToEntry() => new(Key, Date, Description, (Lines ?? []).Select((l, i) => l.ToLine(i + 1)).ToList(), Reference);
    }

    internal sealed record LineJson(
        [property: JsonPropertyName("account")] string Account,
        [property: JsonPropertyName("debit")] decimal? Debit,
        [property: JsonPropertyName("credit")] decimal? Credit,
        [property: JsonPropertyName("memo")] string? Memo)
    {
        public NewLine ToLine(int n) => (Debit, Credit) switch
        {
            (decimal d, null) when d > 0 => NewLine.Debit(Account, d, Memo),
            (null, decimal c) when c > 0 => NewLine.Credit(Account, c, Memo),
            _ => throw new UsageException($"Line {n} needs either a debit or a credit, greater than zero."),
        };
    }
}
