#!/bin/sh
# Applies supabase-setup.sql twice to a database that already has "someone else's" tables, trigger and functions with
# common names, and checks that they are untouched. Then runs the pgTAP tests. Uses the cluster from local-stack.sh.
set -eu
export PGOPTIONS="${PGOPTIONS:--c client_min_messages=warning}"
cd "$(dirname "$0")/.."
PG_BIN=${PG_BIN:-$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)}
PORT=${PGPORT:-54329}
PSQL="$PG_BIN/psql -h 127.0.0.1 -p $PORT -U postgres -v ON_ERROR_STOP=1 -q -X"
sh tests/local-stack.sh db >/dev/null                       # starts the cluster
$PSQL -d postgres -c "drop database if exists vanta_idem with (force)" -c "create database vanta_idem encoding 'UTF8' template template0"
$PSQL -d vanta_idem -f tests/supabase_stub.sql
$PSQL -d vanta_idem <<'SQL'
create table public.profiles (id uuid primary key, email text, full_name text);
create table public.reports (id serial primary key, title text);
create table public.events (id serial primary key, name text);
insert into public.reports (title) values ('existing'); insert into public.events (name) values ('existing');
create function public.handle_new_user() returns trigger language plpgsql security definer set search_path = '' as $$
begin insert into public.profiles (id, email) values (new.id, new.email); return new; end $$;
create trigger on_auth_user_created after insert on auth.users for each row execute function public.handle_new_user();
create function public.submit_report(t text) returns text language sql as $$ select 'mine: ' || t $$;
insert into auth.users (id, email, raw_app_meta_data) values ('00000000-0000-0000-0000-0000000000f1', 'existing@example.invalid', '{"provider":"email"}');
insert into auth.users (id, email, raw_user_meta_data, raw_app_meta_data) values ('00000000-0000-0000-0000-0000000000f2', null,
  '{"provider_id":"400000000000000099","full_name":"early"}', '{"provider":"discord","providers":["discord"]}');
SQL
$PSQL -d vanta_idem -f supabase-setup.sql
$PSQL -d vanta_idem -f supabase-setup.sql                  # second run must not fail or duplicate anything
out=$($PSQL -d vanta_idem -At <<'SQL'
select (select count(*) from public.reports) || ',' || (select count(*) from public.events) || ',' ||
       (select count(*) from public.profiles) || ',' || (select public.submit_report('x')) || ',' ||
       (select count(*) from pg_trigger where tgname = 'on_auth_user_created') || ',' ||
       (select count(*) from vanta.profiles) || ',' || (select count(*) from vanta.settings) || ',' ||
       (select count(*) from pg_trigger where tgname = 'vanta_profile_sync') || ',' ||
       (select count(*) from pg_policies where schemaname = 'vanta');
SQL
)
want="1,1,2,mine: x,1,1,1,1,5"
if [ "$out" != "$want" ]; then echo "idempotency: FAIL (got $out, want $want)"; exit 1; fi
echo "idempotency: ok (existing tables/trigger/function untouched, second run clean, existing Discord user backfilled)"
pg_prove -h 127.0.0.1 -p "$PORT" -U postgres -d vanta_idem tests/database/*.test.sql
