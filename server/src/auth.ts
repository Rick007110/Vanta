import type { Env } from './env';
import { LIMITS, rateLimit } from './ratelimit';
import { HttpError, escapeHtml, html, ipKey, json, now, randomToken, readJson, safeEqual, sha256b64url, sha256hex } from './util';

const LOGIN_TTL = 600;          // pending login: 10 minutes
const CODE_TTL = 120;           // one-time code for the loopback listener: 2 minutes
const SESSION_TTL = 180 * 86400;

export interface User { id: number; discord_id: string; username: string; avatar: string | null; created: number; banned: number }

export function avatarUrl(u: Pick<User, 'discord_id' | 'avatar'>): string {
  if (u.avatar && /^(a_)?[0-9a-f]{32}$/.test(u.avatar)) return `https://cdn.discordapp.com/avatars/${u.discord_id}/${u.avatar}.png?size=64`;
  let idx = 0;
  try { idx = Number((BigInt(u.discord_id) >> 22n) % 6n); } catch { /* keep 0 */ }
  return `https://cdn.discordapp.com/embed/avatars/${idx}.png`;
}

export const publicUser = (u: User) => ({ id: u.discord_id, username: u.username, avatarUrl: avatarUrl(u), banned: !!u.banned });

/**
 * GET /auth/start?port=&state=&challenge=
 * Started by Vanta in the system browser. port = Vanta's loopback listener, state = Vanta's own CSRF value,
 * challenge = base64url(SHA-256(code_verifier)) (PKCE S256): only the Vanta instance holding the verifier can redeem the login.
 */
export async function authStart(req: Request, env: Env): Promise<Response> {
  await rateLimit(env, LIMITS.authStartIp, await ipKey(env, req));
  const u = new URL(req.url);
  const port = Number(u.searchParams.get('port'));
  const state = u.searchParams.get('state') ?? '';
  const challenge = u.searchParams.get('challenge') ?? '';
  if (!Number.isInteger(port) || port < 1024 || port > 65535 || !/^[A-Za-z0-9_-]{16,128}$/.test(state) || !/^[A-Za-z0-9_-]{43}$/.test(challenge))
    return html('<h1>Ongeldige aanvraag</h1><p>Start het inloggen opnieuw vanuit Vanta.</p>', 400);
  if (!env.DISCORD_CLIENT_ID) return html('<h1>Nog niet ingesteld</h1><p>De server heeft nog geen Discord-applicatie.</p>', 503);
  const id = randomToken(24);
  await env.DB.prepare('INSERT INTO auth_requests (id, client_state, challenge, port, created) VALUES (?, ?, ?, ?, ?)')
    .bind(id, state, challenge, port, now()).run();
  const q = new URLSearchParams({
    response_type: 'code',
    client_id: env.DISCORD_CLIENT_ID,
    scope: 'identify',
    redirect_uri: redirectUri(env),
    state: id,
    prompt: 'none',
  });
  return Response.redirect(`${env.DISCORD_AUTHORIZE_URL}?${q}`, 302);
}

const redirectUri = (env: Env) => `${env.PUBLIC_URL.replace(/\/+$/, '')}/auth/callback`;
const loopback = (port: number, q: Record<string, string>) => `http://127.0.0.1:${port}/callback?${new URLSearchParams(q)}`;

