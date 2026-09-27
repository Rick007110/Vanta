import { createExecutionContext, waitOnExecutionContext } from 'cloudflare:test';
import { env } from 'cloudflare:workers';
import { vi } from 'vitest';
import worker from '../src/index';
import { b64url, sha256b64url } from '../src/util';

export const BOT = 'test-bot-secret-0123456789abcdef0123456789';
let ipSeq = 1;
export const freshIp = () => `10.1.${Math.floor(ipSeq / 250)}.${(ipSeq++ % 250) + 1}`;

export interface CallOpts { body?: unknown; token?: string; ip?: string; headers?: Record<string, string>; raw?: BodyInit }
export async function call(method: string, path: string, o: CallOpts = {}): Promise<Response> {
  const h = new Headers(o.headers);
  h.set('CF-Connecting-IP', o.ip ?? '10.0.0.1');
  if (o.token) h.set('Authorization', `Bearer ${o.token}`);
  let body: BodyInit | undefined = o.raw;
  if (o.body !== undefined) { body = JSON.stringify(o.body); if (!h.has('Content-Type')) h.set('Content-Type', 'application/json'); }
  const ctx = createExecutionContext();
  const res = await worker.fetch(new Request(`https://api.test${path}`, { method, headers: h, body, redirect: 'manual' }), env, ctx);
  await waitOnExecutionContext(ctx);
  return res;
}
export const bot = (method: string, path: string, body?: unknown) => call(method, path, { body, headers: { Authorization: `Bearer ${BOT}` } });

export interface DiscordUser { id: string; username: string; global_name?: string | null; avatar?: string | null }
export interface DiscordMock { calls: { url: string; body: string }[]; failToken?: boolean; revokes: number }

/** Stubs global fetch so the worker's Discord calls hit a fake Discord. */
export function mockDiscord(user: DiscordUser, opts: { failToken?: boolean } = {}): DiscordMock {
  const m: DiscordMock = { calls: [], revokes: 0 };
  vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const body = typeof init?.body === 'string' ? init.body : init?.body instanceof URLSearchParams ? init.body.toString() : '';
    m.calls.push({ url, body });
    if (url === 'https://discord.test/api/v10/oauth2/token') {
      if (opts.failToken) return new Response('{"error":"invalid_grant"}', { status: 400 });
      const p = new URLSearchParams(body);
      if (p.get('client_secret') !== 'test-client-secret' || p.get('code') !== 'discord-code') return new Response('{}', { status: 401 });
      return Response.json({ access_token: 'discord-access', token_type: 'Bearer', scope: 'identify' });
    }
    if (url === 'https://discord.test/api/v10/users/@me') {
      if ((init?.headers as Record<string, string>)?.Authorization !== 'Bearer discord-access') return new Response('{}', { status: 401 });
      return Response.json(user);
    }
    if (url === 'https://discord.test/api/v10/oauth2/token/revoke') { m.revokes++; return new Response(null, { status: 200 }); }
    return new Response('unexpected ' + url, { status: 599 });
  });
  return m;
}

export function pkce() {
  const verifier = b64url(crypto.getRandomValues(new Uint8Array(32)));
  const state = b64url(crypto.getRandomValues(new Uint8Array(16)));
  return { verifier, state, challenge: sha256b64url(verifier) };
}

/** Full desktop login: /auth/start -> (Discord) -> /auth/callback -> loopback -> /auth/token. */
export async function login(user: DiscordUser, ip = freshIp()): Promise<{ token: string; user: { id: string; username: string } }> {
  mockDiscord(user);
  const p = pkce();
  const start = await call('GET', `/auth/start?port=50123&state=${p.state}&challenge=${await p.challenge}`, { ip });
  if (start.status !== 302) throw new Error('start ' + start.status + ' ' + (await start.text()));
  const sid = new URL(start.headers.get('Location')!).searchParams.get('state')!;
  const cb = await call('GET', `/auth/callback?code=discord-code&state=${sid}`, { ip });
  const loc = new URL(cb.headers.get('Location')!);
  const code = loc.searchParams.get('code');
  if (!code) throw new Error('callback ' + loc);
  const tr = await call('POST', '/auth/token', { ip, body: { code, verifier: p.verifier } });
  if (tr.status !== 200) throw new Error('token ' + tr.status + ' ' + (await tr.text()));
  vi.unstubAllGlobals();
  return tr.json();
}

let uid = 100000000000000000n;
export const newUser = (name = 'tester'): DiscordUser => ({ id: String(uid++), username: name, global_name: name, avatar: null });
export const report = (token: string, b: Record<string, unknown>, ip = freshIp()) => call('POST', '/reports', { token, ip, body: { vanta_version: '0.3.0', fingerprint: 'fp-1', status: 'broken', ...b } });
/** Logins must be sequential: the Discord mock is a global fetch stub. */
export async function logins(n: number) { const out = []; for (let i = 0; i < n; i++) out.push(await login(newUser('user' + i))); return out; }
