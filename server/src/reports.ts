import type { Env } from './env';
import { requireUser } from './auth';
import { LIMITS, rateLimit } from './ratelimit';
import { computeScore, type ScoreResult } from './score';
import { HttpError, ipKey, json, now, readJson } from './util';
import * as v from './validate';

export interface CheatState {
  id: number; game_id: string; cheat_id: string; fingerprint: string; status: 'open' | 'fixed' | 'cant_reproduce' | 'duplicate';
  fixed_in_version: string | null; status_changed: number | null; game_name: string | null; cheat_name: string | null;
  game_version: string | null; message_id: string | null; updated: number;
}
interface ReportRow { status: 'works' | 'broken'; note: string | null; vanta_version: string; created: number; updated: number; discord_id?: string; username?: string }

/** Semver-ish compare of Vanta versions ("0.2.10" > "0.2.9"); non-numeric parts compare as 0. */
export function cmpVersion(a: string, b: string): number {
  const pa = a.split(/[.+-]/).map((x) => parseInt(x, 10) || 0), pb = b.split(/[.+-]/).map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length, 3); i++) { const d = (pa[i] ?? 0) - (pb[i] ?? 0); if (d) return Math.sign(d); }
  return 0;
}

/** Which reports count for a state: all while open; after a status change only newer ones (and, when fixed in a version, only from that Vanta version on). */
export function counts(state: Pick<CheatState, 'status' | 'status_changed' | 'fixed_in_version'>, r: Pick<ReportRow, 'updated' | 'vanta_version'>): boolean {
  if (state.status === 'open' || state.status_changed == null) return true;
  if (r.updated <= state.status_changed) return false;
  if (state.status === 'fixed' && state.fixed_in_version) return cmpVersion(r.vanta_version, state.fixed_in_version) >= 0;
  return true;
}

export interface Summary {
  id: number; game_id: string; cheat_id: string; fingerprint: string; game_version: string | null; game_name: string | null; cheat_name: string | null;
  status: CheatState['status']; fixed_in_version: string | null; message_id: string | null;
  broken: number; works: number; total_broken: number; total_works: number; score: number; score_detail: ScoreResult;
  notes: { note: string; vanta_version: string; updated: number }[]; vanta_versions: string[]; last_report: number | null;
  newest_fingerprint: boolean;
}

export async function summarize(env: Env, st: CheatState, withReporters = false): Promise<Summary & { reporters?: { discord_id: string; username: string; status: string; updated: number; note: string | null }[] }> {
  const t = now();
  const rows = (await env.DB.prepare(
    `SELECT r.status, r.note, r.vanta_version, r.created, r.updated, u.discord_id, u.username
       FROM reports r JOIN users u ON u.id = r.user_id
      WHERE r.game_id = ? AND r.cheat_id = ? AND r.fingerprint = ? AND u.banned = 0
      ORDER BY r.updated DESC`,
  ).bind(st.game_id, st.cheat_id, st.fingerprint).all<ReportRow>()).results;
  const fp = await env.DB.prepare(
    `SELECT first_seen, (SELECT MAX(first_seen) FROM fingerprints WHERE game_id = ?1) AS newest FROM fingerprints WHERE game_id = ?1 AND fingerprint = ?2`,
  ).bind(st.game_id, st.fingerprint).first<{ first_seen: number; newest: number }>();
  const day = new Date((t - 6 * 86400) * 1000).toISOString().slice(0, 10);
  const usage = await env.DB.prepare('SELECT COALESCE(SUM(count), 0) AS n FROM usage_daily WHERE game_id = ? AND cheat_id = ? AND day >= ?')
    .bind(st.game_id, st.cheat_id, day).first<{ n: number }>();
  const counted = rows.filter((r) => counts(st, r));
  const newest = !!fp && fp.first_seen === fp.newest;
  const sc = computeScore({ reports: counted, now: t, isNewestFingerprint: newest, fingerprintFirstSeen: fp?.first_seen ?? null, usage7d: usage?.n ?? 0 });
  const out: Summary & { reporters?: { discord_id: string; username: string; status: string; updated: number; note: string | null }[] } = {
    id: st.id, game_id: st.game_id, cheat_id: st.cheat_id, fingerprint: st.fingerprint, game_version: st.game_version,
    game_name: st.game_name, cheat_name: st.cheat_name, status: st.status, fixed_in_version: st.fixed_in_version, message_id: st.message_id,
    broken: sc.broken, works: sc.works,
    total_broken: rows.filter((r) => r.status === 'broken').length, total_works: rows.filter((r) => r.status === 'works').length,
    score: st.status === 'open' ? sc.score : 0, score_detail: sc,
    notes: counted.filter((r) => r.status === 'broken' && r.note).slice(0, 3).map((r) => ({ note: r.note!, vanta_version: r.vanta_version, updated: r.updated })),
    vanta_versions: [...new Set(counted.map((r) => r.vanta_version))].sort(cmpVersion).reverse().slice(0, 5),
    last_report: rows[0]?.updated ?? null,
    newest_fingerprint: newest,
  };
  if (withReporters) out.reporters = rows.slice(0, 25).map((r) => ({ discord_id: r.discord_id!, username: r.username!, status: r.status, updated: r.updated, note: r.note }));
  return out;
}

