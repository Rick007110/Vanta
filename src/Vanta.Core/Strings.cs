namespace Vanta.Core;

/// <summary>Host-side user-facing texts (nl default, en). UI texts live in ui/shared/i18n.js.</summary>
public static class Strings
{
    public static string Lang { get; set; } = "en";

    private static readonly Dictionary<string, (string nl, string en)> T = new()
    {
        ["aob.none"] = ("{0}: geen unieke AOB gevonden ({1}). Niets gepatcht.", "{0}: no unique AOB found ({1}). Nothing patched."),
        ["aob.hooked"] = ("{0}: staat al aan in het spel (achtergebleven van een eerdere Vanta-sessie). Niets gepatcht. Herstart het spel om de cheat weer te kunnen gebruiken.", "{0}: is already active in the game (left over from an earlier Vanta session). Nothing patched. Restart the game to use the cheat again."),
        ["aob.report"] = ("patroon {0}: {1} treffer(s)", "pattern {0}: {1} hit(s)"),
        ["module.missing"] = ("Module {0} niet gevonden in het proces.", "Module {0} not found in the process."),
        ["read.fail"] = ("Kan geheugen niet lezen op {0:X}. Niets gepatcht.", "Cannot read memory at {0:X}. Nothing patched."),
        ["write.fail"] = ("Schrijven naar {0:X} mislukt. Niets gepatcht.", "Writing to {0:X} failed. Nothing patched."),
        ["alloc.fail"] = ("Geen vrij geheugen binnen ±2 GB van de game gevonden. Niets gepatcht.", "No free memory within ±2 GB of the game. Nothing patched."),
        ["asm.fail"] = ("{0}: assembleren mislukt ({1}). Niets gepatcht.", "{0}: assembling failed ({1}). Nothing patched."),
        ["check.fail"] = ("{0}: onverwachte waarde op de gevonden plek ({1}). Niets gepatcht.", "{0}: unexpected value at the found location ({1}). Nothing patched."),
        ["requires.fail"] = ("Vereist '{0}': {1}", "Requires '{0}': {1}"),
        ["ptr.null"] = ("Nog niet beschikbaar", "Not available yet"),
        ["ptr.sym"] = ("Symbool {0} bestaat niet (hook niet actief).", "Symbol {0} does not exist (hook not active)."),
        ["not.attached"] = ("Niet gekoppeld aan de game.", "Not attached to the game."),
        ["cheat.unknown"] = ("Onbekende cheat '{0}'.", "Unknown cheat '{0}'."),
        ["blocked"] = ("{0} is online/anti-cheat: Vanta koppelt hier niet aan.", "{0} is online/anti-cheat protected: Vanta will not attach."),
        ["open.fail"] = ("Kan het proces niet openen ({0}). Start Vanta als administrator.", "Cannot open the process ({0}). Run Vanta as administrator."),
        ["enabled"] = ("{0} ingeschakeld", "{0} enabled"),
        ["disabled"] = ("{0} uitgeschakeld", "{0} disabled"),
        ["all.off"] = ("Alle cheats uitgeschakeld", "All cheats disabled"),
        ["attached"] = ("Gekoppeld aan {0} (PID {1}).", "Attached to {0} (PID {1})."),
        ["exited"] = ("{0} is afgesloten.", "{0} has exited."),
        ["launching"] = ("{0} wordt gestart via Steam…", "Starting {0} via Steam…"),
        ["launch.timeout"] = ("{0} niet gevonden binnen {1} s. Start de game en probeer opnieuw.", "{0} not found within {1} s. Start the game and try again."),
        ["launch.fail"] = ("Starten mislukt: {0}", "Launch failed: {0}"),
        ["version.unverified"] = ("Versie niet geverifieerd (vingerafdruk: {0}).", "Version not verified (fingerprint: {0})."),
        ["version.mismatch"] = ("Andere gameversie dan ondersteund ({0}). Cheats kunnen falen.", "Game version differs from the supported one ({0}). Cheats may fail."),
        ["reapplied"] = ("{0} cheat(s) opnieuw toegepast na herstart van de game.", "{0} cheat(s) re-applied after the game restarted."),
        ["restore.warn"] = ("{0}: herstellen gaf een waarschuwing ({1}).", "{0}: restore warning ({1})."),
        ["store.steamStub"] = ("Staat in je Steam-bibliotheek, maar de bestanden zijn niet via Steam geïnstalleerd (buildid {0}, SizeOnDisk {1}).", "In your Steam library, but the files are not installed through Steam (buildid {0}, SizeOnDisk {1})."),
        ["store.xboxProtected"] = ("Xbox/Game Pass-game: Windows kan geheugentoegang tot beschermde apps blokkeren.", "Xbox/Game Pass game: Windows may block memory access to protected apps."),
        ["conf.warn.broken"] = ("Werkt niet in deze versie.", "Does not work in this version."),
        ["cheat.broken"] = ("{0}: werkt niet in deze versie. Kies \"Toch proberen\" om het toch aan te zetten.", "{0}: does not work in this version. Choose \"Try anyway\" to enable it anyway."),
        ["status.saved"] = ("Teststatus opgeslagen: {0} = {1}", "Test status saved: {0} = {1}"),
        ["status.works"] = ("Werkt", "Works"),
        ["status.broken"] = ("Werkt niet", "Broken"),
        ["status.untested"] = ("Niet getest", "Not tested"),
        ["status.default"] = ("Standaard (uit game.json)", "Default (from game.json)"),
        ["account.page.ok"] = ("Je bent ingelogd bij Vanta. Je kunt dit tabblad sluiten.", "You are signed in to Vanta. You can close this tab."),
        ["account.page.failed"] = ("Inloggen is niet gelukt. Ga terug naar Vanta en probeer het opnieuw.", "Signing in failed. Go back to Vanta and try again."),
        ["account.page.badstate"] = ("Deze pagina hoort bij een oudere inlogpoging. Ga terug naar Vanta.", "This page belongs to an older sign-in attempt. Go back to Vanta."),
        ["account.loggedin"] = ("Ingelogd als {0}", "Signed in as {0}"),
        ["account.loggedout"] = ("Uitgelogd", "Signed out"),
        ["account.deleted"] = ("Account en meldingen verwijderd", "Account and reports deleted"),
        ["account.expired"] = ("Je sessie is verlopen; log opnieuw in om meldingen te versturen.", "Your session expired; sign in again to send reports."),
        ["report.sent"] = ("Melding verstuurd: {0}", "Report sent: {0}"),
        ["report.queued"] = ("Geen verbinding: melding wordt later verstuurd ({0})", "No connection: report will be sent later ({0})"),
        ["report.failed"] = ("Melding niet verstuurd: {0}", "Report not sent: {0}"),
        ["status.exported"] = ("Teststatus geëxporteerd: {0}", "Test status exported: {0}"),
        ["conf.warn.untested"] = ("Niet geverifieerd voor deze versie.", "Not verified for this version."),
        ["conf.warn.experimental"] = ("Experimenteel: kan de game laten crashen. Sla eerst op.", "Experimental: may crash the game. Save first."),
        ["store.none"] = ("Geen installatie gevonden (Steam, Ubisoft Connect, Epic, GOG, EA app, Xbox). Start de game zelf; Vanta koppelt automatisch.", "No installation found (Steam, Ubisoft Connect, Epic, GOG, EA app, Xbox). Start the game yourself; Vanta attaches automatically."),
        ["launching.via"] = ("{0} wordt gestart via {1}…", "Starting {0} via {1}…"),
        ["launch.direct"] = ("{0} kon niet starten ({1}); de game-exe wordt direct gestart.", "{0} could not start ({1}); starting the game exe directly."),
        ["open.fail.protected"] = ("Kan het proces niet openen ({0}). Xbox/Game Pass-games zijn door Windows beschermd; Vanta kan hier niet in schrijven.", "Cannot open the process ({0}). Xbox/Game Pass games are protected by Windows; Vanta cannot write to them."),
        ["anticheat.files"] = ("Anti-cheat gevonden in deze installatie ({0}). Vanta koppelt niet.", "Anti-cheat found in this installation ({0}). Vanta will not attach."),
        ["anticheat.module"] = ("Anti-cheat actief in het proces ({0}). Vanta koppelt niet.", "Anti-cheat running in the process ({0}). Vanta will not attach."),
        ["value.set"] = ("{0} = {1}", "{0} = {1}"),
        ["app.startFail"] = ("Vanta kon niet starten:\n\n{0}\n\nLog: {1}", "Vanta could not start:\n\n{0}\n\nLog: {1}"),
        ["app.helperFail"] = ("De update-helper kon niet starten.", "The update helper could not start."),
        ["webview2.missing"] = ("Vanta heeft de Microsoft Edge WebView2 Runtime nodig om de interface te tonen.\n\nOp Windows 11 is die normaal al geïnstalleerd. Op jouw pc ontbreekt hij (of is hij beschadigd).\n\nKlik op OK om de gratis download van Microsoft te openen:\n{0}\n\nInstalleer hem en start Vanta daarna opnieuw.",
            "Vanta needs the Microsoft Edge WebView2 Runtime to show its interface.\n\nIt is normally preinstalled on Windows 11, but it is missing (or damaged) on this PC.\n\nClick OK to open the free download from Microsoft:\n{0}\n\nInstall it and then start Vanta again."),
        ["webview2.missing.title"] = ("{0}: WebView2 ontbreekt", "{0}: WebView2 missing"),
        ["update.splash.title"] = ("Vanta wordt bijgewerkt…", "Updating Vanta…"),
        ["update.splash.sub"] = ("Versie {0} wordt geïnstalleerd. Vanta start daarna vanzelf opnieuw.", "Installing version {0}. Vanta restarts automatically afterwards."),
        ["update.noSha"] = ("De release heeft geen .sha256-bestand; update geweigerd.", "The release has no .sha256 file; update refused."),
        ["update.noHash"] = ("Het .sha256-bestand bevat geen hash voor {0}.", "The .sha256 file contains no hash for {0}."),
        ["update.shaMismatch"] = ("SHA-256 klopt niet (verwacht {0}…, gekregen {1}…); download verwijderd.", "SHA-256 mismatch (expected {0}…, got {1}…); download deleted."),
        ["update.noExe"] = ("Vanta.exe niet gevonden in het updatepakket.", "Vanta.exe not found in the update package."),
        ["update.pkgMismatch"] = ("SHA-256 van het updatepakket klopt niet; niets gewijzigd.", "SHA-256 of the update package does not match; nothing changed."),
        ["account.unavailable"] = ("account niet beschikbaar", "account not available"),
        ["cheat.notValue"] = ("{0}: geen waarde-cheat", "{0}: not a value cheat"),
        ["x86.unsupported"] = ("32-bit games worden niet ondersteund", "32-bit games are not supported"),
    };

    public static string Get(string key, params object?[] args)
    {
        if (!T.TryGetValue(key, out var v)) return key;
        var fmt = Lang == "nl" ? v.nl : v.en;
        return args.Length == 0 ? fmt : string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args);
    }
}
