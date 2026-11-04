using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LedgerKit;

/// <summary>The checks every account and entry must pass, and the limits that keep input bounded.</summary>
public static partial class Rules
{
    /// <summary>The largest amount on one line.</summary>
    public const decimal MaxAmount = 1_000_000_000_000_000m;
    /// <summary>The most lines in one entry.</summary>
    public const int MaxLines = 1000;
    /// <summary>The longest description.</summary>
    public const int MaxDescription = 500;
    /// <summary>The longest reference (ISO 20022 references are at most 35 characters; remittance text 140).</summary>
    public const int MaxReference = 140;
    /// <summary>The longest memo on a line.</summary>
    public const int MaxMemo = 200;
    /// <summary>The longest account name.</summary>
    public const int MaxName = 200;

    private static readonly Dictionary<string, int> Decimals = new(StringComparer.Ordinal)
    {
        ["AUD"] = 2, ["BDT"] = 2, ["BHD"] = 3, ["BND"] = 2, ["CAD"] = 2, ["CHF"] = 2, ["CLP"] = 0, ["CNY"] = 2, ["DKK"] = 2, ["EUR"] = 2,
        ["GBP"] = 2, ["HKD"] = 2, ["IDR"] = 2, ["INR"] = 2, ["ISK"] = 0, ["JOD"] = 3, ["JPY"] = 0, ["KHR"] = 2, ["KRW"] = 0, ["KWD"] = 3,
        ["LAK"] = 2, ["LKR"] = 2, ["MMK"] = 2, ["MOP"] = 2, ["MYR"] = 2, ["NOK"] = 2, ["NPR"] = 2, ["NZD"] = 2, ["OMR"] = 3, ["PHP"] = 2,
        ["PKR"] = 2, ["PLN"] = 2, ["SEK"] = 2, ["SGD"] = 2, ["THB"] = 2, ["TND"] = 3, ["TWD"] = 2, ["USD"] = 2, ["VND"] = 0, ["ZAR"] = 2,
        ["AED"] = 2, ["SAR"] = 2, ["BRL"] = 2, ["MXN"] = 2, ["TRY"] = 2, ["UGX"] = 0, ["XAF"] = 0, ["XOF"] = 0, ["CLF"] = 4, ["UYW"] = 4,
    };

