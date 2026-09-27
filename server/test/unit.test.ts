import { describe, expect, it } from 'vitest';
import { cmpVersion, counts } from '../src/reports';
import { computeScore, decay } from '../src/score';
import * as v from '../src/validate';
import { safeEqual } from '../src/util';

const T = 1_800_000_000, D = 86400;
const r = (status: 'works' | 'broken', ageDays: number, createdDays = ageDays) => ({ status, updated: T - ageDays * D, created: T - createdDays * D });

describe('score', () => {
  it('decays with a 14 day half-life', () => {
    expect(decay(0)).toBe(1);
    expect(decay(14 * D)).toBeCloseTo(0.5);
    expect(decay(28 * D)).toBeCloseTo(0.25);
  });
  it('broken minus half the works, never negative', () => {
    const s = computeScore({ reports: [r('broken', 0), r('broken', 0), r('works', 0)], now: T, isNewestFingerprint: false, fingerprintFirstSeen: T - 30 * D });
    expect(s.score).toBe(1.5);
    expect(computeScore({ reports: [r('works', 0), r('works', 0), r('broken', 0)], now: T, isNewestFingerprint: false, fingerprintFirstSeen: null }).score).toBe(0);
  });
  it('newest fingerprint x1.5, spike up to x2, demand log boost', () => {
    const base = { reports: [r('broken', 0, 0)], now: T, fingerprintFirstSeen: T - 30 * D };
    expect(computeScore({ ...base, isNewestFingerprint: true }).score).toBe(1.5);
    const spikeReports = Array.from({ length: 5 }, () => r('broken', 0, 0));
    expect(computeScore({ reports: spikeReports, now: T, isNewestFingerprint: false, fingerprintFirstSeen: T - 1 * D }).score).toBe(10);
    expect(computeScore({ reports: spikeReports, now: T, isNewestFingerprint: false, fingerprintFirstSeen: T - 10 * D }).score).toBe(5);
    expect(computeScore({ ...base, isNewestFingerprint: false, usage7d: 99 }).score).toBe(1.2);
  });
  it('old reports weigh less', () => {
    const fresh = computeScore({ reports: [r('broken', 0)], now: T, isNewestFingerprint: false, fingerprintFirstSeen: null }).score;
    const old = computeScore({ reports: [r('broken', 28)], now: T, isNewestFingerprint: false, fingerprintFirstSeen: null }).score;
    expect(old).toBeCloseTo(fresh / 4, 2);
  });
});

describe('versions and counting', () => {
  it('compares versions numerically', () => {
    expect(cmpVersion('0.2.10', '0.2.9')).toBe(1);
    expect(cmpVersion('0.3', '0.3.0')).toBe(0);
    expect(cmpVersion('1.0.0-beta', '1.0.1')).toBe(-1);
  });
  it('counts only reports after a status change (and from the fixed version on)', () => {
    const open = { status: 'open' as const, status_changed: 100, fixed_in_version: null };
    expect(counts(open, { updated: 1, vanta_version: '0.1.0' })).toBe(true);
    const fixed = { status: 'fixed' as const, status_changed: 100, fixed_in_version: '0.3.0' };
    expect(counts(fixed, { updated: 99, vanta_version: '0.3.0' })).toBe(false);
    expect(counts(fixed, { updated: 101, vanta_version: '0.2.9' })).toBe(false);
    expect(counts(fixed, { updated: 101, vanta_version: '0.3.0' })).toBe(true);
    expect(counts({ status: 'cant_reproduce', status_changed: 100, fixed_in_version: null }, { updated: 101, vanta_version: '0.1.0' })).toBe(true);
  });
});

describe('validation', () => {
  it('ids', () => {
    expect(v.id('the-last-caretaker', 'x')).toBe('the-last-caretaker');
    for (const bad of ['', 'A', '-x', 'a b', 'a'.repeat(65), 5, null]) expect(() => v.id(bad, 'x')).toThrow();
  });
  it('text strips control, bidi and zero-width characters', () => {
    expect(v.text(' a\u200B\u202Eb\n\nc ', 'n', 300)).toBe('ab c');
    expect(v.text('', 'n', 300)).toBeNull();
    expect(() => v.text('x'.repeat(301), 'n', 300)).toThrow();
  });
  it('fingerprint and status', () => {
    expect(v.fingerprint('steam:12345|exe:abcd')).toBe('steam:12345|exe:abcd');
    expect(() => v.fingerprint('a\u0001')).toThrow();
    expect(() => v.status('maybe')).toThrow();
  });
  it('safeEqual', () => {
    expect(safeEqual('abc', 'abc')).toBe(true);
    expect(safeEqual('abc', 'abd')).toBe(false);
    expect(safeEqual('abc', 'abcd')).toBe(false);
  });
});
