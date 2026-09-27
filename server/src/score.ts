/**
 * Priority score per (game, cheat, fingerprint). See README.md ("Prioriteitsscore") for the formula.
 * Pure function so it can be unit-tested and reused by /bot/top, embeds and the digest.
 */
export const SCORE = {
  halfLifeDays: 14,
  worksWeight: 0.5,
  newestBoost: 1.5,
  spikeWindowHours: 72,       // reports within this window after a fingerprint first appeared count as "spike"
  spikeRecentDays: 7,         // spike boost only while the fingerprint is at most this old
  spikeSaturation: 5,         // 5 early broken reports = full spike boost
  spikeMaxBoost: 1.0,         // up to x2
  demandWeight: 0.1,          // x(1 + 0.1 * log10(1 + enables in the last 7 days))
};

export interface ScoreReport { status: 'works' | 'broken'; updated: number; created: number }
export interface ScoreInput {
  reports: ScoreReport[];      // one per unique (non-banned) user, already filtered to counted reports
  now: number;                 // unix seconds
  isNewestFingerprint: boolean;
  fingerprintFirstSeen: number | null;
  usage7d?: number;
}
export interface ScoreResult { score: number; broken: number; works: number; brokenWeighted: number; worksWeighted: number; spike: number; newest: boolean; demand: number }

export function decay(ageSec: number): number {
  const days = Math.max(0, ageSec) / 86400;
  return Math.pow(0.5, days / SCORE.halfLifeDays);
}

export function computeScore(i: ScoreInput): ScoreResult {
  let bw = 0, ww = 0, broken = 0, works = 0, early = 0;
  for (const r of i.reports) {
    const d = decay(i.now - r.updated);
    if (r.status === 'broken') {
      bw += d; broken++;
      if (i.fingerprintFirstSeen != null && r.created - i.fingerprintFirstSeen <= SCORE.spikeWindowHours * 3600) early++;
    } else { ww += d; works++; }
  }
  const base = Math.max(0, bw - SCORE.worksWeight * ww);
  const fresh = i.fingerprintFirstSeen != null && i.now - i.fingerprintFirstSeen <= SCORE.spikeRecentDays * 86400;
  const spike = fresh ? 1 + SCORE.spikeMaxBoost * Math.min(1, early / SCORE.spikeSaturation) : 1;
  const newest = i.isNewestFingerprint ? SCORE.newestBoost : 1;
  const demand = 1 + SCORE.demandWeight * Math.log10(1 + Math.max(0, i.usage7d ?? 0));
  const score = Math.round(base * newest * spike * demand * 100) / 100;
  return { score, broken, works, brokenWeighted: bw, worksWeighted: ww, spike, newest: i.isNewestFingerprint, demand };
}
