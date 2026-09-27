/**
 * API for the Discord bot (Python, runs elsewhere). Authenticated with a shared secret:
 *   Authorization: Bearer <BOT_API_SECRET>
 * The Worker holds the canonical state (reports, cheat_state incl. the Discord message id, events feed);
 * the bot only renders it in Discord and forwards admin actions (the bot checks the admin Discord ids).
 */
import type { Env } from './env';
import { rateLimit } from './ratelimit';
import { summarize, type CheatState, type Summary } from './reports';
import { HttpError, ipKey, json, now, readJson, safeEqual } from './util';
import * as v from './validate';

async function requireBot(req: Request, env: Env): Promise<void> {
  if (!env.BOT_API_SECRET || env.BOT_API_SECRET.length < 32) throw new HttpError(503, 'bot_api_disabled');
  const h = req.headers.get('Authorization') ?? '';
  const m = /^Bearer (.+)$/.exec(h);
  if (!m || !safeEqual(m[1], env.BOT_API_SECRET)) {
    await rateLimit(env, { name: 'bot-auth-fail', limit: 20, windowSec: 600 }, await ipKey(env, req));
    throw new HttpError(401, 'unauthorized');
  }
}

const STATUSES = ['open', 'fixed', 'cant_reproduce', 'duplicate'] as const;
type Status = (typeof STATUSES)[number];
const int = (s: string | null, def: number, min: number, max: number) => { const n = Number(s ?? def); return Number.isInteger(n) ? Math.min(max, Math.max(min, n)) : def; };

async function stateById(env: Env, id: unknown): Promise<CheatState> {
  if (typeof id !== 'number' || !Number.isInteger(id) || id < 1) throw new HttpError(400, 'invalid_state_id');
  const st = await env.DB.prepare('SELECT * FROM cheat_state WHERE id = ?').bind(id).first<CheatState>();
  if (!st) throw new HttpError(404, 'not_found');
  return st;
}

async function ranked(env: Env, game: string | null, includeClosed = false): Promise<Summary[]> {
  const q = `SELECT * FROM cheat_state WHERE (?1 IS NULL OR game_id = ?1) ${includeClosed ? '' : "AND status = 'open'"} ORDER BY updated DESC LIMIT 500`;
  const states = (await env.DB.prepare(q).bind(game).all<CheatState>()).results;
  const out: Summary[] = [];
  for (const st of states) out.push(await summarize(env, st));
  return out.sort((a, b) => b.score - a.score || b.broken - a.broken);
}

