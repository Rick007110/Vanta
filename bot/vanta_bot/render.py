"""Embeds and texts (pure functions, unit-tested). All user-supplied text is escaped."""
from __future__ import annotations

import datetime as dt
from typing import Any, Dict, Iterable, List, Optional

import discord
from discord.utils import escape_markdown, escape_mentions

RED, GREEN, GREY, BLUE = 0xE5484D, 0x30A46C, 0x8B8D98, 0x5B8DEF
STATUS_LABEL = {
    "open": "Open",
    "fixed": "Fixed",
    "cant_reproduce": "Can't reproduce",
    "duplicate": "Duplicate",
}


def clean(text: Optional[str], limit: int = 200) -> str:
    """Escape markdown and mentions, strip newlines, truncate."""
    if not text:
        return ""
    t = " ".join(str(text).split())
    if len(t) > limit:
        t = t[: limit - 1].rstrip() + "…"
    return escape_markdown(escape_mentions(t))


def cheat_title(s: Dict[str, Any]) -> str:
    return clean(s.get("cheat_name") or s.get("cheat_id"), 100)


def game_title(s: Dict[str, Any]) -> str:
    return clean(s.get("game_name") or s.get("game_id"), 100)


def status_line(s: Dict[str, Any]) -> str:
    st = s.get("status", "open")
    if st == "fixed":
        v = s.get("fixed_in_version")
        return f"✅ Fixed in Vanta {clean(v, 40)}" if v else "✅ Fixed"
    if st == "cant_reproduce":
        return "⚪ Can't reproduce"
    if st == "duplicate":
        return "⚪ Duplicate"
    if s.get("broken", 0) > 0:
        return "🔴 Broken according to users"
    return "⚪ No current reports"


def color_for(s: Dict[str, Any]) -> int:
    st = s.get("status", "open")
    if st == "fixed":
        return GREEN
    if st == "open" and s.get("broken", 0) > 0:
        return RED
    return GREY


def _ts(unix: Optional[int]) -> Optional[dt.datetime]:
    return dt.datetime.fromtimestamp(unix, tz=dt.timezone.utc) if unix else None


def plural(n: int, one: str, many: str) -> str:
    return f"{n} {one if n == 1 else many}"


def report_embed(s: Dict[str, Any], *, reporters: bool = False) -> discord.Embed:
    e = discord.Embed(title=f"{cheat_title(s)} — {game_title(s)}"[:256], description=status_line(s), color=color_for(s),
                      timestamp=_ts(s.get("last_report")))
    e.add_field(name="Reports", value=f"🔴 {s.get('broken', 0)} broken · 🟢 {s.get('works', 0)} works", inline=True)
    if s.get("status") == "open":
        e.add_field(name="Priority", value=f"{float(s.get('score') or 0):.2f}", inline=True)
    gv = s.get("game_version") or s.get("fingerprint")
    if gv:
        extra = " (newest)" if s.get("newest_fingerprint") else ""
        e.add_field(name="Game version", value=clean(gv, 60) + extra, inline=True)
    vv = s.get("vanta_versions") or []
    if vv:
        e.add_field(name="Vanta", value=", ".join(clean(v, 20) for v in vv[:5]), inline=True)
    notes = s.get("notes") or []
    if notes:
        lines = [f"> {clean(n.get('note'), 300)}" for n in notes[:3]]
        e.add_field(name="Notes", value="\n".join(lines)[:1024], inline=False)
    if reporters and s.get("reporters"):
        rl = []
        for r in s["reporters"][:15]:
            icon = "🔴" if r.get("status") == "broken" else "🟢"
            rl.append(f"{icon} <@{int(r['discord_id'])}> ({clean(r.get('username'), 40)})")
        e.add_field(name="Reporters (admins only)", value="\n".join(rl)[:1024], inline=False)
    e.set_footer(text=f"{s.get('game_id')} / {s.get('cheat_id')} · #{s.get('id')}"[:2048])
    return e


def fixed_notice(s: Dict[str, Any]) -> str:
    v = s.get("fixed_in_version")
    return f"✅ **{cheat_title(s)}** ({game_title(s)}) is fixed" + (f" in Vanta {clean(v, 40)}." if v else ".")


