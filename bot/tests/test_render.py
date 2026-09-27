from vanta_bot.render import clean, digest_embed, report_embed, top_embed

from .fakes import summary


def test_clean_escapes_markdown_and_mentions():
    out = clean("@everyone **bold** <@123> `x`\nline")
    assert "@everyone" not in out.replace("@\u200beveryone", "")
    assert "\\*\\*bold\\*\\*" in out and "\n" not in out
    assert clean("x" * 500, 10) == "x" * 9 + "…"
    assert clean(None) == ""


def test_report_embed_contents():
    e = report_embed(summary(notes=[{"note": "crash @here on load", "vanta_version": "0.3.0", "updated": 1}]))
    assert e.title == "God mode — The Game" and e.color.value == 0xE5484D
    fields = {f.name: f.value for f in e.fields}
    assert fields["Meldingen"] == "🔴 2 werkt niet · 🟢 1 werkt"
    assert fields["Prioriteit"] == "3.50"
    assert fields["Gameversie"] == "0.8.5 (nieuwste)"
    assert "@here" not in fields["Opmerkingen"].replace("@\u200bhere", "")
    assert e.footer.text == "tlc / godmode · #1"
    assert len(e) <= 6000


def test_reporters_only_when_requested():
    s = summary(reporters=[{"discord_id": "123456789012345678", "username": "a_b", "status": "broken", "updated": 1, "note": None}])
    assert not any("Melders" in f.name for f in report_embed(s).fields)
    f = [f for f in report_embed(s, reporters=True).fields if "Melders" in f.name][0]
    assert "<@123456789012345678>" in f.value and "a\\_b" in f.value


def test_huge_input_stays_within_discord_limits():
    s = summary(game_name="G" * 500, cheat_name="C" * 500, notes=[{"note": "n" * 400}] * 3, vanta_versions=["1"] * 50)
    e = report_embed(s)
    assert len(e.title) <= 256 and all(len(f.value) <= 1024 for f in e.fields) and len(e) <= 6000


def test_top_and_digest():
    assert "Geen open meldingen" in top_embed([]).description
    assert "**1.** God mode — The Game" in top_embed([summary()]).description
    d = digest_embed({"reports_period": 1, "broken_period": 1, "new_users": 0, "top": [], "fixed": [{"cheat_id": "a", "game_id": "g", "fixed_in_version": "0.3"}]}, "2026-W40")
    assert "1 melding deze week" in d.description and "0 nieuwe gebruikers" in d.description
    assert "✅ a — g (0.3)" in d.fields[1].value