export async function handleBot(req: Request, env: Env, path: string): Promise<Response> {
  await requireBot(req, env);
  const u = new URL(req.url);
  const q = (k: string) => u.searchParams.get(k);
  const game = () => v.id(q('game'), 'game');

  if (req.method === 'GET' && path === '/bot/events') {
    const after = int(q('after'), 0, 0, Number.MAX_SAFE_INTEGER), limit = int(q('limit'), 50, 1, 200);
    const evs = (await env.DB.prepare('SELECT * FROM events WHERE id > ? ORDER BY id LIMIT ?').bind(after, limit).all<{ id: number; type: string; state_id: number; status: string | null; created: number }>()).results;
    // one entry per cheat_state: the bot posts (new broken report, no message yet) or edits (message exists)
    const byState = new Map<number, { last_event: number; types: string[]; has_broken: boolean }>();
    for (const e of evs) {
      const s = byState.get(e.state_id) ?? { last_event: e.id, types: [], has_broken: false };
      s.last_event = e.id; s.types.push(e.type); if (e.type === 'report' && e.status === 'broken') s.has_broken = true;
      byState.set(e.state_id, s);
    }
    const items = [];
    for (const [id, s] of byState) {
      const st = await env.DB.prepare('SELECT * FROM cheat_state WHERE id = ?').bind(id).first<CheatState>();
      if (st) items.push({ ...s, state: await summarize(env, st) });
    }
    return json({ cursor: evs.length ? evs[evs.length - 1].id : after, items, more: evs.length === limit });
  }

  if (req.method === 'GET' && path.startsWith('/bot/state/')) return json({ state: await summarize(env, await stateById(env, Number(path.slice(11))), true) });

  if (req.method === 'POST' && path === '/bot/message') {
    const b = await readJson(req);
    const st = await stateById(env, b.state_id);
    const mid = b.message_id === null ? null : typeof b.message_id === 'string' && /^\d{5,25}$/.test(b.message_id) ? b.message_id : undefined;
    if (mid === undefined) throw new HttpError(400, 'invalid_message_id');
    await env.DB.prepare('UPDATE cheat_state SET message_id = ? WHERE id = ?').bind(mid, st.id).run();
    return json({ ok: true });
  }

  if (req.method === 'POST' && path === '/bot/status') {
    const b = await readJson(req);
    const status = b.status as Status;
    if (!STATUSES.includes(status)) throw new HttpError(400, 'invalid_status');
    const fixedIn = b.fixed_in_version == null || b.fixed_in_version === '' ? null : v.version(b.fixed_in_version, 'fixed_in_version');
    let states: CheatState[];
    if (b.state_id != null) states = [await stateById(env, b.state_id)];
    else {
      const g = v.id(b.game_id, 'game_id'), c = v.id(b.cheat_id, 'cheat_id');
      const fp = b.fingerprint ? v.fingerprint(b.fingerprint) : null;
      states = (await env.DB.prepare(`SELECT * FROM cheat_state WHERE game_id = ? AND cheat_id = ? AND (?3 IS NULL OR fingerprint = ?3) ${fp ? '' : "AND status = 'open'"}`)
        .bind(g, c, fp).all<CheatState>()).results;
      if (!states.length) throw new HttpError(404, 'not_found');
    }
    const t = now();
    const updated: Summary[] = [];
    for (const st of states) {
      const r = await env.DB.prepare('UPDATE cheat_state SET status = ?, fixed_in_version = ?, status_changed = ?, updated = ? WHERE id = ? RETURNING *')
        .bind(status, status === 'fixed' ? fixedIn : null, t, t, st.id).first<CheatState>();
      await env.DB.prepare("INSERT INTO events (type, state_id, created) VALUES ('status', ?, ?)").bind(st.id, t).run();
      updated.push(await summarize(env, r!));
    }
    return json({ ok: true, updated });
  }

  if (req.method === 'POST' && path === '/bot/ban') {
    const b = await readJson(req);
    const id = typeof b.discord_id === 'string' && /^\d{5,25}$/.test(b.discord_id) ? b.discord_id : null;
    if (!id) throw new HttpError(400, 'invalid_discord_id');
    const banned = b.banned === false ? 0 : 1;
    const r = await env.DB.prepare('UPDATE users SET banned = ? WHERE discord_id = ? RETURNING id, username').bind(banned, id).first<{ id: number; username: string }>();
    if (!r) throw new HttpError(404, 'user_not_found');
    if (banned) await env.DB.prepare('DELETE FROM sessions WHERE user_id = ?').bind(r.id).run();
    return json({ ok: true, username: r.username, banned: !!banned });
  }

  if (req.method === 'GET' && path === '/bot/top') {
    const g = q('game') ? game() : null;
    return json({ items: (await ranked(env, g)).filter((s) => s.broken > 0).slice(0, int(q('limit'), 10, 1, 25)) });
  }

  if (req.method === 'GET' && path === '/bot/cheat') {
    const g = game(), c = v.id(q('cheat'), 'cheat');
    const states = (await env.DB.prepare('SELECT * FROM cheat_state WHERE game_id = ? AND cheat_id = ? ORDER BY updated DESC LIMIT 10').bind(g, c).all<CheatState>()).results;
    const items = [];
    for (const st of states) items.push(await summarize(env, st, true));
    return json({ items });
  }

  if (req.method === 'GET' && path === '/bot/game') {
    const g = game();
    const all = await ranked(env, g, true);
    const usage = await env.DB.prepare('SELECT cheat_id, SUM(count) AS n FROM usage_daily WHERE game_id = ? AND day >= ? GROUP BY cheat_id ORDER BY n DESC LIMIT 10')
      .bind(g, new Date((now() - 6 * 86400) * 1000).toISOString().slice(0, 10)).all<{ cheat_id: string; n: number }>();
    return json({ items: all.slice(0, 25), usage7d: usage.results });
  }

  if (req.method === 'GET' && (path === '/bot/stats' || path === '/bot/digest')) {
    const days = path === '/bot/digest' ? int(q('days'), 7, 1, 31) : 7;
    const since = now() - days * 86400;
    const one = async (sql: string, ...a: unknown[]) => (await env.DB.prepare(sql).bind(...a).first<{ n: number }>())?.n ?? 0;
    const stats = {
      days,
      users: await one('SELECT COUNT(*) AS n FROM users WHERE banned = 0'),
      new_users: await one('SELECT COUNT(*) AS n FROM users WHERE created >= ?', since),
      reports: await one('SELECT COUNT(*) AS n FROM reports'),
      reports_period: await one('SELECT COUNT(*) AS n FROM reports WHERE updated >= ?', since),
      broken_period: await one("SELECT COUNT(*) AS n FROM reports WHERE updated >= ? AND status = 'broken'", since),
      open: await one("SELECT COUNT(*) AS n FROM cheat_state WHERE status = 'open' AND EXISTS (SELECT 1 FROM reports r WHERE r.game_id = cheat_state.game_id AND r.cheat_id = cheat_state.cheat_id AND r.fingerprint = cheat_state.fingerprint AND r.status = 'broken')"),
      fixed_period: await one("SELECT COUNT(*) AS n FROM cheat_state WHERE status = 'fixed' AND status_changed >= ?", since),
      usage_period: await one('SELECT COALESCE(SUM(count), 0) AS n FROM usage_daily WHERE day >= ?', new Date(since * 1000).toISOString().slice(0, 10)),
    };
    if (path === '/bot/stats') return json(stats);
    const fixed = (await env.DB.prepare("SELECT * FROM cheat_state WHERE status = 'fixed' AND status_changed >= ? ORDER BY status_changed DESC LIMIT 10").bind(since).all<CheatState>()).results;
    return json({ ...stats, top: (await ranked(env, null)).filter((s) => s.broken > 0).slice(0, 10), fixed: fixed.map((f) => ({ game_id: f.game_id, cheat_id: f.cheat_id, game_name: f.game_name, cheat_name: f.cheat_name, fixed_in_version: f.fixed_in_version })) });
  }

  throw new HttpError(404, 'not_found');
}
