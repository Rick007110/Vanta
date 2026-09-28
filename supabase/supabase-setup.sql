-- Vanta community reports, game requests and admin API: complete database setup for Supabase.
-- Paste into Dashboard > SQL Editor > New query and click Run. Safe to run again (idempotent) and safe in an
-- existing project: it only creates the schema "vanta", functions named public.vanta_* and a trigger named
-- vanta_profile_sync on auth.users. It never drops or changes your own tables.
-- Generated from supabase/migrations/ by build-setup-sql.sh; edit the migrations, not this file.

-- ===== 20260927120000_community_schema.sql =====
-- Vanta community reports: tables, Row Level Security and grants.
--
-- Safe to run in an existing Supabase project and safe to run again: everything lives in its own schema "vanta"
-- (tables and helper functions) plus a few functions named public.vanta_* (the API). Nothing outside those names is
-- created, changed or dropped; the only other object is a trigger named vanta_profile_sync on auth.users.
-- The "vanta" schema does not need to be added to "Exposed schemas": apps and the bot only call the public.vanta_* RPCs.
--
-- Data minimisation: profiles hold only the Discord id, username and avatar URL (copied from the auth metadata by a
-- trigger). No e-mail addresses, no IP addresses (anonymous rate limits use a salted hash that rotates daily and is
-- deleted within a day), no Discord access tokens.
--
-- Every table has RLS enabled. Signed-in users may only touch their own reports; aggregated community data is read
-- through functions that never return identities. The Discord bot uses the secret (service_role) key and the
-- vanta_bot_* functions.

create schema if not exists vanta;
revoke all on schema vanta from public, anon;
grant usage on schema vanta to authenticated, service_role;

-- ---------------------------------------------------------------------------------------------------------------------
-- profiles: one row per auth user, maintained by a trigger on auth.users (see functions migration).
create table if not exists vanta.profiles (
  id          uuid primary key references auth.users (id) on delete cascade,
  discord_id  text unique check (discord_id ~ '^[0-9]{5,25}$'),
  username    text not null default 'user' check (char_length(username) between 1 and 100),
  avatar_url  text check (avatar_url is null or avatar_url like 'https://cdn.discordapp.com/%'),
  banned      boolean not null default false,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now()
);
comment on table vanta.profiles is 'Discord id/username/avatar from the auth metadata. No e-mail. Written only by triggers and the bot.';

-- ---------------------------------------------------------------------------------------------------------------------
-- reports: one vote per user per (game, cheat, game-version fingerprint). A new vote replaces the old one.
create table if not exists vanta.reports (
  id             bigint generated always as identity primary key,
  user_id        uuid not null default auth.uid() references vanta.profiles (id) on delete cascade,
  game_id        text not null check (game_id ~ '^[a-z0-9][a-z0-9_-]{0,63}$'),
  cheat_id       text not null check (cheat_id ~ '^[a-z0-9][a-z0-9_-]{0,63}$'),
  fingerprint    text not null check (char_length(fingerprint) between 1 and 200 and fingerprint ~ '^[ -~]+$'),
  status         text not null check (status in ('works', 'broken')),
  note           text check (char_length(note) <= 300),
  vanta_version  text not null check (vanta_version ~ '^[0-9A-Za-z.+-]{1,32}$'),
  game_version   text check (char_length(game_version) <= 80),
  game_name      text check (char_length(game_name) <= 80),
  cheat_name     text check (char_length(cheat_name) <= 80),
  created_at     timestamptz not null default now(),
  updated_at     timestamptz not null default now(),
  unique (user_id, game_id, cheat_id, fingerprint)
);
create index if not exists reports_cheat on vanta.reports (game_id, cheat_id, fingerprint);
create index if not exists reports_updated on vanta.reports (updated_at);

-- cheat_state: per (game, cheat, fingerprint) the triage status set by admins in Discord and the bot's message id.
create table if not exists vanta.cheat_state (
  id               bigint generated always as identity primary key,
  game_id          text not null,
  cheat_id         text not null,
  fingerprint      text not null,
  status           text not null default 'open' check (status in ('open', 'fixed', 'cant_reproduce', 'duplicate')),
  fixed_in_version text check (fixed_in_version ~ '^[0-9A-Za-z.+-]{1,32}$'),
  status_changed   timestamptz,          -- while status <> 'open', reports older than this do not count
  game_name        text,
  cheat_name       text,
  game_version     text,
  message_id       text check (message_id ~ '^[0-9]{5,25}$'),
  updated_at       timestamptz not null default now(),
  unique (game_id, cheat_id, fingerprint)
);

-- First time a game fingerprint was reported ("newest known game version" and spike detection).
create table if not exists vanta.fingerprints (
  game_id      text not null,
  fingerprint  text not null,
  game_version text,
  first_seen   timestamptz not null default now(),
  primary key (game_id, fingerprint)
);

-- Opt-in anonymous usage: "cheat enabled" counts per UTC day. No user id, no IP.
create table if not exists vanta.usage_daily (
  day      date   not null,
  game_id  text   not null,
  cheat_id text   not null,
  count    bigint not null check (count >= 0),
  primary key (day, game_id, cheat_id)
);

-- Change feed for the Discord bot (polled with a cursor). Pruned after 30 days by bot_cleanup().
create table if not exists vanta.events (
  id        bigint generated always as identity primary key,
  type      text   not null check (type in ('report', 'withdrawn', 'status')),
  state_id  bigint not null references vanta.cheat_state (id) on delete cascade,
  status    text,
  created   timestamptz not null default now()
);

-- Fixed-window rate limits (not exposed through the API).
create table if not exists vanta.rate_limits (
  key      text primary key,
  win      bigint not null,
  count    integer not null,
  expires  timestamptz not null
);

-- Random salt for hashing client IPs in anonymous rate limits (never leaves the database).
create table if not exists vanta.settings (
  key   text primary key,
  value text not null
);
insert into vanta.settings (key, value) values ('ip_salt', replace(gen_random_uuid()::text || gen_random_uuid()::text, '-', ''))
  on conflict (key) do nothing;

-- ---------------------------------------------------------------------------------------------------------------------
-- Row Level Security
alter table vanta.profiles    enable row level security;
alter table vanta.reports     enable row level security;
alter table vanta.cheat_state enable row level security;
alter table vanta.fingerprints enable row level security;
alter table vanta.usage_daily enable row level security;
alter table vanta.events      enable row level security;
alter table vanta.rate_limits enable row level security;
alter table vanta.settings    enable row level security;

-- No access for the API roles except what is granted below.
revoke all on vanta.profiles, vanta.reports, vanta.cheat_state, vanta.fingerprints, vanta.usage_daily, vanta.events
  from anon, authenticated;
revoke all on vanta.rate_limits, vanta.settings from public, anon, authenticated;
grant all on vanta.profiles, vanta.reports, vanta.cheat_state, vanta.fingerprints, vanta.usage_daily, vanta.events
  to service_role;

-- profiles: a signed-in user can read only their own row (to see e.g. the banned flag). No client writes.
grant select on vanta.profiles to authenticated;
drop policy if exists profiles_select_own on vanta.profiles;
create policy profiles_select_own on vanta.profiles for select to authenticated
  using (id = (select auth.uid()));

-- Used by the report policies: the caller has a profile and is not banned.
create or replace function vanta.can_report() returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from vanta.profiles p where p.id = (select auth.uid()) and not p.banned);
$$;
revoke all on function vanta.can_report() from public;
grant execute on function vanta.can_report() to authenticated, service_role;

-- reports: own rows only; inserting/updating needs a profile that is not banned (checked in vanta.can_report()).
grant select, insert, update, delete on vanta.reports to authenticated;
grant usage on sequence vanta.reports_id_seq to authenticated;
drop policy if exists reports_select_own on vanta.reports;
create policy reports_select_own on vanta.reports for select to authenticated
  using (user_id = (select auth.uid()));
drop policy if exists reports_insert_own on vanta.reports;
create policy reports_insert_own on vanta.reports for insert to authenticated
  with check (user_id = (select auth.uid()) and (select vanta.can_report()));
drop policy if exists reports_update_own on vanta.reports;
create policy reports_update_own on vanta.reports for update to authenticated
  using (user_id = (select auth.uid()))
  with check (user_id = (select auth.uid()) and (select vanta.can_report()));
drop policy if exists reports_delete_own on vanta.reports;
create policy reports_delete_own on vanta.reports for delete to authenticated
  using (user_id = (select auth.uid()));

-- cheat_state, fingerprints, usage_daily, events: no policies = no access for anon/authenticated (the service role
-- bypasses RLS). Clients read aggregates through public.vanta_community().

-- ===== 20260927120100_community_functions.sql =====
-- Vanta community reports: helper functions, scoring, triggers and the RPC functions used by the Vanta app.
-- Helpers live in the "vanta" schema, which is not exposed through the Data API; the API is public.vanta_*.
-- Errors use SQLSTATE 'PTxxx' so PostgREST answers with HTTP status xxx; the message is a stable error code.

