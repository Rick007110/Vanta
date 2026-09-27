#!/bin/sh
# Local test stack without Docker: a throwaway Postgres cluster with the Supabase stub + migrations, PostgREST and a
# small mock of Supabase Auth (tests/mock_auth.py) under one URL (http://127.0.0.1:54321), like a real project.
#   sh tests/local-stack.sh db      # only Postgres (port 54329) + migrations
#   sh tests/local-stack.sh up      # Postgres + PostgREST (54322) + mock auth/gateway (54321), in the foreground
# Needs: PostgreSQL 15+ server binaries (PG_BIN), PostgREST (POSTGREST_BIN), Python 3 with aiohttp (PYTHON).
set -eu
export PGOPTIONS="${PGOPTIONS:--c client_min_messages=warning}"
cd "$(dirname "$0")/.."
PG_BIN=${PG_BIN:-$(ls -d /usr/lib/postgresql/*/bin 2>/dev/null | sort -V | tail -1)}
POSTGREST_BIN=${POSTGREST_BIN:-postgrest}
PYTHON=${PYTHON:-python3}
DATA=.tmp/pg
PORT=${PGPORT:-54329}
JWT_SECRET=${JWT_SECRET:-local-test-jwt-secret-that-is-at-least-32-chars}
mkdir -p .tmp

if [ ! -f "$DATA/PG_VERSION" ]; then
  "$PG_BIN/initdb" -D "$DATA" -U postgres -E UTF8 --locale=C.UTF-8 -A trust >/dev/null
fi
if ! "$PG_BIN/pg_ctl" -D "$DATA" status >/dev/null 2>&1; then
  "$PG_BIN/pg_ctl" -D "$DATA" -l .tmp/pg.log -o "-p $PORT -k /tmp -c listen_addresses=127.0.0.1" -w start >/dev/null
fi
PSQL="$PG_BIN/psql -h 127.0.0.1 -p $PORT -U postgres -v ON_ERROR_STOP=1 -q"
$PSQL -d postgres -c "drop database if exists vanta with (force)" -c "create database vanta encoding 'UTF8' template template0"
$PSQL -d vanta -f tests/supabase_stub.sql
for f in migrations/*.sql; do $PSQL -d vanta -f "$f"; done
$PSQL -d vanta -c "create extension if not exists pgtap with schema extensions" 2>/dev/null || true
echo "db ready: postgres://postgres@127.0.0.1:$PORT/vanta"

[ "${1:-db}" = "up" ] || exit 0
cat > .tmp/postgrest.conf <<CONF
db-uri = "postgres://authenticator:authenticator@127.0.0.1:$PORT/vanta"
db-schemas = "public"
db-anon-role = "anon"
jwt-secret = "$JWT_SECRET"
server-host = "127.0.0.1"
server-port = 54322
CONF
"$POSTGREST_BIN" .tmp/postgrest.conf > .tmp/postgrest.log 2>&1 &
PGRST=$!
trap 'kill $PGRST 2>/dev/null' EXIT INT TERM
JWT_SECRET="$JWT_SECRET" DATABASE_URL="postgres://postgres@127.0.0.1:$PORT/vanta" "$PYTHON" tests/mock_auth.py
