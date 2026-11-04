using Npgsql;
using NpgsqlTypes;

namespace LedgerKit.PostgreSql;

/// <summary>
/// A ledger kept in PostgreSQL (13 or later). Every query uses parameters. The schema's triggers refuse changes to
/// posted rows and unbalanced entries, so the rules hold even for programs that write to the tables directly.
/// </summary>
/// <remarks>Connect as a role that can only SELECT and INSERT: see SECURITY.md. The role that owns the tables can disable their triggers.</remarks>
public sealed class PostgresLedgerStore(NpgsqlDataSource dataSource) : ILedgerStore
{
    private readonly NpgsqlDataSource db = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Creates the ledgerkit schema, tables and triggers if they are missing. Needs a role allowed to create them.</summary>
    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var stream = typeof(PostgresLedgerStore).Assembly.GetManifestResourceStream("LedgerKit.PostgreSql.schema.sql")
            ?? throw new InvalidOperationException("The schema resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var lockCmd = new NpgsqlCommand("select pg_advisory_xact_lock(7166510)", conn, tx)) await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAccountAsync(LedgerAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var cmd = db.CreateCommand(
            "insert into ledgerkit.account (code, name, type, currency) values ($1, $2, $3, $4) on conflict (code) do nothing");
        cmd.Parameters.Add(new() { Value = account.Code });
        cmd.Parameters.Add(new() { Value = account.Name });
        cmd.Parameters.Add(new() { Value = TypeName(account.Type) });
        cmd.Parameters.Add(new() { Value = account.Currency });
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return;
        var existing = await FindAccountAsync(account.Code, ct).ConfigureAwait(false);
        if (existing != account) throw new LedgerException($"An account {Rules.Quote(account.Code)} already exists with a different name, type or currency.");
    }

    /// <inheritdoc />
    public async Task<LedgerAccount?> FindAccountAsync(string code, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("select code, name, type, currency from ledgerkit.account where code = $1");
        cmd.Parameters.Add(new() { Value = code });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAccount(r) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LedgerAccount>> AccountsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("select code, name, type, currency from ledgerkit.account order by code collate \"C\"");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<LedgerAccount>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadAccount(r));
        return list;
    }

    /// <inheritdoc />
    public async Task<PostResult> AppendAsync(JournalEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var insert = new NpgsqlCommand(
                "insert into ledgerkit.entry (id, idempotency_key, content_hash, entry_date, description, reference, reverses, line_count) " +
                "values ($1, $2, $3, $4, $5, $6, $7, $8) on conflict (idempotency_key) do nothing", conn, tx))
            {
                insert.Parameters.Add(new() { Value = entry.Id });
                insert.Parameters.Add(new() { Value = entry.IdempotencyKey });
                insert.Parameters.Add(new() { Value = entry.ContentHash });
                insert.Parameters.Add(new() { Value = entry.Date });
                insert.Parameters.Add(new() { Value = entry.Description });
                insert.Parameters.Add(new() { Value = (object?)entry.Reference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                insert.Parameters.Add(new() { Value = (object?)entry.Reverses ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
                insert.Parameters.Add(new() { Value = entry.Lines.Count });
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                {
                    // the key is taken: the same entry posted again is fine, a different one isn't
                    await using var find = new NpgsqlCommand("select id, content_hash from ledgerkit.entry where idempotency_key = $1", conn, tx);
                    find.Parameters.Add(new() { Value = entry.IdempotencyKey });
                    await using var r = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await r.ReadAsync(ct).ConfigureAwait(false);
                    var (id, hash) = (r.GetGuid(0), r.GetString(1));
                    await r.DisposeAsync().ConfigureAwait(false);
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    if (hash != entry.ContentHash) throw new LedgerException($"The idempotency key {Rules.Quote(entry.IdempotencyKey)} was already used for a different entry.");
                    return new PostResult(id, true);
                }
            }

            await using (var lines = new NpgsqlCommand(
                "insert into ledgerkit.line (entry_id, line_no, account, amount, currency, memo) " +
                "select $1, n, a, m, c, x from unnest($2::int[], $3::text[], $4::numeric[], $5::text[], $6::text[]) as t(n, a, m, c, x)", conn, tx))
            {
                lines.Parameters.Add(new() { Value = entry.Id });
                lines.Parameters.Add(new() { Value = entry.Lines.Select(l => l.LineNo).ToArray() });
                lines.Parameters.Add(new() { Value = entry.Lines.Select(l => l.Account).ToArray() });
                lines.Parameters.Add(new() { Value = entry.Lines.Select(l => l.Amount).ToArray() });
                lines.Parameters.Add(new() { Value = entry.Lines.Select(l => l.Currency).ToArray() });
                lines.Parameters.Add(new() { Value = entry.Lines.Select(l => (object?)l.Memo ?? DBNull.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
                await lines.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false); // the balance check runs here
            return new PostResult(entry.Id, false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "entry_reverses_key")
        {
            throw new LedgerException("That entry has already been reversed.");
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.RaiseException or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation)
        {
            // the database's own checks: report what it said, never the connection details
            throw new LedgerException($"The database refused the entry: {e.MessageText}");
        }
    }

    /// <inheritdoc />
    public async Task<JournalEntry?> FindEntryAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "select e.idempotency_key, e.entry_date, e.description, e.reference, e.reverses, e.content_hash, l.line_no, l.account, l.amount, l.currency, l.memo " +
            "from ledgerkit.entry e join ledgerkit.line l on l.entry_id = e.id where e.id = $1 order by l.line_no");
        cmd.Parameters.Add(new() { Value = id });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        JournalEntry? entry = null;
        var lines = new List<JournalLine>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            entry ??= new JournalEntry(id, r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), lines, r.GetString(5));
            lines.Add(new JournalLine(r.GetInt32(6), r.GetString(7), Normalize(r.GetDecimal(8)), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return entry;
    }

    /// <inheritdoc />
    public async Task<decimal> BalanceAsync(string account, DateOnly? asOf = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(asOf is null
            ? "select coalesce(sum(amount), 0) from ledgerkit.line where account = $1"
            : "select coalesce(sum(l.amount), 0) from ledgerkit.line l join ledgerkit.entry e on e.id = l.entry_id where l.account = $1 and e.entry_date <= $2");
        cmd.Parameters.Add(new() { Value = account });
        if (asOf is not null) cmd.Parameters.Add(new() { Value = asOf.Value });
        return Normalize((decimal)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LedgerLine>> LinesAsync(string account, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "select e.id, l.line_no, e.entry_date, l.account, l.amount, l.currency, e.description, e.reference, l.memo " +
            "from ledgerkit.line l join ledgerkit.entry e on e.id = l.entry_id " +
            "where l.account = $1 and e.entry_date between $2 and $3 order by e.entry_date, e.seq, l.line_no");
        cmd.Parameters.Add(new() { Value = account });
        cmd.Parameters.Add(new() { Value = from });
        cmd.Parameters.Add(new() { Value = to });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<LedgerLine>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new LedgerLine(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), Normalize(r.GetDecimal(4)), r.GetString(5), r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        return list;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(DateOnly? asOf = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "select a.code, a.name, a.type, a.currency, " +
            "coalesce(sum(l.amount) filter (where l.amount > 0), 0), coalesce(-sum(l.amount) filter (where l.amount < 0), 0) " +
            "from ledgerkit.account a left join (ledgerkit.line l join ledgerkit.entry e on e.id = l.entry_id and ($1::date is null or e.entry_date <= $1)) on l.account = a.code " +
            "group by a.code, a.name, a.type, a.currency order by a.code collate \"C\"");
        cmd.Parameters.Add(new() { Value = (object?)asOf ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<TrialBalanceRow>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(new TrialBalanceRow(ReadAccount(r), Normalize(r.GetDecimal(4)), Normalize(r.GetDecimal(5))));
        return list;
    }

    private static LedgerAccount ReadAccount(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2) switch
        {
            "asset" => AccountType.Asset, "liability" => AccountType.Liability, "equity" => AccountType.Equity, "income" => AccountType.Income, _ => AccountType.Expense,
        }, r.GetString(3));

    private static string TypeName(AccountType t) => t.ToString().ToLowerInvariant();

    /// <summary>numeric(28,8) comes back with 8 decimals; drop the trailing zeros so values compare and print as they went in.</summary>
    private static decimal Normalize(decimal d) => d / 1.000000000000000000000000000000000m;
}