async function ensureState(env: Env, game: string, cheat: string, fp: string, names: { game_name: string | null; cheat_name: string | null; game_version: string | null }): Promise<CheatState> {
  const t = now();
  return (await env.DB.prepare(
    `INSERT INTO cheat_state (game_id, cheat_id, fingerprint, game_name, cheat_name, game_version, updated) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
     ON CONFLICT(game_id, cheat_id, fingerprint) DO UPDATE SET
       game_name = COALESCE(excluded.game_name, cheat_state.game_name), cheat_name = COALESCE(excluded.cheat_name, cheat_state.cheat_name),
       game_version = COALESCE(excluded.game_version, cheat_state.game_version), updated = excluded.updated
     RETURNING *`,
  ).bind(game, cheat, fp, names.game_name, names.cheat_name, names.game_version, t).first<CheatState>())!;
}

/** POST /reports: one report per user per (game, cheat, fingerprint); a new vote replaces the old one. */
export async function submitReport(req: Request, env: Env): Promise<Response> {
  const { user } = await requireUser(req, env);
  if (user.banned) throw new HttpError(403, 'banned', 'Je account mag geen meldingen meer versturen.');
  await rateLimit(env, LIMITS.reportUser, 'u' + user.id);
  await rateLimit(env, LIMITS.reportIp, await ipKey(env, req));
  const b = await readJson(req, 4096);
  const game = v.id(b.game_id, 'game_id'), cheat = v.id(b.cheat_id, 'cheat_id'), fp = v.fingerprint(b.fingerprint);
  const status = v.status(b.status);
  const vanta = v.version(b.vanta_version, 'vanta_version');
  const note = v.text(b.note, 'note', 300);
  const names = { game_name: v.text(b.game_name, 'game_name', 80), cheat_name: v.text(b.cheat_name, 'cheat_name', 80), game_version: v.text(b.game_version, 'game_version', 80) };
  const t = now();
  await env.DB.prepare('INSERT OR IGNORE INTO fingerprints (game_id, fingerprint, game_version, first_seen) VALUES (?, ?, ?, ?)').bind(game, fp, names.game_version, t).run();
  let st = await ensureState(env, game, cheat, fp, names);
  const prev = await env.DB.prepare('SELECT status FROM reports WHERE user_id = ? AND game_id = ? AND cheat_id = ? AND fingerprint = ?')
    .bind(user.id, game, cheat, fp).first<{ status: string }>();
  await env.DB.prepare(
    `INSERT INTO reports (user_id, game_id, cheat_id, fingerprint, game_version, vanta_version, status, note, created, updated)
     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?9)
     ON CONFLICT(user_id, game_id, cheat_id, fingerprint) DO UPDATE SET
       status = excluded.status, note = excluded.note, vanta_version = excluded.vanta_version,
       game_version = COALESCE(excluded.game_version, reports.game_version), updated = excluded.updated`,
  ).bind(user.id, game, cheat, fp, names.game_version, vanta, status, note, t).run();
  // A "broken" report from a Vanta version that should contain the fix reopens the issue.
  let reopened = false;
  if (status === 'broken' && st.status === 'fixed' && (!st.fixed_in_version || cmpVersion(vanta, st.fixed_in_version) >= 0)) {
    st = (await env.DB.prepare("UPDATE cheat_state SET status = 'open', status_changed = ?, updated = ? WHERE id = ? RETURNING *").bind(t, t, st.id).first<CheatState>())!;
    reopened = true;
  }
  await env.DB.prepare("INSERT INTO events (type, state_id, status, created) VALUES ('report', ?, ?, ?)").bind(st.id, status, t).run();
  const s = await summarize(env, st);
  return json({ ok: true, replaced: !!prev, reopened, community: communityEntry(s) });
}

