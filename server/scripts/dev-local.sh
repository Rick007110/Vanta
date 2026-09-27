#!/usr/bin/env sh
# Run the Worker fully locally (local D1 + fake Discord). Nothing touches Cloudflare or Discord.
#   sh scripts/dev-local.sh           -> API on http://127.0.0.1:8787, mock Discord on :8788
# Bot secret for local testing: printed below (also in .tmp/.dev.vars).
set -e
cd "$(dirname "$0")/.."
mkdir -p .tmp
PORT=${PORT:-8787}; MOCK=${MOCK_DISCORD_PORT:-8788}
[ -f .tmp/.dev.vars ] || cat > .tmp/.dev.vars <<VARS
DISCORD_CLIENT_SECRET=local-client-secret
HASH_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")
BOT_API_SECRET=$(node -e "console.log(require('crypto').randomBytes(32).toString('hex'))")
VARS
echo "BOT_API_SECRET: $(grep BOT_API_SECRET .tmp/.dev.vars | cut -d= -f2)"
npx wrangler d1 migrations apply vanta --local --persist-to .tmp/state >/dev/null
node scripts/mock-discord.mjs "$MOCK" & MPID=$!
trap 'kill $MPID 2>/dev/null' EXIT INT TERM
npx wrangler dev --local --port "$PORT" --ip 127.0.0.1 --persist-to .tmp/state --env-file .tmp/.dev.vars \
  --var PUBLIC_URL:"http://127.0.0.1:$PORT" --var DISCORD_CLIENT_ID:"100000000000000000" \
  --var DISCORD_API:"http://127.0.0.1:$MOCK/api/v10" --var DISCORD_AUTHORIZE_URL:"http://127.0.0.1:$MOCK/oauth2/authorize" \
  --test-scheduled
