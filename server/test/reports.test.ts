import { env } from 'cloudflare:workers';
import { describe, expect, it } from 'vitest';
import { bot, call, freshIp, login, logins, newUser, report } from './helpers';

type Community = { cheats: Record<string, { works: number; broken: number; status: string; fixed_in_version: string | null }> };

describe('reports', () => {
  it('requires login and validates input', async () => {
    expect((await call('POST', '/reports', { body: { game_id: 'g', cheat_id: 'c' } })).status).toBe(401);
    const { token } = await login(newUser());
    for (const bad of [{ game_id: 'Bad Id!' }, { game_id: 'ok', cheat_id: '../x' }, { game_id: 'ok', cheat_id: 'c', status: 'maybe' }, { game_id: 'ok', cheat_id: 'c', vanta_version: 'x'.repeat(40) }, { game_id: 'ok', cheat_id: 'c', note: 5 }])
      expect((await report(token, { cheat_id: 'c', ...bad })).status).toBe(400);
    expect((await call('POST', '/reports', { token, raw: 'x', headers: { 'Content-Type': 'text/plain' } })).status).toBe(415);
    expect((await call('POST', '/reports', { token, raw: '{"a":"' + 'x'.repeat(10000) + '"}', headers: { 'Content-Type': 'application/json' } })).status).toBe(413);
  });

  it('one vote per user; a new vote replaces the old; notes are sanitized', async () => {
    const { token } = await login(newUser());
    const r1 = await report(token, { game_id: 'rep-a', cheat_id: 'ammo', note: 'hi\u202E\u0000 there   <@everyone>', game_name: 'Game A', cheat_name: 'Infinite ammo' });
    expect(r1.status).toBe(200);
    const j1 = await r1.json<{ replaced: boolean; community: { broken: number } }>();
    expect(j1).toMatchObject({ replaced: false, community: { broken: 1 } });
    const r2 = await report(token, { game_id: 'rep-a', cheat_id: 'ammo', status: 'works' });
    expect(await r2.json()).toMatchObject({ replaced: true, community: { broken: 0, works: 1 } });
    const row = await env.DB.prepare("SELECT COUNT(*) AS n FROM reports WHERE game_id = 'rep-a'").first<{ n: number }>();
    expect(row?.n).toBe(1);
    await report(token, { game_id: 'rep-a', cheat_id: 'ammo', note: 'hi\u202E\u0000 there   <@everyone>' });
    const note = await env.DB.prepare("SELECT note FROM reports WHERE game_id = 'rep-a'").first<{ note: string }>();
    expect(note?.note).toBe('hi there <@everyone>'); // mentions are neutralised by the bot at render time
    const st = await env.DB.prepare("SELECT game_name, cheat_name FROM cheat_state WHERE game_id = 'rep-a'").first();
    expect(st).toEqual({ game_name: 'Game A', cheat_name: 'Infinite ammo' });
  });

  it('withdraw removes the vote', async () => {
    const { token } = await login(newUser());
    await report(token, { game_id: 'rep-w', cheat_id: 'fly' });
    const d = await call('DELETE', '/reports', { token, body: { game_id: 'rep-w', cheat_id: 'fly', fingerprint: 'fp-1' } });
    expect(await d.json()).toEqual({ ok: true, removed: true });
    const mine = await (await call('GET', '/reports/mine', { token })).json<{ reports: unknown[] }>();
    expect(mine.reports).toHaveLength(0);
  });

  it('community counts unique users per fingerprint, excludes banned users, is public', async () => {
    const users = await logins(3);
    const banned = newUser('spammer');
    const b = await login(banned);
    for (const u of users) await report(u.token, { game_id: 'rep-c', cheat_id: 'speed', fingerprint: 'v2' });
    await report(users[0].token, { game_id: 'rep-c', cheat_id: 'speed', fingerprint: 'v1', status: 'works' });
    await report(b.token, { game_id: 'rep-c', cheat_id: 'speed', fingerprint: 'v2', status: 'works' });
    expect((await bot('POST', '/bot/ban', { discord_id: banned.id, banned: true })).status).toBe(200);
    const c = await (await call('GET', '/community/rep-c?fingerprint=v2', { ip: freshIp() })).json<Community>();
    expect(c.cheats.speed).toMatchObject({ broken: 3, works: 0, status: 'open' });
    const c1 = await (await call('GET', '/community/rep-c?fingerprint=v1', { ip: freshIp() })).json<Community>();
    expect(c1.cheats.speed).toMatchObject({ broken: 0, works: 1 });
    // banned user can no longer log in with the old session / report
    expect((await report(b.token, { game_id: 'rep-c', cheat_id: 'x' })).status).toBe(401);
  });

  it('fixed status hides older reports and reopens on a new broken report from a fixed version', async () => {
    const [a, b] = await logins(2);
    await report(a.token, { game_id: 'rep-f', cheat_id: 'gold', vanta_version: '0.2.2' });
    const st = await env.DB.prepare("SELECT id FROM cheat_state WHERE game_id = 'rep-f'").first<{ id: number }>();
    await bot('POST', '/bot/status', { state_id: st!.id, status: 'fixed', fixed_in_version: '0.2.3' });
    await env.DB.prepare('UPDATE cheat_state SET status_changed = status_changed - 10 WHERE id = ?').bind(st!.id).run();
    let c = await (await call('GET', '/community/rep-f?fingerprint=fp-1', { ip: freshIp() })).json<Community>();
    expect(c.cheats.gold).toMatchObject({ broken: 0, status: 'fixed', fixed_in_version: '0.2.3' });
    // an old Vanta version still broken: doesn't count, doesn't reopen
    const old = await (await report(b.token, { game_id: 'rep-f', cheat_id: 'gold', vanta_version: '0.2.2' })).json<{ reopened: boolean }>();
    expect(old.reopened).toBe(false);
    c = await (await call('GET', '/community/rep-f?fingerprint=fp-1', { ip: freshIp() })).json<Community>();
    expect(c.cheats.gold.broken).toBe(0);
    const re = await (await report(b.token, { game_id: 'rep-f', cheat_id: 'gold', vanta_version: '0.2.10' })).json<{ reopened: boolean; community: { status: string } }>();
    expect(re).toMatchObject({ reopened: true, community: { status: 'open' } });
  });

  it('rate limits reports per user', async () => {
    const { token } = await login(newUser());
    let last = 0;
    for (let i = 0; i < 31; i++) last = (await report(token, { game_id: 'rep-rl', cheat_id: 'c' + i })).status;
    expect(last).toBe(429);
  });

  it('anonymous usage is aggregated per day and validated', async () => {
    const ip = freshIp();
    expect((await call('POST', '/usage', { ip, body: { game_id: 'use-g', counts: { fly: 2, ammo: 1 } } })).status).toBe(200);
    expect((await call('POST', '/usage', { ip, body: { game_id: 'use-g', counts: { fly: 3 } } })).status).toBe(200);
    const r = await env.DB.prepare("SELECT cheat_id, count FROM usage_daily WHERE game_id = 'use-g' ORDER BY cheat_id").all();
    expect(r.results).toEqual([{ cheat_id: 'ammo', count: 1 }, { cheat_id: 'fly', count: 5 }]);
    for (const bad of [{ fly: 0 }, { fly: 1001 }, { fly: 1.5 }, { 'B A D': 1 }, {}, Object.fromEntries(Array.from({ length: 101 }, (_, i) => ['c' + i, 1]))])
      expect((await call('POST', '/usage', { ip: freshIp(), body: { game_id: 'use-g', counts: bad } })).status).toBe(400);
    const cols = await env.DB.prepare("SELECT name FROM pragma_table_info('usage_daily')").all<{ name: string }>();
    expect(cols.results.map((c) => c.name).sort()).toEqual(['cheat_id', 'count', 'day', 'game_id']);
  });

  it('404 and security headers', async () => {
    const r = await call('GET', '/nope');
    expect(r.status).toBe(404);
    expect(r.headers.get('X-Content-Type-Options')).toBe('nosniff');
  });
});