-- ---------------------------------------------------------------------------------------------------------------------
-- small helpers
create or replace function vanta.fail(p_code text, p_http int default 400, p_detail text default null) returns void
language plpgsql volatile set search_path = '' as $$
begin
  raise exception using errcode = 'PT' || p_http::text, message = p_code, detail = coalesce(p_detail, '');
end $$;

-- Semver-ish compare of Vanta versions ("0.2.10" > "0.2.9"); non-numeric parts compare as 0 (like parseInt).
create or replace function vanta.cmp_version(a text, b text) returns int
language plpgsql immutable set search_path = '' as $$
declare pa numeric[]; pb numeric[]; i int; x numeric; y numeric;
begin
  pa := array(select coalesce(substring(p from '^[0-9]+')::numeric, 0) from unnest(regexp_split_to_array(coalesce(a, ''), '[.+-]')) with ordinality as u(p, n) order by n);
  pb := array(select coalesce(substring(p from '^[0-9]+')::numeric, 0) from unnest(regexp_split_to_array(coalesce(b, ''), '[.+-]')) with ordinality as u(p, n) order by n);
  for i in 1 .. greatest(cardinality(pa), cardinality(pb), 3) loop
    x := coalesce(pa[i], 0); y := coalesce(pb[i], 0);
    if x <> y then return sign(x - y)::int; end if;
  end loop;
  return 0;
end $$;

-- Which reports count for a state: all while open; after a status change only newer ones (and, when fixed in a
-- version, only reports from that Vanta version on).
create or replace function vanta.report_counts(p_state_status text, p_status_changed timestamptz, p_fixed_in text,
                                                 p_updated timestamptz, p_vanta text) returns boolean
language sql immutable set search_path = '' as $$
  select case
    when p_state_status = 'open' or p_status_changed is null then true
    when p_updated <= p_status_changed then false
    when p_state_status = 'fixed' and p_fixed_in is not null then vanta.cmp_version(p_vanta, p_fixed_in) >= 0
    else true
  end;
$$;

-- Printable text: NFC, no control/bidi/zero-width characters, collapsed whitespace, max length; '' becomes null.
create or replace function vanta.clean_text(v text, max_len int, field text) returns text
language plpgsql immutable set search_path = '' as $$
declare c text;
begin
  if v is null then return null; end if;
  c := normalize(v, NFC);
  c := regexp_replace(c, '[\u0001-\u0008\u000B-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\u2060-\u2069\uFEFF]', '', 'g');
  c := btrim(regexp_replace(c, '[ \t\r\n]+', ' ', 'g'));
  if char_length(c) > max_len then perform vanta.fail(field || '_too_long'); end if;
  return nullif(c, '');
end $$;

create or replace function vanta.check_id(v text, field text) returns text
language plpgsql immutable set search_path = '' as $$
begin
  if v is null or v !~ '^[a-z0-9][a-z0-9_-]{0,63}$' then perform vanta.fail('invalid_' || field); end if;
  return v;
end $$;

create or replace function vanta.check_fingerprint(v text) returns text
language plpgsql immutable set search_path = '' as $$
begin
  if v is null or char_length(v) not between 1 and 200 or v !~ '^[ -~]+$' or btrim(v) = '' then perform vanta.fail('invalid_fingerprint'); end if;
  return btrim(v);
end $$;

create or replace function vanta.check_version(v text, field text) returns text
language plpgsql immutable set search_path = '' as $$
begin
  if v is null or v !~ '^[0-9A-Za-z.+-]{1,32}$' then perform vanta.fail('invalid_' || field); end if;
  return v;
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- rate limiting (fixed window). Raises HTTP 429 with detail "retry_after=<seconds>".
create or replace function vanta.rate_limit(p_name text, p_subject text, p_limit int, p_window int) returns void
language plpgsql volatile security definer set search_path = '' as $$
declare t bigint := floor(extract(epoch from clock_timestamp())); w bigint := t / p_window; n int;
begin
  insert into vanta.rate_limits as r (key, win, count, expires)
  values (p_name || ':' || p_subject, w, 1, to_timestamp((w + 1) * p_window))
  on conflict (key) do update set
    count = case when r.win = excluded.win then r.count + 1 else 1 end, win = excluded.win, expires = excluded.expires
  returning r.count into n;
  if n > p_limit then perform vanta.fail('rate_limited', 429, 'retry_after=' || greatest(1, (w + 1) * p_window - t)); end if;
end $$;

-- Anonymous rate-limit subject: salted SHA-256 of the client IP, rotating daily. The IP itself is never stored.
create or replace function vanta.client_key() returns text
language plpgsql stable security definer set search_path = '' as $$
declare h json; ip text;
begin
  begin
    h := nullif(current_setting('request.headers', true), '')::json;
  exception when others then h := null;
  end;
  ip := nullif(btrim(coalesce(h ->> 'cf-connecting-ip', h ->> 'x-real-ip', split_part(h ->> 'x-forwarded-for', ',', 1))), '');
  if ip is null then return 'noip'; end if;
  return left(encode(sha256(convert_to(ip || '|' || (select value from vanta.settings where key = 'ip_salt') || '|'
                                       || (now() at time zone 'utc')::date::text, 'UTF8')), 'hex'), 32);
end $$;

-- Direct database sessions (SQL editor, migrations) and the service role may call the bot functions.
create or replace function vanta.require_service() returns void
language plpgsql stable set search_path = '' as $$
declare r text := nullif(current_setting('request.jwt.claims', true), '')::json ->> 'role';
begin
  if nullif(current_setting('request.jwt.claims', true), '') is null then return; end if;
  if r is distinct from 'service_role' then perform vanta.fail('forbidden', 403); end if;
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- profiles from auth.users (Discord metadata: provider_id, full_name, custom_claims.global_name, avatar_url)
create or replace function vanta.upsert_profile(p_id uuid, meta jsonb) returns void
language plpgsql volatile security definer set search_path = '' as $$
declare did text; uname text; av text;
begin
  meta := coalesce(meta, '{}'::jsonb);
  did := coalesce(meta ->> 'provider_id', meta ->> 'sub');
  if did is null or did !~ '^[0-9]{5,25}$' then did := null; end if;
  uname := left(coalesce(nullif(btrim(meta -> 'custom_claims' ->> 'global_name'), ''), nullif(btrim(meta ->> 'full_name'), ''),
                         nullif(btrim(meta ->> 'user_name'), ''), 'user'), 100);
  av := meta ->> 'avatar_url';
  if av is null or av not like 'https://cdn.discordapp.com/%' or char_length(av) > 300 then av := null; end if;
  insert into vanta.profiles as p (id, discord_id, username, avatar_url)
  values (p_id, did, uname, av)
  on conflict (id) do update set discord_id = coalesce(excluded.discord_id, p.discord_id), username = excluded.username,
                                 avatar_url = excluded.avatar_url, updated_at = now();
end $$;

create or replace function vanta.is_discord(app jsonb) returns boolean
language sql immutable set search_path = '' as $$
  select coalesce(app ->> 'provider' = 'discord' or coalesce(app -> 'providers', '[]'::jsonb) ? 'discord', false);
$$;

-- Trigger on auth.users: only Discord sign-ins get a Vanta profile (other users of the same project are left alone).
-- Never blocks a sign-in: problems are logged as warnings.
create or replace function vanta.sync_profile() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  if not vanta.is_discord(new.raw_app_meta_data) and not exists (select 1 from vanta.profiles where id = new.id) then
    return new;
  end if;
  begin
    perform vanta.upsert_profile(new.id, new.raw_user_meta_data);
  exception when others then
    raise warning 'vanta: profile sync failed for %: %', new.id, sqlerrm;
  end;
  return new;
end $$;

drop trigger if exists vanta_profile_sync on auth.users;
create trigger vanta_profile_sync after insert or update of raw_user_meta_data, raw_app_meta_data on auth.users
  for each row execute function vanta.sync_profile();

-- Profile rows for users that signed up before this migration (or whose sync failed).
create or replace function vanta.ensure_profile(p_id uuid) returns void
language plpgsql volatile security definer set search_path = '' as $$
declare meta jsonb;
begin
  if exists (select 1 from vanta.profiles where id = p_id) then return; end if;
  select raw_user_meta_data into meta from auth.users where id = p_id;
  if not found then perform vanta.fail('not_logged_in', 401); end if;
  perform vanta.upsert_profile(p_id, meta);
end $$;