    /// <summary>The currencies accounts can use, with their ISO 4217 minor units.</summary>
    public static IReadOnlyDictionary<string, int> Currencies => Decimals;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,99}$")]
    private static partial Regex KeyPattern();

    /// <summary>Why an account can't be created; empty if it can.</summary>
    public static IReadOnlyList<string> Check(LedgerAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var p = new List<string>();
        if (account.Code is null || !CodePattern().IsMatch(account.Code))
            p.Add($"The account code {Quote(account.Code)} must be 1 to 32 letters, digits, '.', '_' or '-', starting with a letter or digit.");
        Text(p, "The account name", account.Name, MaxName, required: true);
        if (!Enum.IsDefined(account.Type)) p.Add("The account type must be asset, liability, equity, income or expense.");
        if (account.Currency is null || !Decimals.ContainsKey(account.Currency))
            p.Add($"The currency {Quote(account.Currency)} isn't one this ledger knows (ISO 4217 codes such as USD, KHR, EUR).");
        return p;
    }

    /// <summary>Why an entry can't be posted; empty if it can. <paramref name="accounts"/> finds an account by code.</summary>
    public static IReadOnlyList<string> Check(NewEntry entry, Func<string, LedgerAccount?> accounts)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var p = new List<string>();
        if (entry.IdempotencyKey is null || !KeyPattern().IsMatch(entry.IdempotencyKey))
            p.Add($"The idempotency key {Quote(entry.IdempotencyKey)} must be 1 to 100 letters, digits or . _ : / # -, starting with a letter or digit.");
        if (entry.Date.Year is < 1900 or > 2200) p.Add("The date must be between 1900 and 2200.");
        Text(p, "The description", entry.Description, MaxDescription, required: true);
        Text(p, "The reference", entry.Reference, MaxReference, required: false);
        if (entry.Lines is null || entry.Lines.Count < 2) { p.Add("An entry needs at least two lines: what was debited and what was credited."); return p; }
        if (entry.Lines.Count > MaxLines) { p.Add($"An entry can have at most {MaxLines:N0} lines."); return p; }

        var totals = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        for (var i = 0; i < entry.Lines.Count; i++)
        {
            var line = entry.Lines[i];
            var where = $"Line {i + 1}";
            if (line is null) { p.Add($"{where} is missing."); continue; }
            var account = line.Account is null ? null : accounts(line.Account);
            if (account is null) { p.Add($"{where}: there is no account {Quote(line.Account)}."); continue; }
            if (line.Amount == 0) p.Add($"{where}: the amount is zero.");
            if (Math.Abs(line.Amount) > MaxAmount) p.Add($"{where}: the amount is more than {MaxAmount:N0}.");
            else if (Scale(line.Amount) > Decimals[account.Currency])
                p.Add($"{where}: {account.Currency} has {Decimals[account.Currency]} decimal places, but {line.Amount.ToString(CultureInfo.InvariantCulture)} has {Scale(line.Amount)}.");
            Text(p, $"{where}'s memo", line.Memo, MaxMemo, required: false);
            totals[account.Currency] = totals.GetValueOrDefault(account.Currency) + line.Amount; // can't overflow: 1,000 lines of at most 1e15
        }
        foreach (var (currency, total) in totals)
            if (total != 0)
                p.Add($"The entry doesn't balance in {currency}: debits exceed credits by {total.ToString("0.########", CultureInfo.InvariantCulture)}.");
        return p;
    }

    /// <summary>The number of decimal places a value really has (1.50 has 1).</summary>
    internal static int Scale(decimal d)
    {
        d /= 1.000000000000000000000000000000000m; // drops trailing zeros
        return (decimal.GetBits(d)[3] >> 16) & 0xFF;
    }

    private static void Text(List<string> p, string what, string? value, int max, bool required)
    {
        if (value is null) { if (required) p.Add($"{what} is required."); return; }
        if (required && string.IsNullOrWhiteSpace(value)) { p.Add($"{what} is required."); return; }
        if (value.Length > max) p.Add($"{what} is {value.Length:N0} characters long; the limit is {max:N0}.");
        foreach (var r in value.EnumerateRunes())
        {
            if (Rune.IsControl(r) || IsDirectional(r.Value))
            {
                p.Add($"{what} contains a control or text-direction character (U+{r.Value:X4}). These can make a ledger read differently from what it holds.");
                break;
            }
        }
        if (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))) p.Add($"{what} starts or ends with a space.");
    }

    private static bool IsDirectional(int c) =>
        c is 0x061C or 0x200B or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0xFEFF;

    /// <summary>A value quoted for a message: at most 40 characters, control characters replaced.</summary>
    internal static string Quote(string? s)
    {
        if (s is null) return "(none)";
        var sb = new StringBuilder("\"");
        var n = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (++n > 40) { sb.Append('…'); break; }
            sb.Append(Rune.IsControl(r) || IsDirectional(r.Value) ? $"\\u{r.Value:X4}" : r.ToString());
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A fingerprint of an entry's content, so a second post with the same key can be told apart from a different entry.</summary>
    internal static string Hash(DateOnly date, string description, string? reference, Guid? reverses, IEnumerable<JournalLine> lines)
    {
        var sb = new StringBuilder();
        void Add(string? s) => sb.Append(s is null ? "-1" : s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append(';');
        Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(description);
        Add(reference);
        Add(reverses?.ToString("D"));
        foreach (var l in lines)
        {
            Add(l.Account);
            Add((l.Amount / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
            Add(l.Currency);
            Add(l.Memo);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
