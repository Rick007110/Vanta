#!/bin/sh
# Builds supabase-setup.sql (all migrations in one file, for the Supabase SQL editor).
set -eu
cd "$(dirname "$0")"
{
  echo "-- Vanta community reports: complete database setup for Supabase."
  echo "-- Paste into Dashboard > SQL Editor > New query and click Run. Safe to run again (idempotent) and safe in an"
  echo "-- existing project: it only creates the schema \"vanta\", functions named public.vanta_* and a trigger named"
  echo "-- vanta_profile_sync on auth.users. It never drops or changes your own tables."
  echo "-- Generated from supabase/migrations/ by build-setup-sql.sh; edit the migrations, not this file."
  echo
  for f in migrations/*.sql; do
    echo "-- ===== $(basename "$f") ====="
    cat "$f"
    echo
  done
} > supabase-setup.sql
echo "supabase-setup.sql: $(wc -l < supabase-setup.sql) lines"