do $$ begin perform vanta.upsert_profile(u.id, u.raw_user_meta_data) from auth.users u where vanta.is_discord(u.raw_app_meta_data); end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- report triggers
create or replace function vanta.reports_before() returns trigger
language plpgsql security definer set search_path = '' as $$
declare uid uuid := auth.uid(); is_banned boolean; rowkey text;
begin
  if tg_op = 'UPDATE' then
    if new.user_id <> old.user_id or new.game_id <> old.game_id or new.cheat_id <> old.cheat_id or new.fingerprint <> old.fingerprint then
      perform vanta.fail('immutable_fields');
    end if;
    new.created_at := old.created_at;
  else
    new.user_id := coalesce(new.user_id, uid);
    new.created_at := now();
  end if;
  new.updated_at := now();
  if new.user_id is null then perform vanta.fail('not_logged_in', 401); end if;

  new.game_id := vanta.check_id(new.game_id, 'game_id');
  new.cheat_id := vanta.check_id(new.cheat_id, 'cheat_id');
  new.fingerprint := vanta.check_fingerprint(new.fingerprint);
  if new.status is null or new.status not in ('works', 'broken') then perform vanta.fail('invalid_status'); end if;
  new.vanta_version := vanta.check_version(new.vanta_version, 'vanta_version');
  new.note := vanta.clean_text(new.note, 300, 'note');
  new.game_version := vanta.clean_text(new.game_version, 80, 'game_version');
  new.game_name := vanta.clean_text(new.game_name, 80, 'game_name');
  new.cheat_name := vanta.clean_text(new.cheat_name, 80, 'cheat_name');

  perform vanta.ensure_profile(new.user_id);
  select p.banned into is_banned from vanta.profiles p where p.id = new.user_id;
  if is_banned then perform vanta.fail('banned', 403); end if;

  -- Per-user limit for requests from the app. An upsert fires BEFORE INSERT and then BEFORE UPDATE for the same
  -- row: count it once.
  if uid is not null then
    rowkey := new.user_id::text || '|' || new.game_id || '|' || new.cheat_id || '|' || new.fingerprint;
    if tg_op = 'INSERT' or coalesce(current_setting('vanta.rl_row', true), '') <> rowkey then
      perform vanta.rate_limit('report-user', uid::text, 30, 3600);
    end if;
    perform set_config('vanta.rl_row', case when tg_op = 'INSERT' then rowkey else '' end, true);
  end if;
  return new;
end $$;

create or replace function vanta.reports_after() returns trigger
language plpgsql security definer set search_path = '' as $$
declare st vanta.cheat_state;
begin
  insert into vanta.fingerprints (game_id, fingerprint, game_version) values (new.game_id, new.fingerprint, new.game_version)
  on conflict do nothing;
  insert into vanta.cheat_state as c (game_id, cheat_id, fingerprint, game_name, cheat_name, game_version)
  values (new.game_id, new.cheat_id, new.fingerprint, new.game_name, new.cheat_name, new.game_version)
  on conflict (game_id, cheat_id, fingerprint) do update set
    game_name = coalesce(excluded.game_name, c.game_name), cheat_name = coalesce(excluded.cheat_name, c.cheat_name),
    game_version = coalesce(excluded.game_version, c.game_version), updated_at = now()
  returning * into st;
  -- A "broken" report from a Vanta version that should contain the fix reopens the issue.
  if new.status = 'broken' and st.status = 'fixed'
     and (st.fixed_in_version is null or vanta.cmp_version(new.vanta_version, st.fixed_in_version) >= 0) then
    update vanta.cheat_state set status = 'open', status_changed = now(), updated_at = now() where id = st.id;
    perform set_config('vanta.reopened', '1', true);
  end if;
  insert into vanta.events (type, state_id, status) values ('report', st.id, new.status);
  return null;
end $$;

create or replace function vanta.reports_before_delete() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  -- Withdrawing counts against the same per-user limit, except while an account is being deleted.
  if auth.uid() is not null and coalesce(current_setting('vanta.deleting', true), '') <> '1' then
    perform vanta.rate_limit('report-user', auth.uid()::text, 30, 3600);
  end if;
  return old;
end $$;

create or replace function vanta.reports_after_delete() returns trigger
language plpgsql security definer set search_path = '' as $$
declare sid bigint;
begin
  select id into sid from vanta.cheat_state where game_id = old.game_id and cheat_id = old.cheat_id and fingerprint = old.fingerprint;
  if sid is not null then insert into vanta.events (type, state_id) values ('withdrawn', sid); end if;
  return null;
end $$;

drop trigger if exists reports_before on vanta.reports;
create trigger reports_before before insert or update on vanta.reports for each row execute function vanta.reports_before();
drop trigger if exists reports_after on vanta.reports;
create trigger reports_after after insert or update on vanta.reports for each row execute function vanta.reports_after();
drop trigger if exists reports_before_delete on vanta.reports;
create trigger reports_before_delete before delete on vanta.reports for each row execute function vanta.reports_before_delete();
drop trigger if exists reports_after_delete on vanta.reports;
create trigger reports_after_delete after delete on vanta.reports for each row execute function vanta.reports_after_delete();

-- ---------------------------------------------------------------------------------------------------------------------
-- Priority score per (game, cheat, fingerprint); see supabase/README.md ("Prioriteitsscore").
--   base   = max(0, sum(decay(broken)) - 0.5 * sum(decay(works)))      decay = 0.5 ^ (age_days / 14)
--   score  = base * newest(1.5 if newest fingerprint) * spike(1..2) * demand(1 + 0.1*log10(1 + enables 7d))
-- spike: while the fingerprint is at most 7 days old, broken reports within 72 h of its first sighting;
-- 5 of them give the full x2. Closed states (fixed/cant_reproduce/duplicate) score 0.
create or replace function vanta.decay(age_sec double precision) returns double precision
language sql immutable set search_path = '' as $$
  select power(0.5, greatest(0, age_sec) / 86400.0 / 14.0);
$$;

-- Reports of one cheat_state from users that are not banned, with the "counts" flag.
create or replace function vanta.state_reports(p_id bigint)
returns table (status text, note text, vanta_version text, created_at timestamptz, updated_at timestamptz,
               discord_id text, username text, counted boolean)
language sql stable security definer set search_path = '' as $$
  select r.status, r.note, r.vanta_version, r.created_at, r.updated_at, p.discord_id, p.username,
         vanta.report_counts(st.status, st.status_changed, st.fixed_in_version, r.updated_at, r.vanta_version)
    from vanta.cheat_state st
    join vanta.reports r on r.game_id = st.game_id and r.cheat_id = st.cheat_id and r.fingerprint = st.fingerprint
    join vanta.profiles p on p.id = r.user_id
   where st.id = p_id and not p.banned;
$$;

-- Numeric sort key for versions ("0.2.10" -> {0,2,10}).
create or replace function vanta.version_key(v text) returns numeric[]
language sql immutable set search_path = '' as $$
  select array(select coalesce(substring(p from '^[0-9]+')::numeric, 0)
                 from unnest(regexp_split_to_array(coalesce(v, ''), '[.+-]')) with ordinality as u(p, n) order by n);
$$;

-- Summary of one cheat_state as JSON (same shape the bot renders). Timestamps are unix seconds.
create or replace function vanta.state_summary(p_id bigint, p_reporters boolean default false) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare
  st vanta.cheat_state; t timestamptz := now(); fp_first timestamptz; fp_newest timestamptz; usage7 numeric;
  a record; newest boolean; fresh boolean; spike double precision; demand double precision; base double precision;
  score numeric; notes jsonb; versions jsonb; reporters jsonb; out jsonb;
