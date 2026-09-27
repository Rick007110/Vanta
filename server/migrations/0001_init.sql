-- Vanta community backend (Cloudflare D1 / SQLite). Minimal data: no e-mail, no IP addresses (only short-lived
-- keyed hashes in rate_limits), no Discord access tokens.

CREATE TABLE users (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  discord_id  TEXT    NOT NULL UNIQUE,
  username    TEXT    NOT NULL,
  avatar      TEXT,
  created     INTEGER NOT NULL,             -- unix seconds
  banned      INTEGER NOT NULL DEFAULT 0
);

-- Session tokens are only stored as SHA-256 hashes.
CREATE TABLE sessions (
  token_hash  TEXT    PRIMARY KEY,
  user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created     INTEGER NOT NULL,
  last_used   INTEGER NOT NULL,
  expires     INTEGER NOT NULL
);
CREATE INDEX sessions_user ON sessions(user_id);

-- Pending desktop logins (10 minutes). code_hash is the one-time code handed to the loopback listener,
-- bound to the PKCE challenge of the Vanta instance that started the login.
CREATE TABLE auth_requests (
  id           TEXT    PRIMARY KEY,         -- OAuth state sent to Discord
  client_state TEXT    NOT NULL,
  challenge    TEXT    NOT NULL,
  port         INTEGER NOT NULL,
  created      INTEGER NOT NULL,
  user_id      INTEGER REFERENCES users(id) ON DELETE CASCADE,
  code_hash    TEXT UNIQUE,
  code_expires INTEGER
);

CREATE TABLE reports (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  game_id       TEXT    NOT NULL,
  cheat_id      TEXT    NOT NULL,
  fingerprint   TEXT    NOT NULL,
  game_version  TEXT,
  vanta_version TEXT    NOT NULL,
  status        TEXT    NOT NULL CHECK (status IN ('works', 'broken')),
  note          TEXT,
  created       INTEGER NOT NULL,
  updated       INTEGER NOT NULL,
  UNIQUE (user_id, game_id, cheat_id, fingerprint)
);
CREATE INDEX reports_cheat ON reports(game_id, cheat_id, fingerprint);
CREATE INDEX reports_updated ON reports(updated);

CREATE TABLE cheat_state (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  game_id          TEXT    NOT NULL,
  cheat_id         TEXT    NOT NULL,
  fingerprint      TEXT    NOT NULL,
  status           TEXT    NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'fixed', 'cant_reproduce', 'duplicate')),
  fixed_in_version TEXT,
  status_changed   INTEGER,                 -- reports older than this don't count while status != open
  game_name        TEXT,
  cheat_name       TEXT,
  game_version     TEXT,
  message_id       TEXT,                    -- Discord message that is edited on repeat reports
  updated          INTEGER NOT NULL,
  UNIQUE (game_id, cheat_id, fingerprint)
);

-- First time a game fingerprint was reported: "newest known version" and spike detection.
CREATE TABLE fingerprints (
  game_id      TEXT    NOT NULL,
  fingerprint  TEXT    NOT NULL,
  game_version TEXT,
  first_seen   INTEGER NOT NULL,
  PRIMARY KEY (game_id, fingerprint)
);

-- Opt-in anonymous usage: "cheat enabled" counts per UTC day. No user, no IP.
CREATE TABLE usage_daily (
  day      TEXT    NOT NULL,                -- YYYY-MM-DD
  game_id  TEXT    NOT NULL,
  cheat_id TEXT    NOT NULL,
  count    INTEGER NOT NULL,
  PRIMARY KEY (day, game_id, cheat_id)
);

-- Fixed-window rate limits. Keys contain a keyed daily hash of the IP, never the IP itself; rows expire within a day.
CREATE TABLE rate_limits (
  key     TEXT    PRIMARY KEY,
  window  INTEGER NOT NULL,
  count   INTEGER NOT NULL,
  expires INTEGER NOT NULL
);

-- Change feed for the Discord bot (polled with a cursor). Pruned after 30 days.
CREATE TABLE events (
  id       INTEGER PRIMARY KEY AUTOINCREMENT,
  type     TEXT    NOT NULL,                -- report | withdrawn | status
  state_id INTEGER NOT NULL REFERENCES cheat_state(id) ON DELETE CASCADE,
  status   TEXT,                            -- report status for type=report (works|broken)
  created  INTEGER NOT NULL
);
