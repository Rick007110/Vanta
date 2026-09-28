-- pgTAP tests for game requests and the admin API (migration 20260928120000_game_requests_admin.sql).
begin;
create extension if not exists pgtap with schema extensions;
set search_path = public, extensions;
select plan(48);

-- users: R = admin (Discord identity 348805709837762561), U/V = players, S = spoofs the admin id in user_metadata only
insert into auth.users (id, raw_user_meta_data, raw_app_meta_data) values
  ('00000000-0000-0000-0000-0000000000a1', '{"provider_id":"348805709837762561","full_name":"owner"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-0000000000a2', '{"provider_id":"500000000000000002","full_name":"uma"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-0000000000a3', '{"provider_id":"500000000000000003","full_name":"vic"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-0000000000a4', '{"provider_id":"500000000000000004","full_name":"sam"}', '{"provider":"discord","providers":["discord"]}');
insert into auth.identities (user_id, provider, provider_id) values
  ('00000000-0000-0000-0000-0000000000a1', 'discord', '348805709837762561'),
  ('00000000-0000-0000-0000-0000000000a2', 'discord', '500000000000000002'),
  ('00000000-0000-0000-0000-0000000000a3', 'discord', '500000000000000003'),
  ('00000000-0000-0000-0000-0000000000a4', 'discord', '500000000000000004');

select is((select count(*)::int from vanta.admins where discord_id = '348805709837762561'), 1, 'admin table is seeded');

-- ---------------------------------------------------------------- anonymous
set local role anon;
set local request.jwt.claims = '{"role":"anon"}';
select is(vanta_game_requests() -> 'items', '[]'::jsonb, 'anon can read the (empty) request list');
select is(vanta_is_admin(), false, 'anon is not admin');
select throws_ok($$ select vanta_request_game(1245620, 'ELDEN RING') $$, '42501', null, 'anon cannot request');
select throws_ok($$ select vanta_admin_reports() $$, '42501', null, 'anon cannot call admin functions');
select throws_ok($$ select * from vanta.game_requests $$, '42501', null, 'anon cannot read the requests table');
reset role;

-- ---------------------------------------------------------------- player U
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a2","role":"authenticated"}';
select is((vanta_request_game(1245620, e'  ELDEN\u200B RING ', 'https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1245620/capsule_231x87.jpg?t=1')) ->> 'created', 'true', 'U requests a new game');
select is((select r ->> 'name' from jsonb_array_elements(vanta_game_requests() -> 'items') r), 'ELDEN RING', 'name is cleaned');
select is((vanta_request_game(1245620, 'whatever')) ->> 'already_voted', 'true', 'second vote by the same user is a no-op');
select is((vanta_game_requests() -> 'items' -> 0 ->> 'votes')::int, 1, 'one vote per user per game');
select is((vanta_game_requests() -> 'items' -> 0 ->> 'voted')::boolean, true, 'voted flag for the caller');
select is((vanta_request_game(1091500, 'Cyberpunk 2077', 'https://evil.example/x.png')) -> 'request' ->> 'cover_url', null, 'non-Steam cover URLs are dropped');
select throws_ok($$ select vanta_request_game(0, 'x') $$, 'PT400', 'invalid_appid', 'app id is validated');
select throws_ok($$ select vanta_request_game(42, '   ') $$, 'PT400', 'invalid_name', 'a new request needs a name');
select throws_ok($$ select vanta_request_game(43, repeat('x', 121)) $$, 'PT400', 'name_too_long', 'names are limited');
select is(vanta_is_admin(), false, 'a player is not admin');
select throws_ok($$ select vanta_admin_requests() $$, 'PT403', 'forbidden', 'players cannot list admin data');
select throws_ok($$ select vanta_admin_set_request(1245620, 'added') $$, 'PT403', 'forbidden', 'players cannot change requests');
select throws_ok($$ select vanta_admin_ban('500000000000000003') $$, 'PT403', 'forbidden', 'players cannot ban');
select throws_ok($$ select vanta_admin_stats() $$, 'PT403', 'forbidden', 'players cannot read stats');
select throws_ok($$ select * from vanta.request_votes $$, '42501', null, 'players cannot read votes directly');
select throws_ok($$ select * from vanta.admins $$, '42501', null, 'players cannot read the admin list');
reset role;

-- ---------------------------------------------------------------- player V votes, sorting
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a3","role":"authenticated"}';
select lives_ok($$ select vanta_request_game(1091500) $$, 'V votes for an existing request without a name');
select is((select string_agg(r ->> 'appid', ',') from jsonb_array_elements(vanta_game_requests() -> 'items') r), '1091500,1245620', 'sorted by votes');
select is((vanta_unvote_game(1091500)) ->> 'removed', 'true', 'V removes the vote');
select is((vanta_game_requests() -> 'items' -> 0 ->> 'voted')::boolean, false, 'V has no vote left on the top request');
reset role;