begin
  select * into st from vanta.cheat_state where id = p_id;
  if not found then return null; end if;
  select first_seen into fp_first from vanta.fingerprints where game_id = st.game_id and fingerprint = st.fingerprint;
  select max(first_seen) into fp_newest from vanta.fingerprints where game_id = st.game_id;
  newest := fp_first is not null and fp_first = fp_newest;
  select coalesce(sum(count), 0) into usage7 from vanta.usage_daily
   where game_id = st.game_id and cheat_id = st.cheat_id and day >= (t at time zone 'utc')::date - 6;

  select count(*) filter (where rs.counted and rs.status = 'broken') as nb,
         count(*) filter (where rs.counted and rs.status = 'works') as nw,
         coalesce(sum(vanta.decay(extract(epoch from t - rs.updated_at))) filter (where rs.counted and rs.status = 'broken'), 0) as bw,
         coalesce(sum(vanta.decay(extract(epoch from t - rs.updated_at))) filter (where rs.counted and rs.status = 'works'), 0) as ww,
         count(*) filter (where rs.counted and rs.status = 'broken' and fp_first is not null
                          and extract(epoch from rs.created_at - fp_first) <= 72 * 3600) as early,
         count(*) filter (where rs.status = 'broken') as tb,
         count(*) filter (where rs.status = 'works') as tw,
         max(rs.updated_at) as last_report
    into a from vanta.state_reports(p_id) rs;

  base := greatest(0, a.bw - 0.5 * a.ww);
  fresh := fp_first is not null and extract(epoch from t - fp_first) <= 7 * 86400;
  spike := case when fresh then 1 + 1.0 * least(1, a.early / 5.0) else 1 end;
  demand := 1 + 0.1 * log(1 + greatest(0, usage7)::double precision);
  score := round((base * (case when newest then 1.5 else 1 end) * spike * demand)::numeric, 2);

  select coalesce(jsonb_agg(jsonb_build_object('note', x.note, 'vanta_version', x.vanta_version,
                                               'updated', floor(extract(epoch from x.updated_at))::bigint) order by x.updated_at desc), '[]')
    into notes
    from (select * from vanta.state_reports(p_id) rs where rs.counted and rs.status = 'broken' and rs.note is not null
           order by rs.updated_at desc limit 3) x;
  select coalesce(jsonb_agg(y.v order by y.k desc), '[]') into versions
    from (select d.v, vanta.version_key(d.v) as k
            from (select distinct rs.vanta_version as v from vanta.state_reports(p_id) rs where rs.counted) d
           order by 2 desc limit 5) y;

  out := jsonb_build_object(
    'id', st.id, 'game_id', st.game_id, 'cheat_id', st.cheat_id, 'fingerprint', st.fingerprint, 'game_version', st.game_version,
    'game_name', st.game_name, 'cheat_name', st.cheat_name, 'status', st.status, 'fixed_in_version', st.fixed_in_version,
    'message_id', st.message_id, 'broken', a.nb, 'works', a.nw, 'total_broken', a.tb, 'total_works', a.tw,
    'score', case when st.status = 'open' then score else 0 end,
    'score_detail', jsonb_build_object('score', score, 'broken', a.nb, 'works', a.nw, 'brokenWeighted', a.bw, 'worksWeighted', a.ww,
                                       'spike', spike, 'newest', newest, 'demand', demand),
    'notes', notes, 'vanta_versions', versions,
    'last_report', floor(extract(epoch from a.last_report))::bigint,
    'newest_fingerprint', newest);
  if p_reporters then
    select coalesce(jsonb_agg(jsonb_build_object('discord_id', x.discord_id, 'username', x.username, 'status', x.status,
                                                 'updated', floor(extract(epoch from x.updated_at))::bigint, 'note', x.note)
                              order by x.updated_at desc), '[]')
      into reporters from (select * from vanta.state_reports(p_id) rs order by rs.updated_at desc limit 25) x;
    out := out || jsonb_build_object('reporters', reporters);
  end if;
  return out;
end $$;

-- Counts shown in the app: no identities.
create or replace function vanta.community_entry(p_game text, p_cheat text, p_fp text) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare sid bigint; s jsonb;
begin
  select id into sid from vanta.cheat_state where game_id = p_game and cheat_id = p_cheat and fingerprint = p_fp;
  if sid is null then return null; end if;
  s := vanta.state_summary(sid);
  return jsonb_build_object('works', s -> 'works', 'broken', s -> 'broken', 'status', s -> 'status', 'fixed_in_version', s -> 'fixed_in_version');
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- RPC for the Vanta app

-- Report (or replace) the caller's vote. Runs with the caller's rights, so the RLS policies apply.
create or replace function public.vanta_submit_report(p_game_id text, p_cheat_id text, p_fingerprint text, p_status text,
  p_vanta_version text, p_note text default null, p_game_version text default null, p_game_name text default null,
  p_cheat_name text default null) returns jsonb
language plpgsql volatile security invoker set search_path = '' as $$
declare uid uuid := auth.uid(); replaced boolean; fp text;
begin
  if uid is null then perform vanta.fail('not_logged_in', 401); end if;
  fp := vanta.check_fingerprint(p_fingerprint);
  perform set_config('vanta.reopened', '', true);
  insert into vanta.reports as r (user_id, game_id, cheat_id, fingerprint, status, vanta_version, note, game_version, game_name, cheat_name)
  values (uid, p_game_id, p_cheat_id, fp, p_status, p_vanta_version, p_note, p_game_version, p_game_name, p_cheat_name)
  on conflict (user_id, game_id, cheat_id, fingerprint) do update set
    status = excluded.status, note = excluded.note, vanta_version = excluded.vanta_version,
    game_version = coalesce(excluded.game_version, r.game_version), game_name = coalesce(excluded.game_name, r.game_name),
    cheat_name = coalesce(excluded.cheat_name, r.cheat_name)
  returning (r.xmax::text <> '0') into replaced;
  return jsonb_build_object('ok', true, 'replaced', replaced, 'reopened', coalesce(current_setting('vanta.reopened', true), '') = '1',
                            'community', vanta.community_entry(p_game_id, p_cheat_id, fp));
end $$;

-- Withdraw the caller's vote ("Niet getest" / "Standaard").
create or replace function public.vanta_withdraw_report(p_game_id text, p_cheat_id text, p_fingerprint text) returns jsonb
language plpgsql volatile security invoker set search_path = '' as $$
declare uid uuid := auth.uid(); n int;
begin
  if uid is null then perform vanta.fail('not_logged_in', 401); end if;
  delete from vanta.reports
   where user_id = uid and game_id = vanta.check_id(p_game_id, 'game_id') and cheat_id = vanta.check_id(p_cheat_id, 'cheat_id')
     and fingerprint = vanta.check_fingerprint(p_fingerprint);
  get diagnostics n = row_count;
  return jsonb_build_object('ok', true, 'removed', n > 0);
end $$;

-- Per-cheat counts for one game version (Notes tab). Public; limited per client.
create or replace function public.vanta_community(p_game_id text, p_fingerprint text default null) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare g text; fp text; cheats jsonb;
begin
  perform vanta.rate_limit('read', vanta.client_key(), 240, 600);
  g := vanta.check_id(p_game_id, 'game_id');
  if p_fingerprint is null or p_fingerprint = '' then
    select f.fingerprint into fp from vanta.fingerprints f where f.game_id = g order by f.first_seen desc limit 1;
  else
    fp := vanta.check_fingerprint(p_fingerprint);
  end if;
  select coalesce(jsonb_object_agg(c.cheat_id, vanta.community_entry(c.game_id, c.cheat_id, c.fingerprint)), '{}')
    into cheats from vanta.cheat_state c where c.game_id = g and c.fingerprint = fp;
  return jsonb_build_object('game_id', g, 'fingerprint', fp, 'cheats', cheats);
end $$;

