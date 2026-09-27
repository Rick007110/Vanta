import { env } from 'cloudflare:workers';
import { afterEach, describe, expect, it } from 'vitest';
import { vi } from 'vitest';
import { call, freshIp, login, mockDiscord, newUser, pkce, report } from './helpers';

afterEach(() => vi.unstubAllGlobals());

async function startLogin(ip: string) {
  const p = pkce();
  const r = await call('GET', `/auth/start?port=50123&state=${p.state}&challenge=${await p.challenge}`, { ip });
  return { p, r, sid: r.status === 302 ? new URL(r.headers.get('Location')!).searchParams.get('state')! : '' };
}

describe('desktop login (loopback + PKCE)', () => {
  it('redirects to Discord with identify scope, prompt=none and a server-side state', async () => {
    const { r, p } = await startLogin(freshIp());
    expect(r.status).toBe(302);
    const loc = new URL(r.headers.get('Location')!);
    expect(loc.origin + loc.pathname).toBe('https://discord.test/oauth2/authorize');
    expect(loc.searchParams.get('scope')).toBe('identify');
    expect(loc.searchParams.get('prompt')).toBe('none');
    expect(loc.searchParams.get('redirect_uri')).toBe('https://api.test/auth/callback');
    expect(loc.searchParams.get('state')).not.toBe(p.state); // Vanta's state never goes to Discord
  });

  it('rejects bad start parameters', async () => {
    for (const q of ['port=80&state=aaaaaaaaaaaaaaaaaaaa&challenge=' + 'a'.repeat(43), 'port=50000&state=short&challenge=' + 'a'.repeat(43), 'port=50000&state=aaaaaaaaaaaaaaaaaaaa&challenge=abc'])
      expect((await call('GET', '/auth/start?' + q, { ip: freshIp() })).status).toBe(400);
  });

  it('full flow: callback -> loopback code -> token; Discord token revoked; me works', async () => {
    const ip = freshIp();
    const m = mockDiscord({ id: '123456789012345678', username: 'alice', global_name: 'Alice', avatar: 'a_' + '0'.repeat(32) });
    const { p, sid } = await startLogin(ip);
    const cb = await call('GET', `/auth/callback?code=discord-code&state=${sid}`, { ip });
    expect(cb.status).toBe(302);
    const loc = new URL(cb.headers.get('Location')!);
    expect(loc.origin).toBe('http://127.0.0.1:50123');
    expect(loc.pathname).toBe('/callback');
    expect(loc.searchParams.get('state')).toBe(p.state);
    expect(m.revokes).toBe(1);
    const code = loc.searchParams.get('code')!;
    const tr = await call('POST', '/auth/token', { ip, body: { code, verifier: p.verifier } });
    expect(tr.status).toBe(200);
    const t = await tr.json<{ token: string; user: { id: string; username: string; avatarUrl: string } }>();
    expect(t.token).toMatch(/^vt_[A-Za-z0-9_-]{43}$/);
    expect(t.user).toMatchObject({ id: '123456789012345678', username: 'Alice' });
    expect(t.user.avatarUrl).toBe('https://cdn.discordapp.com/avatars/123456789012345678/a_' + '0'.repeat(32) + '.png?size=64');
    // code is single use
    expect((await call('POST', '/auth/token', { ip, body: { code, verifier: p.verifier } })).status).toBe(400);
    // nothing sensitive stored in plain text
    const s = await env.DB.prepare('SELECT token_hash FROM sessions').all<{ token_hash: string }>();
    expect(s.results.some((x) => x.token_hash === t.token)).toBe(false);
    const me = await call('GET', '/me', { token: t.token, ip });
    expect(me.status).toBe(200);
    expect((await me.json<{ user: { id: string } }>()).user.id).toBe('123456789012345678');
  });

  it('wrong PKCE verifier is rejected and burns the code', async () => {
    const ip = freshIp();
    mockDiscord(newUser());
    const { p, sid } = await startLogin(ip);
    const code = new URL((await call('GET', `/auth/callback?code=discord-code&state=${sid}`, { ip })).headers.get('Location')!).searchParams.get('code')!;
    const other = pkce();
    const r = await call('POST', '/auth/token', { ip, body: { code, verifier: other.verifier } });
    expect(r.status).toBe(400);
    expect((await r.json<{ error: string }>()).error).toBe('invalid_verifier');
    expect((await call('POST', '/auth/token', { ip, body: { code, verifier: p.verifier } })).status).toBe(400);
  });

  it('Discord errors are sent back to the loopback listener', async () => {
    const ip = freshIp();
    mockDiscord(newUser(), { failToken: true });
    const { p, sid } = await startLogin(ip);
    const loc = new URL((await call('GET', `/auth/callback?code=discord-code&state=${sid}`, { ip })).headers.get('Location')!);
    expect(loc.searchParams.get('error')).toBe('token_exchange_failed');
    expect(loc.searchParams.get('state')).toBe(p.state);
    const { sid: sid2 } = await startLogin(ip);
    const denied = new URL((await call('GET', `/auth/callback?error=access_denied&state=${sid2}`, { ip })).headers.get('Location')!);
    expect(denied.searchParams.get('error')).toBe('access_denied');
  });

  it('unknown or replayed callback state shows an error page, no redirect', async () => {
    const r = await call('GET', '/auth/callback?code=discord-code&state=' + 'x'.repeat(32), { ip: freshIp() });
    expect(r.status).toBe(400);
    expect(r.headers.get('Location')).toBeNull();
    expect(r.headers.get('Content-Security-Policy')).toContain("default-src 'none'");
  });

  it('upserts the user on repeat login (same discord id, new name)', async () => {
    const u = newUser('before');
    await login(u);
    const b = await login({ ...u, username: 'after', global_name: 'After' });
    expect(b.user.username).toBe('After');
    const n = await env.DB.prepare('SELECT COUNT(*) AS n FROM users WHERE discord_id = ?').bind(u.id).first<{ n: number }>();
    expect(n?.n).toBe(1);
  });

  it('rate limits /auth/start per IP', async () => {
    const ip = freshIp();
    const statuses: number[] = [];
    for (let i = 0; i < 22; i++) statuses.push((await startLogin(ip)).r.status);
    expect(statuses.slice(0, 20).every((s) => s === 302)).toBe(true);
    const last = await startLogin(ip);
    expect(last.r.status).toBe(429);
    expect(Number(last.r.headers.get('Retry-After'))).toBeGreaterThan(0);
  });

  it('session auth: bad tokens, logout', async () => {
    expect((await call('GET', '/me')).status).toBe(401);
    expect((await call('GET', '/me', { token: 'vt_' + 'a'.repeat(43) })).status).toBe(401);
    const { token } = await login(newUser());
    expect((await call('POST', '/auth/logout', { token })).status).toBe(200);
    expect((await call('GET', '/me', { token })).status).toBe(401);
  });

  it('DELETE /me removes user, sessions and reports and emits withdrawn events', async () => {
    const u = newUser('leaver');
    const { token } = await login(u);
    expect((await report(token, { game_id: 'del-game', cheat_id: 'godmode', note: 'crash' })).status).toBe(200);
    const before = await env.DB.prepare('SELECT MAX(id) AS id FROM events').first<{ id: number }>();
    const r = await call('DELETE', '/me', { token });
    expect(r.status).toBe(200);
    expect((await call('GET', '/me', { token })).status).toBe(401);
    const left = await env.DB.prepare("SELECT (SELECT COUNT(*) FROM users WHERE discord_id = ?1) AS u, (SELECT COUNT(*) FROM reports WHERE game_id = 'del-game') AS r").bind(u.id).first<{ u: number; r: number }>();
    expect(left).toEqual({ u: 0, r: 0 });
    const ev = await env.DB.prepare("SELECT type FROM events WHERE id > ?").bind(before!.id).all<{ type: string }>();
    expect(ev.results.map((e) => e.type)).toContain('withdrawn');
  });
});
