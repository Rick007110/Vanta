import type { Env } from './env';
import { authCallback, authStart, authToken, deleteAccount, logout, me } from './auth';
import { handleBot } from './bot';
import { community, myReports, submitReport, usage, withdrawReport } from './reports';
import { HttpError, json, now } from './util';

export type { Env };

async function route(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  const url = new URL(req.url);
  const p = url.pathname.replace(/\/+$/, '') || '/';
  const m = req.method;
  if (m === 'GET' && (p === '/' || p === '/health')) return json({ ok: true, service: 'vanta-api' });
  if (m === 'GET' && p === '/auth/start') return authStart(req, env);
  if (m === 'GET' && p === '/auth/callback') return authCallback(req, env, ctx);
  if (m === 'POST' && p === '/auth/token') return authToken(req, env);
  if (m === 'POST' && p === '/auth/logout') return logout(req, env);
  if (m === 'GET' && p === '/me') return me(req, env);
  if (m === 'DELETE' && p === '/me') return deleteAccount(req, env);
  if (m === 'GET' && p === '/reports/mine') return myReports(req, env);
  if (m === 'POST' && p === '/reports') return submitReport(req, env);
  if (m === 'DELETE' && p === '/reports') return withdrawReport(req, env);
  if (m === 'POST' && p === '/usage') return usage(req, env);
  const c = /^\/community\/([^/]+)$/.exec(p);
  if (m === 'GET' && c) return community(req, env, decodeURIComponent(c[1]));
  if (p.startsWith('/bot/')) return handleBot(req, env, p);
  throw new HttpError(404, 'not_found');
}

/** Hourly housekeeping: nothing personal lingers longer than needed. */
export async function cleanup(env: Env): Promise<void> {
  const t = now();
  await env.DB.batch([
    env.DB.prepare('DELETE FROM rate_limits WHERE expires < ?').bind(t),
    env.DB.prepare('DELETE FROM auth_requests WHERE created < ?').bind(t - 3600),
    env.DB.prepare('DELETE FROM sessions WHERE expires < ?').bind(t),
    env.DB.prepare('DELETE FROM events WHERE created < ?').bind(t - 30 * 86400),
    env.DB.prepare('DELETE FROM usage_daily WHERE day < ?').bind(new Date((t - 400 * 86400) * 1000).toISOString().slice(0, 10)),
  ]);
}

export default {
  async fetch(req: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    try {
      return await route(req, env, ctx);
    } catch (e) {
      if (e instanceof HttpError) return json({ error: e.code, message: e.message !== e.code ? e.message : undefined }, e.status, e.headers);
      console.error('unhandled', e instanceof Error ? e.stack : e);
      return json({ error: 'internal_error' }, 500);
    }
  },
  async scheduled(_c: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    ctx.waitUntil(cleanup(env));
  },
} satisfies ExportedHandler<Env>;
