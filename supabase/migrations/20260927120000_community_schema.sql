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
