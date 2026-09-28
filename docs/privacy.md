# Privacy — community reports

*Nederlands hieronder.*

## English

Vanta works entirely without an account. Only if you choose **Sign in with Discord** do you share reports with the community.

**Who processes the data**: the Vanta maintainer (Rick007110). Storage and sign-in run on
[Supabase](https://supabase.com/privacy) (database and Supabase Auth) as a processor, preferably in an EU region.
The Discord bot runs at a hosting provider and does not store reports itself.

**Stored when you sign in**
- In the Vanta tables: your Discord user ID, display name and avatar URL (to link reports to you and block abuse).
  No e-mail address.
- In Supabase Auth (the sign-in system): a user with the data Discord provides, including your e-mail address if
  Discord has one. Supabase always requests the Discord `email` scope; this cannot be turned off. Vanta never uses or
  shows that address. Like any sign-in system, Supabase Auth also keeps sign-in times and, per session, the IP
  address and browser (for security).
- Your reports: game, cheat, game version (a technical fingerprint of the game file), Vanta version, "works" or
  "broken", your optional comment (max. 300 characters) and the date.
- On your PC: your session (a one-hour access token and a refresh token), encrypted with Windows DPAPI in
  `%LOCALAPPDATA%\Vanta\account.bin` (readable only by your Windows account). Signing out invalidates the session.

**Not stored**
- No Discord access token: Vanta only reads your profile through Supabase and does not keep the Discord token.
- No IP addresses in the Vanta tables. Rate limiting uses a short-lived hash (SHA-256 with a secret, daily rotating
  salt), deleted within a few hours.
- Nothing about your PC, other programs or your games beyond the reported cheat.

**Who sees what**
- Everyone (also without an account): only counts per cheat and game version ("12 users report: broken").
- Signed-in users only see their own reports; the database enforces this (row level security).
- The maintainers of the Discord channel: your report, your comment and your Discord name.

**Share anonymous usage** (off by default): counts per day how often each cheat is enabled. No account, name or IP
address; daily totals per game and cheat only. You can turn it off in Settings at any time.

**Deletion**: *Settings → Account → Delete account* immediately deletes your Supabase account (including the e-mail
address and sessions in Supabase Auth), your profile and all your reports (deleted, not anonymised). Signing out
removes the session from your PC. Withdraw a single report with *Not tested* or *Default* in the right-click menu.
Supabase may keep technical logs and backups for a limited time under its own retention periods.

## Nederlands

Vanta werkt volledig zonder account. Alleen wie zelf **Inloggen met Discord** kiest, deelt meldingen met de community.

**Wie verwerkt de gegevens**: de beheerder van Vanta (Rick007110). Opslag en inloggen gebeuren bij
[Supabase](https://supabase.com/privacy) (database en Supabase Auth), als verwerker; het project draait bij voorkeur in
een EU-regio. De Discord-bot draait bij een hostingpartij en slaat zelf geen meldingen op.

**Wat er wordt opgeslagen als je inlogt**
- In de Vanta-tabellen: je Discord-gebruikers-ID, weergavenaam en avatar-URL (om je melding aan jou te koppelen en
  misbruik te kunnen blokkeren). Geen e-mailadres.
- In Supabase Auth (het inlogsysteem): een gebruiker met de gegevens die Discord meegeeft, waaronder je
  e-mailadres als Discord dat heeft. Supabase vraagt Discord altijd om de scope `email`; dat is niet uit te zetten.
  Vanta gebruikt of toont dat e-mailadres nergens. Supabase Auth houdt daarnaast, zoals elk inlogsysteem, tijdstippen
  van inloggen en per sessie het IP-adres en de browser bij (voor beveiliging).
- Je meldingen: game, cheat, gameversie (een technische vingerafdruk van het game-bestand), Vanta-versie,
  "werkt" of "werkt niet", je optionele opmerking (max. 300 tekens) en de datum.
- Op je pc: je sessie (een toegangstoken van een uur en een vernieuwingstoken), versleuteld met Windows DPAPI in
  `%LOCALAPPDATA%\Vanta\account.bin` (alleen jouw Windows-account kan hem lezen). Uitloggen maakt de sessie ongeldig.

**Wat er niet wordt opgeslagen**
- Geen Discord-toegangstoken: Vanta vraagt alleen je profiel op via Supabase en bewaart het Discord-token niet.
- Geen IP-adressen in de Vanta-tabellen. Voor rate-limiting wordt kortstondig een hash gebruikt (SHA-256 met een
  geheime, dagelijks wisselende toevoeging), die binnen enkele uren wordt verwijderd.
- Geen gegevens over je pc, andere programma's of je games buiten de gemelde cheat.

**Wie ziet wat**
- Iedereen (ook zonder account): per cheat en gameversie alleen de aantallen ("12 gebruikers melden: werkt niet").
- Ingelogde gebruikers zien alleen hun eigen meldingen; de database dwingt dat af (row level security).
- De beheerders van het Discord-kanaal: je melding, je opmerking en je Discord-naam.

**Anoniem gebruik delen** (standaard uit): telt per dag hoe vaak elke cheat is aangezet. Zonder account, naam of
IP-adres; alleen dagtotalen per game en cheat. Uitzetten kan altijd in Instellingen.

**Verwijderen**: *Instellingen → Account → Account verwijderen* verwijdert direct je Supabase-account (inclusief het
e-mailadres en de sessies in Supabase Auth), je profiel en al je meldingen (gewist, niet geanonimiseerd). Uitloggen
verwijdert de sessie van je pc. Losse meldingen trek je in met *Niet getest* of *Standaard* in het rechtsklikmenu.
Supabase kan technische logboeken en back-ups nog een beperkte tijd bewaren volgens hun eigen bewaartermijnen.