def _line(i: int, s: Dict[str, Any], with_game: bool = True) -> str:
    game = f" — {game_title(s)}" if with_game else ""
    return f"**{i}.** {cheat_title(s)}{game} · 🔴 {s.get('broken', 0)} / 🟢 {s.get('works', 0)} · score {float(s.get('score') or 0):.2f}"


def top_embed(items: List[Dict[str, Any]], game: Optional[str] = None) -> discord.Embed:
    title = "Top broken cheats" + (f" — {clean(game, 64)}" if game else "")
    if not items:
        return discord.Embed(title=title, description="No open reports. 🎉", color=GREEN)
    lines = [_line(i, s, with_game=not game) for i, s in enumerate(items, 1)]
    return discord.Embed(title=title, description="\n".join(lines)[:4096], color=RED)


def game_embed(game: str, items: List[Dict[str, Any]], usage: Iterable[Dict[str, Any]]) -> discord.Embed:
    name = game_title(items[0]) if items else clean(game, 64)
    e = discord.Embed(title=f"Game: {name}", color=BLUE)
    open_items = [s for s in items if s.get("status") == "open" and s.get("broken", 0) > 0]
    closed = [s for s in items if s.get("status") != "open"]
    e.add_field(name=f"Open ({len(open_items)})", value="\n".join(_line(i, s, False) for i, s in enumerate(open_items[:10], 1)) or "None", inline=False)
    if closed:
        e.add_field(name="Resolved", value="\n".join(f"{cheat_title(s)} · {STATUS_LABEL.get(s['status'], s['status'])}" + (f" {clean(s.get('fixed_in_version'), 20)}" if s.get("fixed_in_version") else "") for s in closed[:10])[:1024], inline=False)
    u = list(usage)
    if u:
        e.add_field(name="Most used (7 days, anonymous)", value="\n".join(f"{clean(x['cheat_id'], 64)}: {x['n']}" for x in u[:10])[:1024], inline=False)
    return e


def stats_embed(st: Dict[str, Any]) -> discord.Embed:
    e = discord.Embed(title="Vanta statistics", color=BLUE)
    e.add_field(name="Users", value=f"{st.get('users', 0)} (+{st.get('new_users', 0)} this week)")
    e.add_field(name="Reports", value=f"{st.get('reports', 0)} total · {st.get('reports_period', 0)} this week")
    e.add_field(name="Open issues", value=str(st.get("open", 0)))
    e.add_field(name="Fixed this week", value=str(st.get("fixed_period", 0)))
    e.add_field(name="Cheat usage (anonymous)", value=str(st.get("usage_period", 0)))
    return e


def digest_embed(d: Dict[str, Any], week: str) -> discord.Embed:
    e = discord.Embed(title=f"Weekly digest {week}", color=BLUE,
                      description=f"{plural(d.get('reports_period', 0), 'report', 'reports')} this week, "
                                  f"of which {d.get('broken_period', 0)} 'broken'. "
                                  f"{plural(d.get('new_users', 0), 'new user', 'new users')}.")
    top = d.get("top") or []
    e.add_field(name="Highest priority", value="\n".join(_line(i, s) for i, s in enumerate(top[:5], 1))[:1024] or "Nothing open. 🎉", inline=False)
    fixed = d.get("fixed") or []
    if fixed:
        e.add_field(name="Fixed", value="\n".join(f"✅ {cheat_title(f)} — {game_title(f)}" + (f" ({clean(f.get('fixed_in_version'), 20)})" if f.get("fixed_in_version") else "") for f in fixed[:10])[:1024], inline=False)
    reqs = [r for r in (d.get("requests") or []) if isinstance(r, dict) and r.get("name")]
    if reqs:
        e.add_field(name="Most requested games", value="\n".join(
            f"{i}. {clean(r.get('name'), 60)} — {plural(int(r.get('votes') or 0), 'vote', 'votes')}"
            + (f" (+{int(r.get('votes_7d') or 0)} this week)" if r.get("votes_7d") else "")
            + (f" · {REQ_STATUS[r.get('status')]}" if r.get("status") in REQ_STATUS else "")
            for i, r in enumerate(reqs[:5], 1))[:1024], inline=False)
    return e


REQ_STATUS = {"planned": "planned", "in_progress": "in progress"}
