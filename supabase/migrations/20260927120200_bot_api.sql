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
