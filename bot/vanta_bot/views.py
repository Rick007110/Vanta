"""Persistent buttons under report messages. DynamicItem = they keep working after a restart:
the state id is encoded in the custom_id (vanta:<action>:<state_id>)."""
from __future__ import annotations

import logging
import re
from typing import TYPE_CHECKING, Any, Dict, Optional

import discord

from .api import ApiError
from .render import report_embed

if TYPE_CHECKING:
    from .client import VantaBot

log = logging.getLogger("vanta.views")
ACTIONS = {"fix": "fixed", "cnr": "cant_reproduce", "dup": "duplicate", "open": "open"}
LABELS = {"fix": ("Gefixt", discord.ButtonStyle.success), "cnr": ("Niet reproduceerbaar", discord.ButtonStyle.secondary),
          "dup": ("Dubbel", discord.ButtonStyle.secondary), "open": ("Heropenen", discord.ButtonStyle.primary)}
VERSION_RE = re.compile(r"^[0-9A-Za-z][0-9A-Za-z.\-+]{0,31}$")


def view_for(state: Dict[str, Any]) -> discord.ui.View:
    v = discord.ui.View(timeout=None)
    acts = ["fix", "cnr", "dup"] if state.get("status") == "open" else ["open"]
    for a in acts:
        v.add_item(StatusButton(a, int(state["id"])))
    return v


async def apply_status(bot: "VantaBot", interaction: discord.Interaction, state_id: int, action: str, version: Optional[str]) -> None:
    try:
        res = await bot.api.set_status(ACTIONS[action], state_id=state_id, fixed_in_version=version)
    except ApiError as e:
        log.warning("status change failed: %s", e)
        await interaction.followup.send(f"Mislukt: {e.code}", ephemeral=True)
        return
    st = res["updated"][0]
    if interaction.message is not None:
        try:
            await interaction.message.edit(embed=report_embed(st), view=view_for(st))
        except discord.HTTPException as e:
            log.warning("could not edit message: %s", e)
    log.info("%s set #%s to %s%s", interaction.user.id, state_id, st["status"], f" ({version})" if version else "")
    await interaction.followup.send(f"Status: **{st['status']}**" + (f" (Vanta {version})" if version else ""), ephemeral=True)


class FixedModal(discord.ui.Modal, title="Gefixt in welke versie?"):
    version: discord.ui.TextInput = discord.ui.TextInput(label="Vanta-versie (optioneel)", placeholder="0.2.3", required=False, max_length=32)

    def __init__(self, bot: "VantaBot", state_id: int):
        super().__init__(timeout=300)
        self.bot, self.state_id = bot, state_id

    async def on_submit(self, interaction: discord.Interaction) -> None:
        v = (self.version.value or "").strip().lstrip("vV") or None
        if v and not VERSION_RE.match(v):
            await interaction.response.send_message("Ongeldige versie. Voorbeeld: 0.2.3", ephemeral=True)
            return
        await interaction.response.defer(ephemeral=True, thinking=False)
        await apply_status(self.bot, interaction, self.state_id, "fix", v)


class StatusButton(discord.ui.DynamicItem[discord.ui.Button], template=r"vanta:(?P<action>fix|cnr|dup|open):(?P<id>\d{1,12})"):
    def __init__(self, action: str, state_id: int):
        label, style = LABELS[action]
        super().__init__(discord.ui.Button(label=label, style=style, custom_id=f"vanta:{action}:{state_id}"))
        self.action, self.state_id = action, state_id

    @classmethod
    async def from_custom_id(cls, interaction: discord.Interaction, item: discord.ui.Button, match: "re.Match[str]") -> "StatusButton":
        return cls(match["action"], int(match["id"]))

    async def interaction_check(self, interaction: discord.Interaction) -> bool:
        bot: VantaBot = interaction.client  # type: ignore[assignment]
        if not bot.config.is_admin(interaction.user.id):
            await interaction.response.send_message("Alleen beheerders kunnen de status wijzigen.", ephemeral=True)
            return False
        return True

    async def callback(self, interaction: discord.Interaction) -> None:
        bot: VantaBot = interaction.client  # type: ignore[assignment]
        if self.action == "fix":
            await interaction.response.send_modal(FixedModal(bot, self.state_id))
            return
        await interaction.response.defer(ephemeral=True, thinking=False)
        await apply_status(bot, interaction, self.state_id, self.action, None)
