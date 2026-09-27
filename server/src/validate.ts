import { HttpError } from './util';

const ID_RX = /^[a-z0-9][a-z0-9_\-]{0,63}$/;
const VERSION_RX = /^[0-9A-Za-z.\-+]{1,32}$/;

export function id(v: unknown, field: string): string {
  if (typeof v !== 'string' || !ID_RX.test(v)) throw new HttpError(400, 'invalid_' + field);
  return v;
}

export function version(v: unknown, field: string): string {
  if (typeof v !== 'string' || !VERSION_RX.test(v)) throw new HttpError(400, 'invalid_' + field);
  return v;
}

/** Printable text without control characters, collapsed whitespace, max length (in code points). */
export function text(v: unknown, field: string, max: number, required = false): string | null {
  if (v === undefined || v === null || v === '') {
    if (required) throw new HttpError(400, 'missing_' + field);
    return null;
  }
  if (typeof v !== 'string') throw new HttpError(400, 'invalid_' + field);
  // strip control chars, bidi overrides and zero-width characters, collapse whitespace
  const clean = v
    .normalize('NFC')
    .replace(/[\u0000-\u0008\u000B-\u001F\u007F-\u009F\u200B-\u200F\u202A-\u202E\u2060-\u2069\uFEFF]/g, '')
    .replace(/[ \t\r\n]+/g, ' ')
    .trim();
  if ([...clean].length > max) throw new HttpError(400, field + '_too_long');
  if (!clean) {
    if (required) throw new HttpError(400, 'missing_' + field);
    return null;
  }
  return clean;
}

export function fingerprint(v: unknown): string {
  if (typeof v !== 'string' || v.length < 1 || v.length > 200 || !/^[\x20-\x7E]+$/.test(v)) throw new HttpError(400, 'invalid_fingerprint');
  return v.trim();
}

export function status(v: unknown): 'works' | 'broken' {
  if (v !== 'works' && v !== 'broken') throw new HttpError(400, 'invalid_status');
  return v;
}
