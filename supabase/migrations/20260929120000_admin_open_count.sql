-- Admin dashboard: the open count now matches the Reports list. The list shows one entry per game + cheat that has at
-- least one "doesn't work" report (from a non-banned user) on an open report state; "works"-only states and extra game
-- versions of the same cheat are not separate entries. vanta_admin_reports adds 'open_cheats' (all games, used for the
-- tab badge) and games[].open now uses the same rule. 'counts' (raw report states per status) is unchanged.
-- Idempotent: only replaces the function and re-applies its grants.

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
      select jsonb_agg(jsonb_build_object('game_id', y.game_id, 'game_name', y.game_name, 'open', coalesce(o.n, 0)) order by y.game_id)
        from (select c.game_id, max(c.game_name) as game_name from vanta.cheat_state c group by c.game_id) y
        left join (select b.game_id, count(distinct b.cheat_id)::int as n from vanta.cheat_state b
                    where b.status = 'open'
                      and exists (select 1 from vanta.reports r join vanta.profiles p on p.id = r.user_id
                                   where r.game_id = b.game_id and r.cheat_id = b.cheat_id and r.fingerprint = b.fingerprint
                                     and r.status = 'broken' and not p.banned)
                    group by b.game_id) o on o.game_id = y.game_id), '[]'),
    'open_cheats', (select count(*)::int from (
                      select distinct b.game_id, b.cheat_id from vanta.cheat_state b
                       where b.status = 'open'
                         and exists (select 1 from vanta.reports r join vanta.profiles p on p.id = r.user_id
                                      where r.game_id = b.game_id and r.cheat_id = b.cheat_id and r.fingerprint = b.fingerprint
                                        and r.status = 'broken' and not p.banned)) oc),
    'counts', coalesce((select jsonb_object_agg(z.status, z.n) from (
                          select c.status, count(*)::int as n from vanta.cheat_state c where g is null or c.game_id = g group by c.status) z), '{}'));
end $$;

revoke all on function public.vanta_admin_reports(text, text, int) from public, anon;
grant execute on function public.vanta_admin_reports(text, text, int) to authenticated, service_role;