-- ---------------------------------------------------------------- metadata spoofing does not make an admin
update auth.users set raw_user_meta_data = raw_user_meta_data || '{"provider_id":"348805709837762561"}' where id = '00000000-0000-0000-0000-0000000000a4';
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a4","role":"authenticated"}';
select is(vanta_is_admin(), false, 'user_metadata provider_id does not grant admin');
select throws_ok($$ select vanta_admin_reports() $$, 'PT403', 'forbidden', 'spoofed metadata cannot read reports');
reset role;
select is((select discord_id from vanta.profiles where id = '00000000-0000-0000-0000-0000000000a4'), '500000000000000004', 'profile keeps the Discord id from auth.identities');

-- ---------------------------------------------------------------- admin
insert into vanta.reports (user_id, game_id, cheat_id, fingerprint, status, vanta_version, game_name, cheat_name)
values ('00000000-0000-0000-0000-0000000000a3', 'far-cry-5', 'godmode', 'fpx', 'broken', '0.3.1', 'Far Cry 5', 'God mode');
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a1","role":"authenticated"}';
select is(vanta_is_admin(), true, 'the Discord identity in vanta.admins is admin');
select is(jsonb_array_length(vanta_admin_reports() -> 'items'), 1, 'admin lists open reports');
select is(vanta_admin_reports() -> 'items' -> 0 -> 'reporters' -> 0 ->> 'discord_id', '500000000000000003', 'reports include reporters');
select is((vanta_admin_set_report_status((vanta_admin_reports() -> 'items' -> 0 ->> 'id')::bigint, 'fixed', '0.3.2')) -> 'state' ->> 'fixed_in_version', '0.3.2', 'admin marks fixed in a version');
select is((vanta_admin_reports(p_status => 'fixed') -> 'items' -> 0 ->> 'status'), 'fixed', 'status change is queued for the bot');
select is(jsonb_array_length(vanta_admin_reports(p_status => 'open') -> 'items'), 0, 'fixed state leaves the open list');
select throws_ok($$ select vanta_admin_set_report_status(1, 'wontfix') $$, 'PT400', 'invalid_status', 'report statuses are validated');
select is((vanta_admin_set_request(1245620, 'planned', 'Next up')) -> 'request' ->> 'status', 'planned', 'admin sets request status');
select is((vanta_game_requests() -> 'items' -> 0 ->> 'note'), 'Next up', 'the note is public');
select throws_ok($$ select vanta_admin_ban('348805709837762561') $$, 'PT409', 'cannot_ban_admin', 'admins cannot be banned');
select is((vanta_admin_ban('500000000000000002')) ->> 'banned', 'true', 'admin bans U');
select is((vanta_admin_stats() ->> 'banned')::int, 1, 'stats count banned users');
select lives_ok($$ select vanta_admin_set_request(1245620, 'added') $$, 'admin marks a request added');
reset role;
select is((select type from vanta.events order by id desc limit 1), 'status', 'report status change is queued for the bot');
-- open count = game + cheat entries with a "doesn't work" report, not raw states (works-only / extra game versions)
insert into vanta.reports (user_id, game_id, cheat_id, fingerprint, status, vanta_version, game_name, cheat_name) values
  ('00000000-0000-0000-0000-0000000000a3', 'far-cry-5', 'godmode', 'fpy', 'broken', '0.3.1', 'Far Cry 5', 'God mode'),
  ('00000000-0000-0000-0000-0000000000a3', 'far-cry-5', 'godmode', 'fpz', 'broken', '0.3.1', 'Far Cry 5', 'God mode'),
  ('00000000-0000-0000-0000-0000000000a3', 'far-cry-5', 'ammo', 'fpx', 'works', '0.3.1', 'Far Cry 5', 'Infinite ammo');
set local role authenticated;
select is((vanta_admin_reports() -> 'counts' ->> 'open')::int, 3, 'counts still has raw open states');
select is((vanta_admin_reports() ->> 'open_cheats')::int, 1, 'open_cheats counts one entry per cheat with a broken report');
select is((vanta_admin_reports(p_game => 'far-cry-5') -> 'games' -> 0 ->> 'open')::int, 1, 'games[].open uses the same rule');
reset role;

-- ---------------------------------------------------------------- banned / closed
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a2","role":"authenticated"}';
select throws_ok($$ select vanta_request_game(1091500) $$, 'PT403', 'banned', 'banned users cannot vote');
reset role;
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-0000000000a3","role":"authenticated"}';
select throws_ok($$ select vanta_request_game(1245620) $$, 'PT409', 'request_closed', 'added requests take no votes');
reset role;

select * from finish();
rollback;
