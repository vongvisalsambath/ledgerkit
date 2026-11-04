-- LedgerKit schema for PostgreSQL 13 or later. Safe to run again: it creates only what is missing.
-- The database enforces the ledger's rules itself, so they hold even for a program that bypasses LedgerKit:
--   * posted rows are never updated or deleted (corrections are reversals);
--   * every entry balances in each currency, uses its accounts' currencies, and has exactly the lines it was posted with.

create schema if not exists ledgerkit;

create table if not exists ledgerkit.account (
  code       text primary key check (code ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$'),
  name       text not null check (length(name) between 1 and 200),
  type       text not null check (type in ('asset', 'liability', 'equity', 'income', 'expense')),
  currency   char(3) not null check (currency ~ '^[A-Z]{3}$'),
  created_at timestamptz not null default now()
);

create table if not exists ledgerkit.entry (
  id              uuid primary key,
  seq             bigint generated always as identity unique,
  idempotency_key text not null unique check (idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:/#-]{0,99}$'),
  content_hash    text not null check (content_hash ~ '^[0-9A-F]{64}$'),
  entry_date      date not null check (entry_date between date '1900-01-01' and date '2200-12-31'),
  description     text not null check (length(description) between 1 and 500),
  reference       text check (length(reference) between 1 and 140),
  reverses        uuid unique references ledgerkit.entry (id),
  line_count      int not null check (line_count between 2 and 1000),
  recorded_at     timestamptz not null default now()
);

create table if not exists ledgerkit.line (
  entry_id uuid not null references ledgerkit.entry (id),
  line_no  int not null check (line_no between 1 and 1000),
  account  text not null references ledgerkit.account (code),
  amount   numeric(28, 8) not null check (amount <> 0 and abs(amount) <= 1000000000000000),
  currency char(3) not null,
  memo     text check (length(memo) <= 200),
  primary key (entry_id, line_no)
);

create index if not exists line_account_idx on ledgerkit.line (account);
create index if not exists entry_date_idx on ledgerkit.entry (entry_date);

-- append-only
create or replace function ledgerkit.refuse_change() returns trigger language plpgsql as $$
begin
  raise exception 'LedgerKit: % on ledgerkit.% is refused: the ledger is append-only. Correct an entry by reversing it.', tg_op, tg_table_name
    using errcode = 'P0001';
end $$;

-- an entry's lines, checked when the transaction that wrote them commits
create or replace function ledgerkit.check_entry(p_entry uuid) returns void language plpgsql as $$
declare
  expected int;
  found int;
  bad text;
begin
  select line_count into expected from ledgerkit.entry where id = p_entry;
  select count(*) into found from ledgerkit.line where entry_id = p_entry;
  if expected is null then
    raise exception 'LedgerKit: lines refer to entry %, which does not exist.', p_entry using errcode = 'P0001';
  end if;
  if found <> expected then
    raise exception 'LedgerKit: entry % was posted with % lines but has %; lines cannot be added to a posted entry.', p_entry, expected, found using errcode = 'P0001';
  end if;
  select l.account into bad from ledgerkit.line l join ledgerkit.account a on a.code = l.account
    where l.entry_id = p_entry and l.currency <> a.currency limit 1;
  if bad is not null then
    raise exception 'LedgerKit: entry % has a line in a different currency from its account %.', p_entry, bad using errcode = 'P0001';
  end if;
  select currency into bad from ledgerkit.line where entry_id = p_entry group by currency having sum(amount) <> 0 limit 1;
  if bad is not null then
    raise exception 'LedgerKit: entry % does not balance in %.', p_entry, bad using errcode = 'P0001';
  end if;
end $$;

create or replace function ledgerkit.check_entry_on_entry() returns trigger language plpgsql as $$
begin
  perform ledgerkit.check_entry(new.id);
  return null;
end $$;

create or replace function ledgerkit.check_entry_on_line() returns trigger language plpgsql as $$
begin
  perform ledgerkit.check_entry(new.entry_id);
  return null;
end $$;

do $$
declare
  t text;
begin
  foreach t in array array['account', 'entry', 'line'] loop
    if not exists (select from pg_trigger where tgname = t || '_append_only' and tgrelid = ('ledgerkit.' || t)::regclass) then
      execute format('create trigger %I before update or delete on ledgerkit.%I for each row execute function ledgerkit.refuse_change()', t || '_append_only', t);
    end if;
    if not exists (select from pg_trigger where tgname = t || '_no_truncate' and tgrelid = ('ledgerkit.' || t)::regclass) then
      execute format('create trigger %I before truncate on ledgerkit.%I for each statement execute function ledgerkit.refuse_change()', t || '_no_truncate', t);
    end if;
  end loop;
  foreach t in array array['entry', 'line'] loop
    if not exists (select from pg_trigger where tgname = t || '_balanced' and tgrelid = ('ledgerkit.' || t)::regclass) then
      execute format('create constraint trigger %I after insert on ledgerkit.%I deferrable initially deferred for each row execute function ledgerkit.%I()', t || '_balanced', t, 'check_entry_on_' || t);
    end if;
  end loop;
end $$;