/** DELETE /reports {game_id, cheat_id, fingerprint}: withdraw the vote ("Niet getest" / "Standaard"). */
export async function withdrawReport(req: Request, env: Env): Promise<Response> {
  const { user } = await requireUser(req, env);
  await rateLimit(env, LIMITS.reportUser, 'u' + user.id);
  const b = await readJson(req, 2048);
  const game = v.id(b.game_id, 'game_id'), cheat = v.id(b.cheat_id, 'cheat_id'), fp = v.fingerprint(b.fingerprint);
  const r = await env.DB.prepare('DELETE FROM reports WHERE user_id = ? AND game_id = ? AND cheat_id = ? AND fingerprint = ?').bind(user.id, game, cheat, fp).run();
  const st = await env.DB.prepare('SELECT * FROM cheat_state WHERE game_id = ? AND cheat_id = ? AND fingerprint = ?').bind(game, cheat, fp).first<CheatState>();
  if (st && r.meta.changes) await env.DB.prepare("INSERT INTO events (type, state_id, created) VALUES ('withdrawn', ?, ?)").bind(st.id, now()).run();
  return json({ ok: true, removed: r.meta.changes > 0 });
}

export async function myReports(req: Request, env: Env): Promise<Response> {
  const { user } = await requireUser(req, env);
  await rateLimit(env, LIMITS.accountUser, 'u' + user.id);
  const rows = await env.DB.prepare('SELECT game_id, cheat_id, fingerprint, game_version, vanta_version, status, note, created, updated FROM reports WHERE user_id = ? ORDER BY updated DESC LIMIT 500')
    .bind(user.id).all();
  return json({ reports: rows.results });
}

const communityEntry = (s: Summary) => ({ works: s.works, broken: s.broken, status: s.status, fixed_in_version: s.fixed_in_version });

/** GET /community/:game?fingerprint=  (public): per-cheat counts for that game version, for the Notes tab. */
export async function community(req: Request, env: Env, game: string): Promise<Response> {
  await rateLimit(env, LIMITS.readIp, await ipKey(env, req));
  v.id(game, 'game_id');
  const fpRaw = new URL(req.url).searchParams.get('fingerprint');
  const fp = fpRaw ? v.fingerprint(fpRaw) : null;
  const states = (fp
    ? await env.DB.prepare('SELECT * FROM cheat_state WHERE game_id = ? AND fingerprint = ?').bind(game, fp).all<CheatState>()
    : await env.DB.prepare(
      `SELECT * FROM cheat_state WHERE game_id = ?1 AND fingerprint = (SELECT fingerprint FROM fingerprints WHERE game_id = ?1 ORDER BY first_seen DESC LIMIT 1)`,
    ).bind(game).all<CheatState>()).results;
  const cheats: Record<string, ReturnType<typeof communityEntry>> = {};
  for (const st of states) cheats[st.cheat_id] = communityEntry(await summarize(env, st));
  return json({ game_id: game, fingerprint: fp ?? states[0]?.fingerprint ?? null, cheats }, 200, { 'Cache-Control': 'public, max-age=60' });
}

/** POST /usage {game_id, counts: {cheat_id: n}} (anonymous opt-in): daily aggregate only, no user, no IP. */
export async function usage(req: Request, env: Env): Promise<Response> {
  await rateLimit(env, LIMITS.usageIp, await ipKey(env, req));
  const b = await readJson(req, 8192);
  const game = v.id(b.game_id, 'game_id');
  const c = b.counts;
  if (typeof c !== 'object' || c === null || Array.isArray(c)) throw new HttpError(400, 'invalid_counts');
  const entries = Object.entries(c as Record<string, unknown>);
  if (entries.length === 0 || entries.length > 100) throw new HttpError(400, 'invalid_counts');
  const day = new Date().toISOString().slice(0, 10);
  const stmts = entries.map(([cheat, n]) => {
    v.id(cheat, 'cheat_id');
    if (typeof n !== 'number' || !Number.isInteger(n) || n < 1 || n > 1000) throw new HttpError(400, 'invalid_counts');
    return env.DB.prepare(`INSERT INTO usage_daily (day, game_id, cheat_id, count) VALUES (?, ?, ?, ?)
      ON CONFLICT(day, game_id, cheat_id) DO UPDATE SET count = usage_daily.count + excluded.count`).bind(day, game, cheat, n);
  });
  await env.DB.batch(stmts);
  return json({ ok: true });
}