-- Opt-in anonymous usage counts: {"cheat_id": n, ...} per game. Only daily totals are stored; no user id, no IP.
create or replace function public.vanta_count_usage(p_game_id text, p_counts jsonb) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare g text; k text; v jsonb; n int; d date := (now() at time zone 'utc')::date;
begin
  perform vanta.rate_limit('usage', vanta.client_key(), 30, 3600);
  perform vanta.rate_limit('usage-all', 'all', 20000, 3600);
  g := vanta.check_id(p_game_id, 'game_id');
  if p_counts is null or jsonb_typeof(p_counts) <> 'object' then perform vanta.fail('invalid_counts'); end if;
  if (select count(*) from jsonb_object_keys(p_counts)) not between 1 and 100 then perform vanta.fail('invalid_counts'); end if;
  for k, v in select * from jsonb_each(p_counts) loop
    perform vanta.check_id(k, 'cheat_id');
    if jsonb_typeof(v) <> 'number' or (v #>> '{}') !~ '^[0-9]+$' then perform vanta.fail('invalid_counts'); end if;
    n := (v #>> '{}')::int;
    if n < 1 or n > 1000 then perform vanta.fail('invalid_counts'); end if;
  end loop;
  insert into vanta.usage_daily as u (day, game_id, cheat_id, count)
  select d, g, e.key, (e.value #>> '{}')::int from jsonb_each(p_counts) e
  on conflict (day, game_id, cheat_id) do update set count = u.count + excluded.count;
  return jsonb_build_object('ok', true);
end $$;

-- Delete the caller's account: all reports, the profile and the Supabase auth user (sessions go with it).
create or replace function public.vanta_delete_my_account() returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare uid uuid := auth.uid(); n int;
begin
  if uid is null then perform vanta.fail('not_logged_in', 401); end if;
  perform set_config('vanta.deleting', '1', true);
  select count(*) into n from vanta.reports where user_id = uid;
  delete from auth.users where id = uid;            -- cascades to profiles -> reports (and auth sessions/identities)
  delete from vanta.profiles where id = uid;       -- in case the auth user was already gone
  perform set_config('vanta.deleting', '', true);
  return jsonb_build_object('ok', true, 'reports', n);
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- privileges: helpers are internal; the app may call exactly these RPCs.
revoke all on all functions in schema vanta from public, anon, authenticated;
grant execute on all functions in schema vanta to service_role;
-- submit_report/withdraw_report run with the caller's rights and use these:
grant execute on function vanta.can_report(), vanta.fail(text, int, text), vanta.check_id(text, text),
  vanta.check_fingerprint(text), vanta.community_entry(text, text, text) to authenticated;

revoke all on function public.vanta_submit_report(text, text, text, text, text, text, text, text, text) from public, anon;
revoke all on function public.vanta_withdraw_report(text, text, text) from public, anon;
revoke all on function public.vanta_delete_my_account() from public, anon;
revoke all on function public.vanta_community(text, text) from public;
revoke all on function public.vanta_count_usage(text, jsonb) from public;
grant execute on function public.vanta_submit_report(text, text, text, text, text, text, text, text, text) to authenticated, service_role;
grant execute on function public.vanta_withdraw_report(text, text, text) to authenticated, service_role;
grant execute on function public.vanta_delete_my_account() to authenticated;
grant execute on function public.vanta_community(text, text) to anon, authenticated, service_role;
grant execute on function public.vanta_count_usage(text, jsonb) to anon, authenticated, service_role;

-- The trigger on auth.users runs as supabase_auth_admin.
do $$ begin
  if exists (select 1 from pg_roles where rolname = 'supabase_auth_admin') then
    grant usage on schema vanta to supabase_auth_admin;
  end if;
end $$;

-- ===== 20260927120200_bot_api.sql =====
-- Vanta community reports: functions for the Discord bot. Only the service role (secret key) may call these; the
-- bot checks the admin Discord ids itself. Results use the same JSON shapes as vanta.state_summary().

create or replace function vanta.ranked(p_game text, p_include_closed boolean default false) returns jsonb
language sql stable security definer set search_path = '' as $$
  select coalesce(jsonb_agg(x.s order by (x.s ->> 'score')::numeric desc, (x.s ->> 'broken')::int desc), '[]')
    from (select vanta.state_summary(c.id) as s
            from vanta.cheat_state c
           where (p_game is null or c.game_id = p_game) and (p_include_closed or c.status = 'open')
           order by c.updated_at desc limit 500) x;
$$;

-- Change feed: one item per cheat_state touched after the cursor.
create or replace function public.vanta_bot_events(p_after bigint default 0, p_limit int default 50) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare lim int := least(200, greatest(1, coalesce(p_limit, 50))); last_id bigint; n int; items jsonb;
begin
  perform vanta.require_service();
  with evs as (
    select e.* from vanta.events e where e.id > coalesce(p_after, 0) order by e.id limit lim
  ), by_state as (
    select state_id, max(id) as last_event, jsonb_agg(type order by id) as types,
           bool_or(type = 'report' and status = 'broken') as has_broken
      from evs group by state_id
  )
  select (select max(id) from evs), (select count(*) from evs),
         coalesce(jsonb_agg(jsonb_build_object('last_event', b.last_event, 'types', b.types, 'has_broken', b.has_broken,
                                               'state', vanta.state_summary(b.state_id)) order by b.last_event), '[]')
    into last_id, n, items
    from by_state b join vanta.cheat_state c on c.id = b.state_id;
  return jsonb_build_object('cursor', coalesce(last_id, coalesce(p_after, 0)), 'items', items, 'more', n = lim);
end $$;

create or replace function public.vanta_bot_state(p_state_id bigint) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare s jsonb;
begin
  perform vanta.require_service();
  s := vanta.state_summary(p_state_id, true);
  if s is null then perform vanta.fail('not_found', 404); end if;
  return s;
end $$;

create or replace function public.vanta_bot_set_message(p_state_id bigint, p_message_id text) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
begin
  perform vanta.require_service();
  if p_message_id is not null and p_message_id !~ '^[0-9]{5,25}$' then perform vanta.fail('invalid_message_id'); end if;
  update vanta.cheat_state set message_id = p_message_id where id = p_state_id;
  if not found then perform vanta.fail('not_found', 404); end if;
  return jsonb_build_object('ok', true);
end $$;

-- Triage from Discord: by state id (buttons) or by game + cheat (+ fingerprint) (/fixed).
create or replace function public.vanta_bot_set_status(p_status text, p_state_id bigint default null, p_game_id text default null,
  p_cheat_id text default null, p_fingerprint text default null, p_fixed_in_version text default null) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare fixed_in text; ids bigint[]; sid bigint; updated jsonb := '[]';
begin
  perform vanta.require_service();
  if p_status is null or p_status not in ('open', 'fixed', 'cant_reproduce', 'duplicate') then perform vanta.fail('invalid_status'); end if;
  fixed_in := case when p_fixed_in_version is null or p_fixed_in_version = '' then null
                   else vanta.check_version(p_fixed_in_version, 'fixed_in_version') end;
  if p_state_id is not null then
    ids := array(select id from vanta.cheat_state where id = p_state_id);
  else
    ids := array(select id from vanta.cheat_state
                  where game_id = vanta.check_id(p_game_id, 'game_id') and cheat_id = vanta.check_id(p_cheat_id, 'cheat_id')
                    and (case when coalesce(p_fingerprint, '') = '' then status = 'open'
                              else fingerprint = vanta.check_fingerprint(p_fingerprint) end));
  end if;
  if cardinality(ids) = 0 then perform vanta.fail('not_found', 404); end if;
  foreach sid in array ids loop
    update vanta.cheat_state set status = p_status, fixed_in_version = case when p_status = 'fixed' then fixed_in end,
           status_changed = now(), updated_at = now()
     where id = sid;
    insert into vanta.events (type, state_id) values ('status', sid);
    updated := updated || jsonb_build_array(vanta.state_summary(sid));
  end loop;
  return jsonb_build_object('ok', true, 'updated', updated);
end $$;

-- Ban (or unban) by Discord id. Banning also ends the user's Supabase sessions.
create or replace function public.vanta_bot_ban(p_discord_id text, p_banned boolean default true) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare pid uuid; uname text;
begin
  perform vanta.require_service();
  if p_discord_id is null or p_discord_id !~ '^[0-9]{5,25}$' then perform vanta.fail('invalid_discord_id'); end if;
  update vanta.profiles set banned = coalesce(p_banned, true), updated_at = now() where discord_id = p_discord_id
  returning id, username into pid, uname;
  if pid is null then perform vanta.fail('user_not_found', 404); end if;
  if coalesce(p_banned, true) then
    begin
      delete from auth.sessions where user_id = pid;
    exception when others then
      raise warning 'vanta: could not end sessions for %: %', pid, sqlerrm;   -- the ban itself still applies
    end;
  end if;
  return jsonb_build_object('ok', true, 'username', uname, 'banned', coalesce(p_banned, true));
end $$;

create or replace function public.vanta_bot_top(p_game text default null, p_limit int default 10) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare g text := case when coalesce(p_game, '') = '' then null else vanta.check_id(p_game, 'game') end;
begin
  perform vanta.require_service();
  return jsonb_build_object('items', coalesce((
    select jsonb_agg(x.s order by x.o) from (
      select s, o from jsonb_array_elements(vanta.ranked(g)) with ordinality as e(s, o)
       where (s ->> 'broken')::int > 0 order by o limit least(25, greatest(1, coalesce(p_limit, 10)))) x), '[]'));
end $$;

create or replace function public.vanta_bot_cheat(p_game text, p_cheat text) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
  perform vanta.require_service();
  return jsonb_build_object('items', coalesce((
    select jsonb_agg(vanta.state_summary(c.id, true) order by c.updated_at desc) from (
      select id, updated_at from vanta.cheat_state
       where game_id = vanta.check_id(p_game, 'game') and cheat_id = vanta.check_id(p_cheat, 'cheat')
       order by updated_at desc limit 10) c), '[]'));
end $$;

create or replace function public.vanta_bot_game(p_game text) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare g text := vanta.check_id(p_game, 'game');
begin
  perform vanta.require_service();
  return jsonb_build_object(
    'items', coalesce((select jsonb_agg(s order by o) from jsonb_array_elements(vanta.ranked(g, true)) with ordinality as e(s, o) where o <= 25), '[]'),
    'usage7d', coalesce((select jsonb_agg(jsonb_build_object('cheat_id', u.cheat_id, 'n', u.n) order by u.n desc) from (
       select cheat_id, sum(count) as n from vanta.usage_daily
        where game_id = g and day >= (now() at time zone 'utc')::date - 6
        group by cheat_id order by n desc limit 10) u), '[]'));
end $$;

create or replace function vanta.stats(p_days int) returns jsonb
language sql stable security definer set search_path = '' as $$
  with p as (select greatest(1, least(31, coalesce(p_days, 7))) as days,
                    now() - make_interval(days => greatest(1, least(31, coalesce(p_days, 7)))) as since)
  select jsonb_build_object(
    'days', p.days,
    'users', (select count(*) from vanta.profiles where not banned),
    'new_users', (select count(*) from vanta.profiles where created_at >= p.since),
    'reports', (select count(*) from vanta.reports),
    'reports_period', (select count(*) from vanta.reports where updated_at >= p.since),
    'broken_period', (select count(*) from vanta.reports where updated_at >= p.since and status = 'broken'),
    'open', (select count(*) from vanta.cheat_state c where c.status = 'open' and exists (
               select 1 from vanta.reports r where r.game_id = c.game_id and r.cheat_id = c.cheat_id
                  and r.fingerprint = c.fingerprint and r.status = 'broken')),
    'fixed_period', (select count(*) from vanta.cheat_state where status = 'fixed' and status_changed >= p.since),
    'usage_period', (select coalesce(sum(count), 0) from vanta.usage_daily where day >= (p.since at time zone 'utc')::date))
  from p;
$$;

create or replace function public.vanta_bot_stats() returns jsonb
language plpgsql stable security definer set search_path = '' as $$
begin
  perform vanta.require_service();
  return vanta.stats(7);
end $$;

create or replace function public.vanta_bot_digest(p_days int default 7) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare st jsonb := vanta.stats(p_days); since timestamptz;
begin
  perform vanta.require_service();
  since := now() - make_interval(days => (st ->> 'days')::int);
  return st || jsonb_build_object(
    'top', coalesce((select jsonb_agg(s order by o) from (
              select s, o from jsonb_array_elements(vanta.ranked(null)) with ordinality as e(s, o)
               where (s ->> 'broken')::int > 0 order by o limit 10) x), '[]'),
    'fixed', coalesce((select jsonb_agg(jsonb_build_object('game_id', f.game_id, 'cheat_id', f.cheat_id, 'game_name', f.game_name,
                                                           'cheat_name', f.cheat_name, 'fixed_in_version', f.fixed_in_version)
                                        order by f.status_changed desc) from (
              select * from vanta.cheat_state where status = 'fixed' and status_changed >= since
               order by status_changed desc limit 10) f), '[]'));
end $$;

-- Housekeeping, called by the bot about once an hour: expired rate-limit rows and events older than 30 days.
create or replace function public.vanta_bot_cleanup() returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare a int; b int;
begin
  perform vanta.require_service();
  delete from vanta.rate_limits where expires < now();
  get diagnostics a = row_count;
  delete from vanta.events where created < now() - interval '30 days';
  get diagnostics b = row_count;
  return jsonb_build_object('rate_limits', a, 'events', b);
end $$;

-- privileges: service role only
do $$
declare f text;
begin
  foreach f in array array[
    'public.vanta_bot_events(bigint, int)', 'public.vanta_bot_state(bigint)', 'public.vanta_bot_set_message(bigint, text)',
    'public.vanta_bot_set_status(text, bigint, text, text, text, text)', 'public.vanta_bot_ban(text, boolean)',
    'public.vanta_bot_top(text, int)', 'public.vanta_bot_cheat(text, text)', 'public.vanta_bot_game(text)', 'public.vanta_bot_stats()',
    'public.vanta_bot_digest(int)', 'public.vanta_bot_cleanup()'] loop
    execute format('revoke all on function %s from public, anon, authenticated', f);
    execute format('grant execute on function %s to service_role', f);
  end loop;
end $$;
revoke all on function vanta.ranked(text, boolean), vanta.stats(int) from public, anon, authenticated;
grant execute on function vanta.ranked(text, boolean), vanta.stats(int) to service_role;

-- ===== 20260928120000_game_requests_admin.sql =====
-- Vanta: game requests (players vote for the next supported game) and the API for the admin dashboard on the website.
--
-- Safe to run on top of the existing setup and safe to run again. Adds only tables in the "vanta" schema and functions
-- named public.vanta_* / vanta.*; nothing else is created, changed or dropped.
--
-- Game requests: one row per Steam app id, one vote per user per game. Signed-in (Discord) users request/vote through
-- public.vanta_request_game and public.vanta_unvote_game; the list with vote counts is public (public.vanta_game_requests).
-- Banned users cannot request or vote and their votes do not count.
--
-- Admin: vanta.admins holds Discord user ids. A caller is admin when their Supabase auth user has a Discord identity
-- (auth.identities.provider = 'discord') whose provider_id is in vanta.admins. auth.identities is written only by
-- Supabase Auth from the Discord OAuth response; unlike raw_user_meta_data (user_metadata) a user cannot edit it.
-- Every public.vanta_admin_* function checks this itself (SECURITY DEFINER); the tables stay unreadable for clients.

create schema if not exists vanta;

-- ---------------------------------------------------------------------------------------------------------------------
-- tables
create table if not exists vanta.game_requests (
  id              bigint generated always as identity primary key,
  steam_appid     integer not null unique check (steam_appid between 1 and 2147483647),
  name            text not null check (char_length(name) between 1 and 120),
  cover_url       text check (cover_url is null or char_length(cover_url) <= 400),
  status          text not null default 'open' check (status in ('open', 'planned', 'in_progress', 'added', 'rejected')),
  admin_note      text check (char_length(admin_note) <= 500),
  created_by      uuid references vanta.profiles (id) on delete set null,
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  status_changed  timestamptz
);
comment on table vanta.game_requests is 'Requested games (Steam app id). admin_note is shown to players.';

create table if not exists vanta.request_votes (
  request_id  bigint not null references vanta.game_requests (id) on delete cascade,
  user_id     uuid   not null references vanta.profiles (id) on delete cascade,
  created_at  timestamptz not null default now(),
  primary key (request_id, user_id)
);
create index if not exists request_votes_user on vanta.request_votes (user_id);
create index if not exists request_votes_created on vanta.request_votes (created_at);

create table if not exists vanta.admins (
  discord_id  text primary key check (discord_id ~ '^[0-9]{5,25}$'),
  added_at    timestamptz not null default now()
);
insert into vanta.admins (discord_id) values ('348805709837762561') on conflict (discord_id) do nothing;

alter table vanta.game_requests enable row level security;
alter table vanta.request_votes enable row level security;
alter table vanta.admins        enable row level security;
revoke all on vanta.game_requests, vanta.request_votes, vanta.admins from public, anon, authenticated;
grant all on vanta.game_requests, vanta.request_votes, vanta.admins to service_role;
grant usage on sequence vanta.game_requests_id_seq to service_role;
-- No policies: anon/authenticated have no table access at all; everything goes through the functions below.

-- ---------------------------------------------------------------------------------------------------------------------
-- profiles: take the Discord id from auth.identities when it exists (not user-editable), else from the metadata.
create or replace function vanta.upsert_profile(p_id uuid, meta jsonb) returns void
language plpgsql volatile security definer set search_path = '' as $$
declare did text; uname text; av text;
begin
  meta := coalesce(meta, '{}'::jsonb);
  select i.provider_id into did from auth.identities i where i.user_id = p_id and i.provider = 'discord' limit 1;
  did := coalesce(did, meta ->> 'provider_id', meta ->> 'sub');
  if did is null or did !~ '^[0-9]{5,25}$' then did := null; end if;
  uname := left(coalesce(nullif(btrim(meta -> 'custom_claims' ->> 'global_name'), ''), nullif(btrim(meta ->> 'full_name'), ''),
                         nullif(btrim(meta ->> 'user_name'), ''), 'user'), 100);
  av := meta ->> 'avatar_url';
  if av is null or av not like 'https://cdn.discordapp.com/%' or char_length(av) > 300 then av := null; end if;
  insert into vanta.profiles as p (id, discord_id, username, avatar_url)
  values (p_id, did, uname, av)
  on conflict (id) do update set discord_id = coalesce(excluded.discord_id, p.discord_id), username = excluded.username,
                                 avatar_url = excluded.avatar_url, updated_at = now();
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- helpers
create or replace function vanta.is_admin(p_uid uuid) returns boolean
language sql stable security definer set search_path = '' as $$
  select p_uid is not null
     and exists (select 1 from auth.identities i join vanta.admins a on a.discord_id = i.provider_id
                  where i.user_id = p_uid and i.provider = 'discord')
     and not exists (select 1 from vanta.profiles p where p.id = p_uid and p.banned);
$$;

-- API callers (anon/authenticated) must be admin; the service role and direct database sessions (SQL editor) pass.
create or replace function vanta.require_admin() returns void
language plpgsql volatile security definer set search_path = '' as $$
declare claims text := nullif(current_setting('request.jwt.claims', true), ''); r text := coalesce(nullif(current_setting('role', true), ''), 'none');
begin
  if claims is null and r not in ('anon', 'authenticated') then return; end if;
  if claims is not null and (claims::json ->> 'role') = 'service_role' then return; end if;
  if not vanta.is_admin(auth.uid()) then perform vanta.fail('forbidden', 403); end if;
  perform vanta.rate_limit('admin', auth.uid()::text, 1200, 600);
end $$;

-- Steam CDN image URL or null (never fails: a bad cover just isn't stored).
create or replace function vanta.check_cover(v text) returns text
language sql immutable set search_path = '' as $$
  select case when v is not null and char_length(v) <= 400
               and v ~ '^https://([a-z0-9-]+\.)*(steamstatic\.com|akamaihd\.net)/[A-Za-z0-9._~/%?=&+-]+$' then v end;
$$;

create or replace function vanta.check_request_status(v text) returns text
language plpgsql immutable set search_path = '' as $$
begin
  if v is null or v not in ('open', 'planned', 'in_progress', 'added', 'rejected') then perform vanta.fail('invalid_status'); end if;
  return v;
end $$;

-- Requests as JSON, most votes first. p_filter: null/'active' (open, planned, in progress), 'all' or one status.
create or replace function vanta.request_list(p_filter text, p_limit int, p_uid uuid, p_admin boolean, p_appid int default null)
returns jsonb language sql stable security definer set search_path = '' as $$
  select coalesce(jsonb_agg(x.j order by x.votes desc, x.created_at asc, x.id), '[]') from (
    select r.id, r.created_at, v.votes, jsonb_build_object(
        'appid', r.steam_appid, 'name', r.name, 'cover_url', r.cover_url, 'status', r.status, 'note', r.admin_note,
        'votes', v.votes, 'votes_7d', v.votes_7d,
        'voted', p_uid is not null and exists (select 1 from vanta.request_votes mv where mv.request_id = r.id and mv.user_id = p_uid),
        'created', floor(extract(epoch from r.created_at))::bigint, 'updated', floor(extract(epoch from r.updated_at))::bigint)
      || case when p_admin then jsonb_build_object(
           'id', r.id, 'votes_all', v.votes_all,
           'requested_by', (select jsonb_build_object('username', p.username, 'discord_id', p.discord_id, 'banned', p.banned)
                              from vanta.profiles p where p.id = r.created_by)) else '{}'::jsonb end as j
      from vanta.game_requests r
      cross join lateral (
        select count(*) filter (where not p.banned)::int as votes,
               count(*) filter (where not p.banned and rv.created_at >= now() - interval '7 days')::int as votes_7d,
               count(*)::int as votes_all
          from vanta.request_votes rv join vanta.profiles p on p.id = rv.user_id where rv.request_id = r.id) v
     where (p_appid is null or r.steam_appid = p_appid)
       and case when p_filter is null or p_filter = 'active' then r.status in ('open', 'planned', 'in_progress')
                when p_filter = 'all' then true else r.status = p_filter end
     order by v.votes desc, r.created_at asc, r.id
     limit greatest(1, least(500, coalesce(p_limit, 50)))
  ) x;
$$;

create or replace function vanta.request_one(p_id bigint, p_uid uuid) returns jsonb
language sql stable security definer set search_path = '' as $$
  select vanta.request_list('all', 1, p_uid, false, (select steam_appid from vanta.game_requests where id = p_id)) -> 0;
$$;

-- ---------------------------------------------------------------------------------------------------------------------
-- game requests: API for the app (and anyone with the publishable key for the list)

-- Top requests. Public; the "voted" flag is set when the caller is signed in.
create or replace function public.vanta_game_requests(p_status text default null, p_limit int default 50) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare uid uuid := auth.uid();
begin
  perform vanta.rate_limit('read', vanta.client_key(), 240, 600);
  if p_status is not null and p_status not in ('active', 'all') then perform vanta.check_request_status(p_status); end if;
  return jsonb_build_object('items', vanta.request_list(p_status, least(100, greatest(1, coalesce(p_limit, 50))), uid, false),
                            'logged_in', uid is not null);
end $$;

-- Request a game (first time) or vote for it. Name/cover are only used when the request is new.
create or replace function public.vanta_request_game(p_appid integer, p_name text default null, p_cover_url text default null) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare uid uuid := auth.uid(); rid bigint; st text; nm text; created boolean := false; n int;
begin
  if uid is null then perform vanta.fail('not_logged_in', 401); end if;
  perform vanta.ensure_profile(uid);
  if exists (select 1 from vanta.profiles where id = uid and banned) then perform vanta.fail('banned', 403); end if;
  if p_appid is null or p_appid < 1 then perform vanta.fail('invalid_appid'); end if;
  perform vanta.rate_limit('request-user', uid::text, 30, 3600);
  select id, status into rid, st from vanta.game_requests where steam_appid = p_appid;
  if rid is null then
    nm := vanta.clean_text(p_name, 120, 'name');
    if nm is null then perform vanta.fail('invalid_name'); end if;
    perform vanta.rate_limit('request-new', uid::text, 10, 86400);
    perform vanta.rate_limit('request-new-all', 'all', 300, 3600);
    insert into vanta.game_requests (steam_appid, name, cover_url, created_by)
    values (p_appid, nm, vanta.check_cover(p_cover_url), uid)
    on conflict (steam_appid) do nothing
    returning id, status into rid, st;
    if rid is null then select id, status into rid, st from vanta.game_requests where steam_appid = p_appid;
    else created := true; end if;
  end if;
  if st in ('added', 'rejected') then perform vanta.fail('request_closed', 409, 'status=' || st); end if;
  insert into vanta.request_votes (request_id, user_id) values (rid, uid) on conflict do nothing;
  get diagnostics n = row_count;
  return jsonb_build_object('ok', true, 'created', created, 'already_voted', n = 0, 'request', vanta.request_one(rid, uid));
end $$;

-- Remove the caller's vote. An open request without votes and without an admin note disappears.
create or replace function public.vanta_unvote_game(p_appid integer) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare uid uuid := auth.uid(); rid bigint; n int;
begin
  if uid is null then perform vanta.fail('not_logged_in', 401); end if;
  if p_appid is null or p_appid < 1 then perform vanta.fail('invalid_appid'); end if;
  perform vanta.rate_limit('request-user', uid::text, 30, 3600);
  select id into rid from vanta.game_requests where steam_appid = p_appid;
  if rid is null then return jsonb_build_object('ok', true, 'removed', false); end if;
  delete from vanta.request_votes where request_id = rid and user_id = uid;
  get diagnostics n = row_count;
  delete from vanta.game_requests r where r.id = rid and r.status = 'open' and r.admin_note is null
     and not exists (select 1 from vanta.request_votes v where v.request_id = rid);
  return jsonb_build_object('ok', true, 'removed', n > 0, 'request', vanta.request_one(rid, uid));
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- admin API (website dashboard)

create or replace function public.vanta_is_admin() returns boolean
language plpgsql stable security definer set search_path = '' as $$
begin
  return vanta.is_admin(auth.uid());
end $$;

-- Report states with score and reporters. p_status: 'open' (default), 'closed', 'all' or one state status.
create or replace function public.vanta_admin_reports(p_game text default null, p_status text default 'open', p_limit int default 100) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare g text; st text := coalesce(nullif(p_status, ''), 'open'); lim int := least(500, greatest(1, coalesce(p_limit, 100)));
begin
  perform vanta.require_admin();
  g := case when coalesce(p_game, '') = '' then null else vanta.check_id(p_game, 'game') end;
  if st not in ('open', 'closed', 'all', 'fixed', 'cant_reproduce', 'duplicate') then perform vanta.fail('invalid_status'); end if;
  return jsonb_build_object(
    'items', coalesce((
      select jsonb_agg(x.s order by (x.s ->> 'score')::numeric desc, (x.s ->> 'broken')::int desc,
                                    coalesce((x.s ->> 'last_report')::bigint, 0) desc)
        from (select vanta.state_summary(c.id, true) as s from vanta.cheat_state c
               where (g is null or c.game_id = g)
                 and case st when 'all' then true when 'closed' then c.status <> 'open' else c.status = st end
               order by c.updated_at desc limit lim) x), '[]'),
    'games', coalesce((
      select jsonb_agg(jsonb_build_object('game_id', y.game_id, 'game_name', y.game_name, 'open', y.open) order by y.game_id)
        from (select c.game_id, max(c.game_name) as game_name, count(*) filter (where c.status = 'open')::int as open
                from vanta.cheat_state c group by c.game_id) y), '[]'),
    'counts', coalesce((select jsonb_object_agg(z.status, z.n) from (
                          select c.status, count(*)::int as n from vanta.cheat_state c where g is null or c.game_id = g group by c.status) z), '{}'));
end $$;

-- Triage a report state (same statuses as the bot: open, fixed [in version], cant_reproduce = won't fix, duplicate).
create or replace function public.vanta_admin_set_report_status(p_state_id bigint, p_status text, p_fixed_in_version text default null) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare fixed_in text;
begin
  perform vanta.require_admin();
  if p_status is null or p_status not in ('open', 'fixed', 'cant_reproduce', 'duplicate') then perform vanta.fail('invalid_status'); end if;
  fixed_in := case when p_status <> 'fixed' or coalesce(p_fixed_in_version, '') = '' then null
                   else vanta.check_version(btrim(p_fixed_in_version), 'fixed_in_version') end;
  update vanta.cheat_state set status = p_status, fixed_in_version = fixed_in, status_changed = now(), updated_at = now()
   where id = p_state_id;
  if not found then perform vanta.fail('not_found', 404); end if;
  insert into vanta.events (type, state_id) values ('status', p_state_id);   -- the bot updates its Discord message
  return jsonb_build_object('ok', true, 'state', vanta.state_summary(p_state_id, true));
end $$;

-- Ban or unban by Discord id (same effect as /ban in Discord). Admins cannot be banned.
create or replace function public.vanta_admin_ban(p_discord_id text, p_banned boolean default true) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare pid uuid; uname text;
begin
  perform vanta.require_admin();
  if p_discord_id is null or p_discord_id !~ '^[0-9]{5,25}$' then perform vanta.fail('invalid_discord_id'); end if;
  if coalesce(p_banned, true) and exists (select 1 from vanta.admins where discord_id = p_discord_id) then
    perform vanta.fail('cannot_ban_admin', 409);
  end if;
  update vanta.profiles set banned = coalesce(p_banned, true), updated_at = now() where discord_id = p_discord_id
  returning id, username into pid, uname;
  if pid is null then perform vanta.fail('user_not_found', 404); end if;
  if coalesce(p_banned, true) then
    begin
      delete from auth.sessions where user_id = pid;
    exception when others then
      raise warning 'vanta: could not end sessions for %: %', pid, sqlerrm;
    end;
  end if;
  return jsonb_build_object('ok', true, 'username', uname, 'banned', coalesce(p_banned, true));
end $$;

create or replace function public.vanta_admin_banned() returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
begin
  perform vanta.require_admin();
  return jsonb_build_object('items', coalesce((
    select jsonb_agg(jsonb_build_object('discord_id', p.discord_id, 'username', p.username,
                                        'since', floor(extract(epoch from p.updated_at))::bigint) order by p.updated_at desc)
      from vanta.profiles p where p.banned), '[]'));
end $$;

-- All requests (including added/rejected) with requester and raw vote counts.
create or replace function public.vanta_admin_requests(p_status text default 'all', p_limit int default 200) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare st text := coalesce(nullif(p_status, ''), 'all');
begin
  perform vanta.require_admin();
  if st not in ('active', 'all') then perform vanta.check_request_status(st); end if;
  return jsonb_build_object('items', vanta.request_list(st, p_limit, auth.uid(), true));
end $$;

-- Change status, player-visible note ('' clears it) and/or name of a request. null = leave unchanged.
create or replace function public.vanta_admin_set_request(p_appid integer, p_status text default null, p_note text default null,
  p_name text default null) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare r vanta.game_requests; nm text;
begin
  perform vanta.require_admin();
  select * into r from vanta.game_requests where steam_appid = p_appid;
  if not found then perform vanta.fail('not_found', 404); end if;
  if p_status is not null then perform vanta.check_request_status(p_status); end if;
  nm := vanta.clean_text(p_name, 120, 'name');
  update vanta.game_requests set
    status = coalesce(p_status, status),
    status_changed = case when p_status is not null and p_status <> status then now() else status_changed end,
    admin_note = case when p_note is null then admin_note else vanta.clean_text(p_note, 500, 'note') end,
    name = coalesce(nm, name),
    updated_at = now()
   where id = r.id;
  return jsonb_build_object('ok', true, 'request', vanta.request_list('all', 1, auth.uid(), true, p_appid) -> 0);
end $$;

create or replace function public.vanta_admin_delete_request(p_appid integer) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare n int;
begin
  perform vanta.require_admin();
  delete from vanta.game_requests where steam_appid = p_appid;
  get diagnostics n = row_count;
  return jsonb_build_object('ok', true, 'removed', n > 0);
end $$;

create or replace function public.vanta_admin_stats(p_days int default 7) returns jsonb
language plpgsql volatile security definer set search_path = '' as $$
declare st jsonb; since timestamptz; d int;
begin
  perform vanta.require_admin();
  st := vanta.stats(p_days);
  d := (st ->> 'days')::int;
  since := now() - make_interval(days => d);
  return st || jsonb_build_object(
    'banned', (select count(*) from vanta.profiles where banned),
    'requests', (select count(*) from vanta.game_requests),
    'requests_active', (select count(*) from vanta.game_requests where status in ('open', 'planned', 'in_progress')),
    'requests_period', (select count(*) from vanta.game_requests where created_at >= since),
    'votes', (select count(*) from vanta.request_votes),
    'votes_period', (select count(*) from vanta.request_votes where created_at >= since),
    'top_requests', vanta.request_list('active', 5, null, false),
    'games', coalesce((select jsonb_agg(jsonb_build_object('game_id', g.game_id, 'game_name', g.game_name, 'open', g.open,
                                                           'reports_period', g.rp, 'usage_period', g.usage) order by g.rp desc, g.game_id)
      from (select c.game_id, max(c.game_name) as game_name, count(*) filter (where c.status = 'open')::int as open,
                   (select count(*) from vanta.reports r where r.game_id = c.game_id and r.updated_at >= since)::int as rp,
                   (select coalesce(sum(u.count), 0) from vanta.usage_daily u where u.game_id = c.game_id
                     and u.day >= (since at time zone 'utc')::date)::bigint as usage
              from vanta.cheat_state c group by c.game_id) g), '[]'),
    'daily', coalesce((select jsonb_agg(jsonb_build_object('day', dd.day, 'reports', dd.reports, 'broken', dd.broken, 'votes', dd.votes) order by dd.day)
      from (select s.day::date as day,
                   (select count(*) from vanta.reports r where (r.updated_at at time zone 'utc')::date = s.day::date)::int as reports,
                   (select count(*) from vanta.reports r where (r.updated_at at time zone 'utc')::date = s.day::date and r.status = 'broken')::int as broken,
                   (select count(*) from vanta.request_votes v where (v.created_at at time zone 'utc')::date = s.day::date)::int as votes
              from generate_series((now() at time zone 'utc')::date - 13, (now() at time zone 'utc')::date, interval '1 day') s(day)) dd), '[]'));
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- bot: weekly digest also lists the most requested games
create or replace function public.vanta_bot_digest(p_days int default 7) returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare st jsonb := vanta.stats(p_days); since timestamptz;
begin
  perform vanta.require_service();
  since := now() - make_interval(days => (st ->> 'days')::int);
  return st || jsonb_build_object(
    'top', coalesce((select jsonb_agg(s order by o) from (
              select s, o from jsonb_array_elements(vanta.ranked(null)) with ordinality as e(s, o)
               where (s ->> 'broken')::int > 0 order by o limit 10) x), '[]'),
    'fixed', coalesce((select jsonb_agg(jsonb_build_object('game_id', f.game_id, 'cheat_id', f.cheat_id, 'game_name', f.game_name,
                                                           'cheat_name', f.cheat_name, 'fixed_in_version', f.fixed_in_version)
                                        order by f.status_changed desc) from (
              select * from vanta.cheat_state where status = 'fixed' and status_changed >= since
               order by status_changed desc limit 10) f), '[]'),
    'requests', vanta.request_list('active', 5, null, false));
end $$;

-- ---------------------------------------------------------------------------------------------------------------------
-- privileges
revoke all on all functions in schema vanta from public, anon, authenticated;
grant execute on all functions in schema vanta to service_role;
-- (same list as the functions migration: used by submit/withdraw, which run with the caller's rights)
grant execute on function vanta.can_report(), vanta.fail(text, int, text), vanta.check_id(text, text),
  vanta.check_fingerprint(text), vanta.community_entry(text, text, text) to authenticated;

do $$
declare f text;
begin
  -- public (also without login)
  foreach f in array array['public.vanta_game_requests(text, int)', 'public.vanta_is_admin()'] loop
    execute format('revoke all on function %s from public', f);
    execute format('grant execute on function %s to anon, authenticated, service_role', f);
  end loop;
  -- signed-in users
  foreach f in array array['public.vanta_request_game(integer, text, text)', 'public.vanta_unvote_game(integer)'] loop
    execute format('revoke all on function %s from public, anon', f);
    execute format('grant execute on function %s to authenticated, service_role', f);
  end loop;
  -- admins (each function checks vanta.is_admin itself)
  foreach f in array array[
    'public.vanta_admin_reports(text, text, int)', 'public.vanta_admin_set_report_status(bigint, text, text)',
    'public.vanta_admin_ban(text, boolean)', 'public.vanta_admin_banned()', 'public.vanta_admin_requests(text, int)',
    'public.vanta_admin_set_request(integer, text, text, text)', 'public.vanta_admin_delete_request(integer)',
    'public.vanta_admin_stats(int)'] loop
    execute format('revoke all on function %s from public, anon', f);
    execute format('grant execute on function %s to authenticated, service_role', f);
  end loop;
  -- bot
  execute 'revoke all on function public.vanta_bot_digest(int) from public, anon, authenticated';
  execute 'grant execute on function public.vanta_bot_digest(int) to service_role';
end $$;

