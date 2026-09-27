import { cloudflareTest, readD1Migrations } from '@cloudflare/vitest-pool-workers';
import path from 'node:path';
import { defineConfig } from 'vitest/config';

export default defineConfig(async () => {
  const migrations = await readD1Migrations(path.join(__dirname, 'migrations'));
  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: './wrangler.toml' },
        miniflare: {
          bindings: {
            TEST_MIGRATIONS: migrations,
            PUBLIC_URL: 'https://api.test',
            DISCORD_CLIENT_ID: '111111111111111111',
            DISCORD_CLIENT_SECRET: 'test-client-secret',
            DISCORD_API: 'https://discord.test/api/v10',
            DISCORD_AUTHORIZE_URL: 'https://discord.test/oauth2/authorize',
            HASH_SECRET: 'test-hash-secret-0123456789abcdef0123456789',
            BOT_API_SECRET: 'test-bot-secret-0123456789abcdef0123456789',
          },
        },
      }),
    ],
    test: { setupFiles: ['./test/setup.ts'] },
  };
});
