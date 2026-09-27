namespace Vanta.Core;

/// <summary>All product naming in one place (UI gets it via the host library; see docs/BRANDING.md).</summary>
public static class Branding
{
    public const string Name = "Vanta";
    public const string Tagline = "Single-player trainer";
    public const string DataFolder = "Vanta";          // %LOCALAPPDATA%\Vanta
    public const string UiHost = "vanta.example";          // virtual origin for the embedded UI (https://vanta.example/)
    public const string MutexName = "Local\\Vanta.SingleInstance";
    /// <summary>Community backend (Cloudflare Worker), e.g. "https://vanta-api.example.workers.dev". Empty = accounts/reports off.
    /// Can be overridden per PC with "backendUrl" in settings.json or the VANTA_BACKEND_URL environment variable.</summary>
    public const string BackendUrl = "";
    public static string Version => typeof(Branding).Assembly.GetName().Version?.ToString(3) ?? "0.2.2";
}