/** GET /auth/callback?code=&state= (from Discord). Exchanges the code, creates/updates the user, redirects to the loopback. */
export async function authCallback(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  const u = new URL(req.url);
  const id = u.searchParams.get('state') ?? '';
  const ar = /^[A-Za-z0-9_-]{16,64}$/.test(id)
    ? await env.DB.prepare('SELECT * FROM auth_requests WHERE id = ? AND code_hash IS NULL').bind(id).first<{ id: string; client_state: string; port: number; created: number }>()
    : null;
  if (!ar || now() - ar.created > LOGIN_TTL) return html('<h1>Inloggen verlopen</h1><p>Deze inlogpoging is ongeldig of verlopen. Probeer het opnieuw vanuit Vanta.</p>', 400);
  const fail = async (error: string) => {
    await env.DB.prepare('DELETE FROM auth_requests WHERE id = ?').bind(ar.id).run();
    return Response.redirect(loopback(ar.port, { state: ar.client_state, error }), 302);
  };
  const err = u.searchParams.get('error');
  if (err) return fail(err === 'access_denied' ? 'access_denied' : 'discord_error');
  const code = u.searchParams.get('code');
  if (!code || code.length > 200) return fail('missing_code');

  let access: string;
  try {
    const tr = await fetch(`${env.DISCORD_API}/oauth2/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ client_id: env.DISCORD_CLIENT_ID, client_secret: env.DISCORD_CLIENT_SECRET, grant_type: 'authorization_code', code, redirect_uri: redirectUri(env) }),
    });
    if (!tr.ok) return fail('token_exchange_failed');
    const tok = await tr.json<{ access_token?: string; scope?: string }>();
    if (!tok.access_token) return fail('token_exchange_failed');
    access = tok.access_token;
  } catch { return fail('discord_unreachable'); }

  let me: { id?: string; username?: string; global_name?: string | null; avatar?: string | null };
  try {
    const mr = await fetch(`${env.DISCORD_API}/users/@me`, { headers: { Authorization: `Bearer ${access}` } });
    if (!mr.ok) return fail('profile_failed');
    me = await mr.json();
  } catch { return fail('discord_unreachable'); }
  // We only needed the profile once: revoke the Discord token right away (best effort) and never store it.
  ctx.waitUntil(fetch(`${env.DISCORD_API}/oauth2/token/revoke`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: env.DISCORD_CLIENT_ID, client_secret: env.DISCORD_CLIENT_SECRET, token: access, token_type_hint: 'access_token' }),
  }).catch(() => undefined));

  if (!me.id || !/^\d{5,25}$/.test(me.id)) return fail('profile_failed');
  const name = (me.global_name || me.username || 'Discord-gebruiker').replace(/[\u0000-\u001F\u007F]/g, '').slice(0, 64);
  const avatar = me.avatar && /^(a_)?[0-9a-f]{32}$/.test(me.avatar) ? me.avatar : null;
  const user = await env.DB.prepare(
    `INSERT INTO users (discord_id, username, avatar, created) VALUES (?1, ?2, ?3, ?4)
     ON CONFLICT(discord_id) DO UPDATE SET username = excluded.username, avatar = excluded.avatar
     RETURNING *`,
  ).bind(me.id, name, avatar, now()).first<User>();

  const oneTime = randomToken(32);
  await env.DB.prepare('UPDATE auth_requests SET user_id = ?, code_hash = ?, code_expires = ? WHERE id = ?')
    .bind(user!.id, await sha256hex(oneTime), now() + CODE_TTL, ar.id).run();
  return Response.redirect(loopback(ar.port, { state: ar.client_state, code: oneTime }), 302);
}

/** POST /auth/token {code, verifier} -> {token, user}. The session token only travels in this response body. */
export async function authToken(req: Request, env: Env): Promise<Response> {
  await rateLimit(env, LIMITS.authTokenIp, await ipKey(env, req));
  const b = await readJson<{ code?: unknown; verifier?: unknown }>(req);
  if (typeof b.code !== 'string' || typeof b.verifier !== 'string' || !/^[A-Za-z0-9_-]{43,128}$/.test(b.verifier) || b.code.length > 100)
    throw new HttpError(400, 'invalid_request');
  const ar = await env.DB.prepare('SELECT * FROM auth_requests WHERE code_hash = ?').bind(await sha256hex(b.code)).first<{ id: string; challenge: string; user_id: number; code_expires: number }>();
  if (!ar) throw new HttpError(400, 'invalid_code');
  // single use, whatever happens next
  await env.DB.prepare('DELETE FROM auth_requests WHERE id = ?').bind(ar.id).run();
  if (now() > ar.code_expires) throw new HttpError(400, 'code_expired');
  if (!safeEqual(await sha256b64url(b.verifier), ar.challenge)) throw new HttpError(400, 'invalid_verifier');
  const token = 'vt_' + randomToken(32);
  const t = now();
  await env.DB.prepare('INSERT INTO sessions (token_hash, user_id, created, last_used, expires) VALUES (?, ?, ?, ?, ?)')
    .bind(await sha256hex(token), ar.user_id, t, t, t + SESSION_TTL).run();
  const user = await env.DB.prepare('SELECT * FROM users WHERE id = ?').bind(ar.user_id).first<User>();
  return json({ token, expires: t + SESSION_TTL, user: publicUser(user!) });
}

/** Resolves the Bearer session token; throws 401. */
export async function requireUser(req: Request, env: Env): Promise<{ user: User; tokenHash: string }> {
  const h = req.headers.get('Authorization') ?? '';
  const m = /^Bearer (vt_[A-Za-z0-9_-]{43})$/.exec(h);
  if (!m) throw new HttpError(401, 'unauthorized');
  const th = await sha256hex(m[1]);
  const row = await env.DB.prepare(
    'SELECT u.*, s.expires AS s_expires, s.last_used AS s_last FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token_hash = ?',
  ).bind(th).first<User & { s_expires: number; s_last: number }>();
  if (!row || row.s_expires < now()) throw new HttpError(401, 'unauthorized');
  if (now() - row.s_last > 3600) await env.DB.prepare('UPDATE sessions SET last_used = ? WHERE token_hash = ?').bind(now(), th).run();
  const { s_expires: _e, s_last: _l, ...user } = row;
  return { user, tokenHash: th };
}

export async function me(req: Request, env: Env): Promise<Response> {
  const { user } = await requireUser(req, env);
  await rateLimit(env, LIMITS.accountUser, 'u' + user.id);
  const c = await env.DB.prepare('SELECT COUNT(*) AS n FROM reports WHERE user_id = ?').bind(user.id).first<{ n: number }>();
  return json({ user: publicUser(user), reports: c?.n ?? 0 });
}

export async function logout(req: Request, env: Env): Promise<Response> {
  const { tokenHash } = await requireUser(req, env);
  await env.DB.prepare('DELETE FROM sessions WHERE token_hash = ?').bind(tokenHash).run();
  return json({ ok: true });
}

/** DELETE /me: GDPR erasure. Deletes the user, all sessions and all reports (not anonymized: removed). */
export async function deleteAccount(req: Request, env: Env): Promise<Response> {
  const { user } = await requireUser(req, env);
  const affected = await env.DB.prepare(
    `SELECT DISTINCT cs.id FROM reports r JOIN cheat_state cs ON cs.game_id = r.game_id AND cs.cheat_id = r.cheat_id AND cs.fingerprint = r.fingerprint WHERE r.user_id = ?`,
  ).bind(user.id).all<{ id: number }>();
  const t = now();
  await env.DB.batch([
    env.DB.prepare('DELETE FROM reports WHERE user_id = ?').bind(user.id),
    env.DB.prepare('DELETE FROM sessions WHERE user_id = ?').bind(user.id),
    env.DB.prepare('DELETE FROM auth_requests WHERE user_id = ?').bind(user.id),
    env.DB.prepare('DELETE FROM users WHERE id = ?').bind(user.id),
    // the bot re-renders the affected Discord messages (counts drop, notes disappear)
    ...affected.results.map((a) => env.DB.prepare("INSERT INTO events (type, state_id, created) VALUES ('withdrawn', ?, ?)").bind(a.id, t)),
  ]);
  return json({ ok: true, deletedReports: affected.results.length > 0 });
}

