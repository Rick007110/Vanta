export interface Env {
  DB: D1Database;
  /** Public https URL of this worker (used for the OAuth redirect URI). */
  PUBLIC_URL: string;
  DISCORD_CLIENT_ID: string;
  DISCORD_CLIENT_SECRET: string;
  DISCORD_API: string;
  DISCORD_AUTHORIZE_URL: string;
  /** Random secret for hashing IP addresses in rate limits. */
  HASH_SECRET: string;
  /** Shared secret for the Discord bot (Authorization: Bearer ...) on /bot/* endpoints. */
  BOT_API_SECRET: string;
}
