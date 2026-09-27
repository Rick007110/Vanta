import { createExecutionContext, createScheduledController, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { describe, expect, it } from 'vitest';
import worker from '../src/index';
import { bot, call, login, logins, newUser, report } from './helpers';

describe('bot API', () => {
  it('requires the shared secret (constant-time compare)', async () => {
    expect((await call('GET', '/bot/stats')).status).toBe(401);
    expect((await call('GET', '/bot/stats', { headers: { Authorization: 'Bearer wrong' } })).status).toBe(401);
    expect((await call('GET', '/bot/stats', { token: 'vt_' + 'a'.repeat(43) })).status).toBe(401);
    expect((await bot('GET', '/bot/stats')).status).toBe(200);
  });

  it('events feed: one item per cheat, cursor, message id round trip', async () => {
    const cur = (await (await bot('GET', '/bot/events?after=0&limit=200')).json<{ cursor: number }>()).cursor;
    let after = cur;
    for (;;) { const j = await (await bot('GET', `/bot/events?after=${after}&limit=200`)).json<{ cursor: number; more: boolean }>(); after = j.cursor; if (!j.more) break; }
    const [a, b] = await logins(2);
    await report(a.token, { game_id: 'bot-g', cheat_id: 'fly', note: 'falls through floor', game_name: 'Bot Game', cheat_name: 'Fly' });
    await report(b.token, { game_id: 'bot-g', cheat_id: 'fly' });
    await report(b.token, { game_id: 'bot-g', cheat_id: 'ammo', status: 'works' });
    const ev = await (await bot('GET', `/bot/events?after=${after}`)).json<{ cursor: number; items: { has_broken: boolean; types: string[]; state: { id: number; cheat_id: string; broken: number; score: number; notes: { note: string }[]; message_id: string | null } }[] }>();
    expect(ev.cursor).toBeGreaterThan(after);
    const fly = ev.items.find((i) => i.state.cheat_id === 'fly')!;
    expect(fly).toMatchObject({ has_broken: true, types: ['report', 'report'] });
    expect(fly.state.broken).toBe(2);
    expect(fly.state.score).toBeGreaterThan(0);
    expect(fly.state.notes[0].note).toBe('falls through floor');
    expect(ev.items.find((i) => i.state.cheat_id === 'ammo')!.has_broken).toBe(false);
    expect((await bot('POST', '/bot/message', { state_id: fly.state.id, message_id: '1234567890123456789' })).status).toBe(200);
    expect((await bot('POST', '/bot/message', { state_id: fly.state.id, message_id: 'abc' })).status).toBe(400);
    const s = await (await bot('GET', `/bot/state/${fly.state.id}`)).json<{ state: { message_id: string; reporters: { discord_id: string }[] } }>();
    expect(s.state.message_id).toBe('1234567890123456789');
    expect(s.state.reporters).toHaveLength(2);
    // nothing new
    const empty = await (await bot('GET', `/bot/events?after=${ev.cursor}`)).json<{ items: unknown[]; cursor: number }>();
    expect(empty).toMatchObject({ items: [], cursor: ev.cursor });
  });

  it('top, cheat, game, status by game/cheat, digest', async () => {
    const us = await logins(3);
    for (const u of us) await report(u.token, { game_id: 'top-g', cheat_id: 'hot' });
    await report(us[0].token, { game_id: 'top-g', cheat_id: 'mild' });
    const top = await (await bot('GET', '/bot/top?game=top-g')).json<{ items: { cheat_id: string; broken: number }[] }>();
    expect(top.items.map((i) => i.cheat_id)).toEqual(['hot', 'mild']);
    const cheat = await (await bot('GET', '/bot/cheat?game=top-g&cheat=hot')).json<{ items: { reporters: unknown[] }[] }>();
    expect(cheat.items[0].reporters).toHaveLength(3);
    expect((await bot('GET', '/bot/game?game=top-g')).status).toBe(200);
    expect((await bot('GET', '/bot/top?game=BAD GAME')).status).toBe(400);
    const st = await (await bot('POST', '/bot/status', { game_id: 'top-g', cheat_id: 'hot', status: 'fixed', fixed_in_version: '0.3.1' })).json<{ updated: { status: string; score: number }[] }>();
    expect(st.updated[0]).toMatchObject({ status: 'fixed', score: 0 });
    expect((await bot('POST', '/bot/status', { game_id: 'top-g', cheat_id: 'hot', status: 'bogus' })).status).toBe(400);
    const top2 = await (await bot('GET', '/bot/top?game=top-g')).json<{ items: { cheat_id: string }[] }>();
    expect(top2.items.map((i) => i.cheat_id)).toEqual(['mild']);
    const d = await (await bot('GET', '/bot/digest?days=7')).json<{ fixed: { cheat_id: string }[]; top: unknown[]; users: number }>();
    expect(d.fixed.some((f) => f.cheat_id === 'hot')).toBe(true);
    expect(d.users).toBeGreaterThan(0);
  });

  it('ban revokes sessions; unban restores reporting after a new login', async () => {
    const u = newUser('ban-me');
    const { token } = await login(u);
    expect((await bot('POST', '/bot/ban', { discord_id: '1' })).status).toBe(400);
    expect((await bot('POST', '/bot/ban', { discord_id: '99999999999' })).status).toBe(404);
    expect((await bot('POST', '/bot/ban', { discord_id: u.id, banned: true })).status).toBe(200);
    expect((await call('GET', '/me', { token })).status).toBe(401);
    const again = await login(u);
    expect((await report(again.token, { game_id: 'ban-g', cheat_id: 'x' })).status).toBe(403);
    await bot('POST', '/bot/ban', { discord_id: u.id, banned: false });
    expect((await report(again.token, { game_id: 'ban-g', cheat_id: 'x' })).status).toBe(200);
  });

  it('scheduled cleanup removes expired rows', async () => {
    await env.DB.prepare("INSERT INTO rate_limits (key, window, count, expires) VALUES ('old', 1, 1, 1)").run();
    await env.DB.prepare("INSERT INTO auth_requests (id, client_state, challenge, port, created) VALUES ('oldreq', 's', 'c', 50000, 1)").run();
    const ctx = createExecutionContext();
    await worker.scheduled(createScheduledController(), env, ctx);
    await waitOnExecutionContext(ctx);
    const n = await env.DB.prepare("SELECT (SELECT COUNT(*) FROM rate_limits WHERE key = 'old') + (SELECT COUNT(*) FROM auth_requests WHERE id = 'oldreq') AS n").first<{ n: number }>();
    expect(n?.n).toBe(0);
  });
});
