// Minimal fake Discord for local testing (no real Discord account needed).
//   node scripts/mock-discord.mjs [port]      default 8788
// /oauth2/authorize immediately "approves" and redirects back with ?code=mock-code&state=...
// Set MOCK_DISCORD_DENY=1 to simulate the user clicking "Cancel".
import http from 'node:http';

const port = Number(process.argv[2] ?? process.env.MOCK_DISCORD_PORT ?? 8788);
const user = { id: process.env.MOCK_DISCORD_USER_ID ?? '400000000000000001', username: 'mockuser', global_name: 'Mock User', avatar: null };
const read = (req) => new Promise((r) => { let b = ''; req.on('data', (c) => (b += c)); req.on('end', () => r(b)); });
const send = (res, status, body, headers = {}) => { res.writeHead(status, { 'Content-Type': 'application/json', ...headers }); res.end(typeof body === 'string' ? body : JSON.stringify(body)); };

http.createServer(async (req, res) => {
  const u = new URL(req.url, `http://127.0.0.1:${port}`);
  const body = await read(req);
  console.log(`[mock-discord] ${req.method} ${u.pathname}`);
  if (u.pathname === '/oauth2/authorize') {
    const back = new URL(u.searchParams.get('redirect_uri'));
    back.searchParams.set('state', u.searchParams.get('state') ?? '');
    if (process.env.MOCK_DISCORD_DENY) back.searchParams.set('error', 'access_denied'); else back.searchParams.set('code', 'mock-code');
    return send(res, 302, '', { Location: back.href });
  }
  if (u.pathname === '/api/v10/oauth2/token' && req.method === 'POST') {
    const p = new URLSearchParams(body);
    if (p.get('code') !== 'mock-code' || !p.get('client_secret')) return send(res, 400, { error: 'invalid_grant' });
    return send(res, 200, { access_token: 'mock-access', token_type: 'Bearer', scope: 'identify', expires_in: 604800 });
  }
  if (u.pathname === '/api/v10/users/@me') {
    if (req.headers.authorization !== 'Bearer mock-access') return send(res, 401, { message: '401: Unauthorized' });
    return send(res, 200, user);
  }
  if (u.pathname === '/api/v10/oauth2/token/revoke') return send(res, 200, {});
  send(res, 404, { message: 'not found' });
}).listen(port, '127.0.0.1', () => console.log(`[mock-discord] listening on http://127.0.0.1:${port}`));
