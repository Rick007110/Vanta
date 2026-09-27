"""Local stand-in for a Supabase project's HTTP API, for tests only (never deploy this).

  /rest/v1/*  -> proxied to PostgREST. Like the Supabase gateway, an "apikey" header is required; the publishable and
                 secret test keys are turned into anon / service_role JWTs when no user token is sent.
  /auth/v1/*  -> a small mock of Supabase Auth with the Discord provider: authorize (PKCE, loopback redirect), token
                 (grant_type=pkce and refresh_token, with refresh-token rotation), user, logout. Users are written to
                 auth.users like Supabase Auth does (so the vanta_profile_sync trigger runs).

Env: DATABASE_URL, JWT_SECRET, POSTGREST_URL (default http://127.0.0.1:54322), PORT (default 54321),
     MOCK_JWT_TTL (seconds, default 3600), MOCK_DISCORD_ID / MOCK_DISCORD_NAME (the "Discord user" that signs in).
Needs: aiohttp, asyncpg.
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import secrets
import time
import uuid
from urllib.parse import urlencode, urlparse

import asyncpg
from aiohttp import ClientSession, web

PUBLISHABLE = "sb_publishable_localtest"
SECRET = "sb_secret_localtest"
JWT_SECRET = os.environ.get("JWT_SECRET", "local-test-jwt-secret-that-is-at-least-32-chars")
POSTGREST = os.environ.get("POSTGREST_URL", "http://127.0.0.1:54322")
TTL = int(os.environ.get("MOCK_JWT_TTL", "3600"))


def b64(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def jwt(claims: dict) -> str:
    head = b64(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
    body = b64(json.dumps(claims).encode())
    sig = b64(hmac.new(JWT_SECRET.encode(), f"{head}.{body}".encode(), hashlib.sha256).digest())
    return f"{head}.{body}.{sig}"


def verify(token: str) -> dict | None:
    try:
        head, body, sig = token.split(".")
        good = b64(hmac.new(JWT_SECRET.encode(), f"{head}.{body}".encode(), hashlib.sha256).digest())
        if not hmac.compare_digest(sig, good):
            return None
        c = json.loads(base64.urlsafe_b64decode(body + "=" * (-len(body) % 4)))
        return c if c.get("exp", 0) > time.time() else None
    except ValueError:
        return None


ANON_JWT = jwt({"role": "anon", "iss": "supabase", "exp": 4102444800})
SERVICE_JWT = jwt({"role": "service_role", "iss": "supabase", "exp": 4102444800})


def gerr(status: int, code: str, msg: str) -> web.Response:  # Supabase Auth error shape
    return web.json_response({"code": status, "error_code": code, "msg": msg}, status=status)


def is_loopback_redirect(u: str) -> bool:
    p = urlparse(u)
    return p.scheme == "http" and p.hostname in ("127.0.0.1", "::1") and p.port is not None


class App:
    def __init__(self) -> None:
        self.codes: dict = {}      # code -> (challenge, discord_id, name)
        self.refresh: dict = {}    # refresh token -> (user_id, session_id)
        self.pool: asyncpg.Pool | None = None
        self.http: ClientSession | None = None
        self.stats = {"authorize": 0, "pkce": 0, "refresh": 0, "logout": 0, "user": 0}

    async def start(self, app: web.Application) -> None:
        self.pool = await asyncpg.create_pool(os.environ["DATABASE_URL"], min_size=1, max_size=4)
        self.http = ClientSession()

    async def stop(self, app: web.Application) -> None:
        await self.http.close()
        await self.pool.close()

    def apikey_ok(self, r: web.Request) -> bool:
        return r.headers.get("apikey") in (PUBLISHABLE, SECRET)

    # ---------------- auth ----------------
    async def authorize(self, r: web.Request) -> web.StreamResponse:
        self.stats["authorize"] += 1
        q = r.query
        if q.get("provider") != "discord":
            return gerr(400, "validation_failed", "Unsupported provider")
        redirect = q.get("redirect_to", "")
        if not is_loopback_redirect(redirect):
            return gerr(400, "validation_failed", "redirect_to not allowed in this mock")
        if q.get("code_challenge_method", "").lower() != "s256" or len(q.get("code_challenge", "")) < 43:
            return gerr(400, "validation_failed", "PKCE required")
        did = q.get("mock_discord_id") or os.environ.get("MOCK_DISCORD_ID", "400000000000000777")
        name = q.get("mock_name") or os.environ.get("MOCK_DISCORD_NAME", "tester")
        sep = "&" if "?" in redirect else "?"
        if q.get("mock_deny"):
            raise web.HTTPFound(redirect + sep + urlencode({"error": "access_denied", "error_code": "access_denied",
                                                            "error_description": "The resource owner denied the request"}))
        code = str(uuid.uuid4())
        self.codes[code] = (q["code_challenge"], did, name)
        raise web.HTTPFound(redirect + sep + urlencode({"code": code}))

    def user_json(self, row) -> dict:
        return {"id": str(row["id"]), "aud": "authenticated", "role": "authenticated", "email": row["email"],
                "app_metadata": json.loads(row["raw_app_meta_data"]), "user_metadata": json.loads(row["raw_user_meta_data"])}

    async def issue(self, user_id, session_id) -> dict:
        row = await self.pool.fetchrow("select * from auth.users where id = $1", user_id)
        now = int(time.time())
        access = jwt({"sub": str(user_id), "role": "authenticated", "aud": "authenticated", "session_id": str(session_id),
                      "iat": now, "exp": now + TTL})
        rt = secrets.token_urlsafe(24)
        self.refresh[rt] = (user_id, session_id)
        return {"access_token": access, "token_type": "bearer", "expires_in": TTL, "expires_at": now + TTL,
                "refresh_token": rt, "user": self.user_json(row)}

    async def token(self, r: web.Request) -> web.Response:
        if not self.apikey_ok(r):
            return web.json_response({"message": "No API key found in request"}, status=401)
        body = await r.json()
        grant = r.query.get("grant_type")
        if grant == "pkce":
            self.stats["pkce"] += 1
            item = self.codes.pop(body.get("auth_code", ""), None)
            if not item:
                return gerr(404, "flow_state_not_found", "invalid flow state, no valid flow state found")
            challenge, did, name = item
            if b64(hashlib.sha256(body.get("code_verifier", "").encode()).digest()) != challenge:
                return gerr(400, "bad_code_verifier", "code challenge does not match previously saved code verifier")
            meta = {"iss": "https://discord.com/api", "sub": did, "provider_id": did, "full_name": name, "name": f"{name}#0",
                    "avatar_url": "https://cdn.discordapp.com/embed/avatars/1.png", "picture": "https://cdn.discordapp.com/embed/avatars/1.png",
                    "custom_claims": {"global_name": name}, "email": f"{name}@example.invalid", "email_verified": True}
            app_meta = {"provider": "discord", "providers": ["discord"]}
            async with self.pool.acquire() as c:
                uid = await c.fetchval("select user_id from auth.identities where provider = 'discord' and provider_id = $1", did)
                if uid is None:
                    uid = await c.fetchval("insert into auth.users (email, raw_user_meta_data, raw_app_meta_data) values ($1, $2::jsonb, $3::jsonb) returning id",
                                           meta["email"], json.dumps(meta), json.dumps(app_meta))
                    await c.execute("insert into auth.identities (user_id, provider, provider_id, identity_data) values ($1, 'discord', $2, $3::jsonb)",
                                    uid, did, json.dumps(meta))
                else:
                    await c.execute("update auth.users set raw_user_meta_data = $2::jsonb, updated_at = now() where id = $1", uid, json.dumps(meta))
                sid = await c.fetchval("insert into auth.sessions (user_id) values ($1) returning id", uid)
            return web.json_response(await self.issue(uid, sid))
        if grant == "refresh_token":
            self.stats["refresh"] += 1
            item = self.refresh.pop(body.get("refresh_token", ""), None)
            if not item or not await self.pool.fetchval("select 1 from auth.sessions where id = $1", item[1]):
                return gerr(400, "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found")
            return web.json_response(await self.issue(*item))
        return gerr(400, "validation_failed", "unsupported grant_type")

    async def current(self, r: web.Request):
        c = verify(r.headers.get("Authorization", "").removeprefix("Bearer "))
        if not c or c.get("role") != "authenticated":
            return None, gerr(401, "bad_jwt", "invalid JWT")
        return c, None

    async def user(self, r: web.Request) -> web.Response:
        self.stats["user"] += 1
        c, err = await self.current(r)
        if err:
            return err
        row = await self.pool.fetchrow("select * from auth.users where id = $1", uuid.UUID(c["sub"]))
        if not row:
            return gerr(403, "user_not_found", "User from sub claim in JWT does not exist")
        return web.json_response(self.user_json(row))

    async def logout(self, r: web.Request) -> web.Response:
        self.stats["logout"] += 1
        c, err = await self.current(r)
        if err:
            return err
        await self.pool.execute("delete from auth.sessions where id = $1", uuid.UUID(c["session_id"]))
        return web.Response(status=204)

    async def mock_stats(self, r: web.Request) -> web.Response:
        return web.json_response(self.stats)

    # ---------------- rest ----------------
    async def rest(self, r: web.Request) -> web.StreamResponse:
        if not self.apikey_ok(r):
            return web.json_response({"message": "No API key found in request", "hint": "No `apikey` request header or url param was found."}, status=401)
        headers = {k: v for k, v in r.headers.items() if k.lower() in ("content-type", "accept", "prefer", "authorization", "x-forwarded-for")}
        auth = headers.get("Authorization") or headers.get("authorization")
        if auth and auth.removeprefix("Bearer ").startswith("sb_"):
            return web.json_response({"message": "Invalid JWT"}, status=401)   # new API keys are not JWTs
        if not auth:
            headers["Authorization"] = "Bearer " + (SERVICE_JWT if r.headers["apikey"] == SECRET else ANON_JWT)
        headers.setdefault("X-Forwarded-For", r.remote or "127.0.0.1")
        async with self.http.request(r.method, POSTGREST + r.path_qs.removeprefix("/rest/v1"), data=await r.read(), headers=headers) as up:
            return web.Response(status=up.status, body=await up.read(), content_type=up.content_type)


def make_app() -> web.Application:
    a = App()
    app = web.Application()
    app.on_startup.append(a.start)
    app.on_cleanup.append(a.stop)
    app.router.add_get("/auth/v1/authorize", a.authorize)
    app.router.add_post("/auth/v1/token", a.token)
    app.router.add_get("/auth/v1/user", a.user)
    app.router.add_post("/auth/v1/logout", a.logout)
    app.router.add_get("/mock/stats", a.mock_stats)
    app.router.add_route("*", "/rest/v1/{tail:.*}", a.rest)
    return app


if __name__ == "__main__":
    web.run_app(make_app(), host="127.0.0.1", port=int(os.environ.get("PORT", "54321")), print=lambda *_: print("mock supabase on :%s" % os.environ.get("PORT", "54321"), flush=True))
