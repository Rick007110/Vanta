namespace Vanta.Core;

/// <summary>Host-side user-facing texts (nl default, en). UI texts live in ui/shared/i18n.js.</summary>
public static class Strings
{
    public static string Lang { get; set; } = "nl";

    private static readonly Dictionary<string, (string nl, string en)> T = new()
    {
        ["aob.none"] = ("{0}: geen unieke AOB gevonden ({1}). Niets gepatcht.", "{0}: no unique AOB found ({1}). Nothing patched."),
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
    };

    public static string Get(string key, params object?[] args)
    {
        if (!T.TryGetValue(key, out var v)) return key;
        var fmt = Lang == "en" ? v.en : v.nl;
        return args.Length == 0 ? fmt : string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args);
    }
}
