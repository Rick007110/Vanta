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
