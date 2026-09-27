-- pgTAP tests for the Vanta community schema. Run with `supabase test db` or, locally without Docker:
--   sh supabase/tests/local-stack.sh db && pg_prove -h 127.0.0.1 -p 54329 -U postgres -d vanta supabase/tests/database/*.test.sql
begin;
create extension if not exists pgtap with schema extensions;
set search_path = public, extensions;
select plan(69);

-- ---------------------------------------------------------------- users (as Supabase Auth would create them)
insert into auth.users (id, email, raw_user_meta_data, raw_app_meta_data) values
  ('00000000-0000-0000-0000-00000000000a', 'a@example.invalid', '{"provider_id":"400000000000000001","sub":"400000000000000001","full_name":"alice","custom_claims":{"global_name":"Alice"},"avatar_url":"https://cdn.discordapp.com/avatars/400000000000000001/abc.png","email":"a@example.invalid"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-00000000000b', 'b@example.invalid', '{"provider_id":"400000000000000002","full_name":"bob","avatar_url":"https://evil.example/x.png"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-00000000000c', null, '{"provider_id":"400000000000000003","full_name":"carol"}', '{"provider":"discord","providers":["discord"]}'),
  ('00000000-0000-0000-0000-00000000000d', null, '{"provider_id":"400000000000000004","full_name":"dave"}', '{"provider":"discord","providers":["discord"]}');
insert into auth.users (id, email, raw_app_meta_data) values ('00000000-0000-0000-0000-0000000000ee', 'other@example.invalid', '{"provider":"email","providers":["email"]}');
insert into auth.sessions (user_id) values ('00000000-0000-0000-0000-00000000000d');

select is((select discord_id || '|' || username || '|' || coalesce(avatar_url, '-') from vanta.profiles where id = '00000000-0000-0000-0000-00000000000a'),
          '400000000000000001|Alice|https://cdn.discordapp.com/avatars/400000000000000001/abc.png', 'profile from Discord metadata (global name, avatar)');
select is((select username || '|' || coalesce(avatar_url, '-') from vanta.profiles where id = '00000000-0000-0000-0000-00000000000b'),
          'bob|-', 'non-Discord avatar URLs are dropped');
select hasnt_column('vanta', 'profiles', 'email', 'profiles have no e-mail column');
select is((select count(*)::int from vanta.profiles where id = '00000000-0000-0000-0000-0000000000ee'), 0, 'non-Discord users of the project get no Vanta profile');
update auth.users set raw_user_meta_data = raw_user_meta_data || '{"full_name":"bobby"}' where id = '00000000-0000-0000-0000-00000000000b';
select is((select username from vanta.profiles where id = '00000000-0000-0000-0000-00000000000b'), 'bobby', 'profile follows metadata updates');

-- ---------------------------------------------------------------- helpers
select is(vanta.cmp_version('0.2.10', '0.2.9'), 1, 'cmp_version 0.2.10 > 0.2.9');
select is(vanta.cmp_version('0.3', '0.3.0'), 0, 'cmp_version 0.3 = 0.3.0');
select is(vanta.cmp_version('0.2.2', '0.3.0-beta'), -1, 'cmp_version 0.2.2 < 0.3.0-beta');
select is(vanta.report_counts('fixed', now(), '0.3.0', now() + interval '1 s', '0.2.9'), false, 'fixed: older Vanta version does not count');
select is(vanta.report_counts('fixed', now(), '0.3.0', now() + interval '1 s', '0.3.0'), true, 'fixed: fixed version counts');
select is(vanta.report_counts('cant_reproduce', now(), null, now() - interval '1 s', '0.3.0'), false, 'closed: vanta.reports before the change do not count');
select is(vanta.clean_text(e' a\u200Bb \n\t c\u0007 ', 300, 'note'), 'ab c', 'clean_text strips control/zero-width chars and collapses whitespace');
select is(vanta.clean_text('   ', 300, 'note'), null, 'blank note becomes null');

-- ---------------------------------------------------------------- user A vanta.reports
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000a","role":"authenticated"}';
select is((vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.0', e'crashes\u202E on load', '1.0', 'Far Cry 5', 'God mode')) -> 'community',
          '{"works":0,"broken":1,"status":"open","fixed_in_version":null}'::jsonb, 'submit_report returns the community counts');
select is((vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.0', 'still crashes')) ->> 'replaced', 'true', 'second vote replaces the first');
select is((select count(*)::int from vanta.reports), 1, 'A sees only A''s single report');
select is((select note from vanta.reports), 'still crashes', 'note replaced');
select throws_ok($$ select vanta_submit_report('far-cry-5', 'ammo', 'fp1', 'broken', '0.3.0', repeat('x', 301)) $$, 'PT400', 'note_too_long', 'notes are limited to 300 characters');
select throws_ok($$ select vanta_submit_report('Far Cry', 'ammo', 'fp1', 'broken', '0.3.0') $$, 'PT400', 'invalid_game_id', 'game ids are validated');
select throws_ok($$ select vanta_submit_report('far-cry-5', 'ammo', 'fp1', 'maybe', '0.3.0') $$, 'PT400', 'invalid_status', 'status is validated');
select throws_ok($$ insert into vanta.reports (user_id, game_id, cheat_id, fingerprint, status, vanta_version)
                    values ('00000000-0000-0000-0000-00000000000b', 'far-cry-5', 'ammo', 'fp1', 'works', '0.3.0') $$,
                 '42501', null, 'A cannot insert a report in B''s name');
select lives_ok($$ insert into vanta.reports (game_id, cheat_id, fingerprint, status, vanta_version) values ('far-cry-5', 'ammo', 'fp1', 'works', '0.3.0') $$,
                'direct insert of an own report works (user_id defaults to auth.uid())');
reset role;

-- B vanta.reports on the same cheat
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000b","role":"authenticated"}';
select lives_ok($$ select vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'works', '0.3.0') $$, 'B reports');
select is((select count(*)::int from vanta.reports where user_id = '00000000-0000-0000-0000-00000000000a'), 0, 'B cannot read A''s reports');
update vanta.reports set status = 'works' where user_id = '00000000-0000-0000-0000-00000000000a';
delete from vanta.reports where user_id = '00000000-0000-0000-0000-00000000000a';
select is((select count(*)::int from vanta.profiles), 1, 'B sees only its own profile');
select throws_ok($$ update vanta.profiles set banned = false $$, '42501', null, 'B cannot update profiles');
select throws_ok($$ select * from vanta.cheat_state $$, '42501', null, 'authenticated cannot read vanta.cheat_state directly');
select throws_ok($$ select vanta_bot_top() $$, '42501', null, 'authenticated cannot call bot functions');
select throws_ok($$ update vanta.reports set user_id = '00000000-0000-0000-0000-00000000000a' $$, 'PT400', 'immutable_fields', 'owner and key fields cannot be changed');
reset role;
select is((select status from vanta.reports where user_id = '00000000-0000-0000-0000-00000000000a' and cheat_id = 'godmode'), 'broken', 'B could not change A''s report');
select is((select count(*)::int from vanta.reports where user_id = '00000000-0000-0000-0000-00000000000a'), 2, 'B could not delete A''s reports');

-- ---------------------------------------------------------------- anon
set local role anon;
set local request.jwt.claims = '{"role":"anon"}';
select throws_ok($$ select * from vanta.profiles $$, '42501', null, 'anon cannot read profiles');
select throws_ok($$ select * from vanta.reports $$, '42501', null, 'anon cannot read reports');
select throws_ok($$ select vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.0') $$, '42501', null, 'anon cannot report');
select throws_ok($$ select vanta_delete_my_account() $$, '42501', null, 'anon cannot delete accounts');
select throws_ok($$ select vanta_bot_state(1) $$, '42501', null, 'anon cannot call bot functions');
select is(vanta_community('far-cry-5', 'fp1') -> 'cheats' -> 'godmode', '{"works":1,"broken":1,"status":"open","fixed_in_version":null}'::jsonb,
          'anon reads aggregated counts');
select ok(vanta_community('far-cry-5', null)::text !~ '4000000000000000|alice|bob|Alice', 'community data contains no identities');
select is(vanta_community('far-cry-5', null) ->> 'fingerprint', 'fp1', 'without fingerprint: newest known game version');
select lives_ok($$ select vanta_count_usage('far-cry-5', '{"godmode": 3, "ammo": 1}') $$, 'anon may send usage counts');
select throws_ok($$ select vanta_count_usage('far-cry-5', '{"godmode": 5000}') $$, 'PT400', 'invalid_counts', 'usage counts are bounded');
select throws_ok($$ select vanta_count_usage('far-cry-5', '{"godmode": "x"}') $$, 'PT400', 'invalid_counts', 'usage counts must be numbers');
reset role;
select is((select string_agg(column_name, ',' order by ordinal_position) from information_schema.columns where table_schema = 'vanta' and table_name = 'usage_daily'),
          'day,game_id,cheat_id,count', 'usage_daily stores no user or client data');

-- ---------------------------------------------------------------- scoring and triage (service role)
set local role service_role;
set local request.jwt.claims = '{"role":"service_role"}';
select is((vanta_bot_top() -> 'items' -> 0 ->> 'score')::numeric,
          round((1 - 0.5) * 1.5 * 1.2 * (1 + 0.1 * log(1 + 3))::numeric, 2),
          'score = (broken - 0.5*works) * newest 1.5 * spike 1.2 * demand');
select is(vanta_bot_top() -> 'items' -> 0 -> 'notes' -> 0 ->> 'note', 'still crashes', 'notes of broken vanta.reports are included');
select ok(vanta_bot_state((select id from vanta.cheat_state where cheat_id = 'godmode')) -> 'reporters' @> '[{"discord_id":"400000000000000001","status":"broken"}]',
          'the bot sees who reported (for moderation)');
select is(vanta_bot_set_status('fixed', p_game_id => 'far-cry-5', p_cheat_id => 'godmode', p_fixed_in_version => '0.3.1') -> 'updated' -> 0 ->> 'status', 'fixed', '/fixed marks the open state fixed');
select is((vanta_bot_state((select id from vanta.cheat_state where cheat_id = 'godmode')) ->> 'broken')::int, 0, 'after fixing, old vanta.reports do not count');
select is((vanta_bot_state((select id from vanta.cheat_state where cheat_id = 'godmode')) ->> 'score')::numeric, 0::numeric, 'closed states score 0');
reset role;

set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000a","role":"authenticated"}';
select is(vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.0') ->> 'reopened', 'false', 'broken on an older Vanta version does not reopen');
select is(vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.1') ->> 'reopened', 'true', 'broken on the fixed version reopens');
reset role;
select is((select status from vanta.cheat_state where cheat_id = 'godmode'), 'open', 'state is open again');

-- ---------------------------------------------------------------- bans
set local role service_role;
set local request.jwt.claims = '{"role":"service_role"}';
select is(vanta_bot_ban('400000000000000004') ->> 'username', 'dave', 'bot_ban by Discord id');
select throws_ok($$ select vanta_bot_ban('499999999999999999') $$, 'PT404', 'user_not_found', 'unknown Discord id');
reset role;
select is((select count(*)::int from auth.sessions where user_id = '00000000-0000-0000-0000-00000000000d'), 0, 'banning ends the user''s sessions');
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000d","role":"authenticated"}';
select throws_ok($$ select vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'broken', '0.3.1') $$, 'PT403', 'banned', 'banned users cannot report');
select throws_ok($$ insert into vanta.reports (game_id, cheat_id, fingerprint, status, vanta_version) values ('far-cry-5', 'x', 'fp1', 'works', '0.3.0') $$,
                 'PT403', 'banned', 'banned users cannot insert directly');
reset role;

-- ---------------------------------------------------------------- rate limit (30 per user per hour; an upsert counts once)
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000c","role":"authenticated"}';
select lives_ok($$ select vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'works', '0.3.1') from generate_series(1, 30) $$, '30 vanta.reports in an hour are fine');
select throws_ok($$ select vanta_submit_report('far-cry-5', 'godmode', 'fp1', 'works', '0.3.1') $$, 'PT429', 'rate_limited', 'the 31st is rate limited');
reset role;

-- ---------------------------------------------------------------- bot feed
set local role service_role;
set local request.jwt.claims = '{"role":"service_role"}';
select ok((select jsonb_array_length(vanta_bot_events(0, 200) -> 'items') = 2), 'events: one item per touched cheat_state');
select ok(vanta_bot_events(0, 200) -> 'items' @> '[{"has_broken": true, "state": {"cheat_id": "godmode"}}]', 'events flag new broken reports');
select is((vanta_bot_events(0, 1) ->> 'more')::boolean, true, 'events paginate');
select is((select (vanta_bot_events((vanta_bot_events(0, 200) ->> 'cursor')::bigint, 50) -> 'items')), '[]'::jsonb, 'nothing after the cursor');
select lives_ok($$ select vanta_bot_set_message((select id from vanta.cheat_state where cheat_id = 'godmode'), '123456789012345678') $$, 'message id stored');
select is((vanta_bot_stats() ->> 'users')::int, (select count(*)::int from vanta.profiles where not banned), 'stats count non-banned users');
reset role;

-- ---------------------------------------------------------------- account deletion
set local role authenticated;
set local request.jwt.claims = '{"sub":"00000000-0000-0000-0000-00000000000a","role":"authenticated"}';
select is(vanta_delete_my_account() ->> 'reports', '2', 'delete_my_account removes the reports');
reset role;
select is((select count(*)::int from auth.users where id = '00000000-0000-0000-0000-00000000000a'), 0, 'auth user deleted');
select is((select count(*)::int from vanta.profiles where id = '00000000-0000-0000-0000-00000000000a') + (select count(*)::int from vanta.reports where user_id = '00000000-0000-0000-0000-00000000000a'),
          0, 'profile and vanta.reports deleted');
select ok(exists (select 1 from vanta.events where type = 'withdrawn'), 'deletion emits withdrawn vanta.events for the bot');

select * from finish();
rollback;
