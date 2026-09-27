import type { Env } from './env';
import { HttpError, now } from './util';

export interface Limit { name: string; limit: number; windowSec: number }

export const LIMITS = {
  authStartIp: { name: 'auth-start', limit: 20, windowSec: 600 },
  authTokenIp: { name: 'auth-token', limit: 30, windowSec: 600 },
  reportUser: { name: 'report-user', limit: 30, windowSec: 3600 },
  reportIp: { name: 'report-ip', limit: 60, windowSec: 3600 },
  readIp: { name: 'read-ip', limit: 240, windowSec: 600 },
  usageIp: { name: 'usage-ip', limit: 30, windowSec: 3600 },
  accountUser: { name: 'account-user', limit: 60, windowSec: 600 },
} satisfies Record<string, Limit>;

/** Fixed-window counter in D1. Throws 429 with Retry-After when over the limit. */
export async function rateLimit(env: Env, l: Limit, subject: string): Promise<void> {
  const t = now();
  const win = Math.floor(t / l.windowSec);
  const key = `${l.name}:${subject}`;
  const row = await env.DB.prepare(
    `INSERT INTO rate_limits (key, window, count, expires) VALUES (?1, ?2, 1, ?3)
     ON CONFLICT(key) DO UPDATE SET
       count = CASE WHEN rate_limits.window = excluded.window THEN rate_limits.count + 1 ELSE 1 END,
       window = excluded.window, expires = excluded.expires
     RETURNING count`,
  ).bind(key, win, (win + 1) * l.windowSec).first<{ count: number }>();
  if ((row?.count ?? 0) > l.limit) {
    const retry = (win + 1) * l.windowSec - t;
    throw new HttpError(429, 'rate_limited', 'Te veel verzoeken, probeer het later opnieuw.', { 'Retry-After': String(Math.max(1, retry)) });
  }
}
