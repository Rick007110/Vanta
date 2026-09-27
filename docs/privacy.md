# Privacy — community-meldingen

*English below.*

## Nederlands

Vanta werkt volledig zonder account. Alleen wie zelf **Inloggen met Discord** kiest, deelt meldingen met de community.

**Wat we opslaan als je inlogt**
- Je Discord-gebruikers-ID, weergavenaam en avatar-ID (om je melding aan jou te koppelen en misbruik te kunnen blokkeren).
  Geen e-mailadres: Vanta vraagt alleen de Discord-scope `identify`.
- Je meldingen: game, cheat, gameversie (een technische vingerafdruk van het game-bestand), Vanta-versie,
  "werkt" of "werkt niet", je optionele opmerking (max. 300 tekens) en de datum.
- Een sessie: alleen de SHA-256-hash van je token, 180 dagen geldig. Op je pc staat de token versleuteld met Windows
  DPAPI in `%LOCALAPPDATA%\Vanta\account.bin` (alleen jouw Windows-account kan hem lezen).

**Wat we niet opslaan**
- Geen Discord-toegangstoken: die wordt na het ophalen van je profiel direct ingetrokken.
- Geen IP-adressen. Voor rate-limiting wordt kortstondig een versleutelde hash (HMAC met een dagelijks wisselende
  sleutel) gebruikt, die binnen enkele uren wordt verwijderd.
- Geen gegevens over je pc, andere programma's of je games buiten de gemelde cheat.

**Wie ziet wat**
- Iedereen (ook zonder account): per cheat en gameversie alleen de aantallen ("12 gebruikers melden: werkt niet").
- De beheerders van het Discord-kanaal: je melding, je opmerking en je Discord-naam.

**Anoniem gebruik delen** (standaard uit): telt per dag hoe vaak elke cheat is aangezet. Zonder account, naam of
IP-adres; alleen dagtotalen per game en cheat. Uitzetten kan altijd in Instellingen.

**Verwijderen**: *Instellingen → Account → Account verwijderen* verwijdert direct je account, al je meldingen en
sessies van de server (niet geanonimiseerd, maar gewist). Uitloggen verwijdert de token van je pc. Losse meldingen
trek je in met *Niet getest* of *Standaard* in het rechtsklikmenu.

**Waar**: de gegevens staan in een Cloudflare D1-database; de Discord-bot draait bij een hostingpartij en slaat zelf
geen meldingen op.

## English

Vanta works entirely without an account. Only if you choose **Sign in with Discord** do you share reports with the community.

**Stored when you sign in**
- Your Discord user ID, display name and avatar ID (to link reports to you and block abuse). No e-mail address:
  Vanta only requests the Discord `identify` scope.
- Your reports: game, cheat, game version (a technical fingerprint of the game file), Vanta version, "works" or
  "broken", your optional comment (max. 300 characters) and the date.
- A session: only the SHA-256 hash of your token, valid for 180 days. On your PC the token is encrypted with Windows
  DPAPI in `%LOCALAPPDATA%\Vanta\account.bin` (readable only by your Windows account).

**Not stored**
- No Discord access token: it is revoked right after your profile is fetched.
- No IP addresses. Rate limiting uses a short-lived keyed hash (HMAC with a daily rotating key), deleted within a few hours.
- Nothing about your PC, other programs or your games beyond the reported cheat.

**Who sees what**
- Everyone (also without an account): only counts per cheat and game version ("12 users report: broken").
- The maintainers of the Discord channel: your report, your comment and your Discord name.

**Share anonymous usage** (off by default): counts per day how often each cheat is enabled. No account, name or IP
address; daily totals per game and cheat only. You can turn it off in Settings at any time.

**Deletion**: *Settings → Account → Delete account* immediately deletes your account, all your reports and sessions
from the server (deleted, not anonymised). Signing out removes the token from your PC. Withdraw a single report with
*Not tested* or *Default* in the right-click menu.

**Where**: the data lives in a Cloudflare D1 database; the Discord bot runs at a hosting provider and does not store reports itself.
