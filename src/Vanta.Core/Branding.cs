namespace Vanta.Core;

/// <summary>All product naming in one place (UI gets it via the host library; see docs/BRANDING.md).</summary>
public static class Branding
{
    public const string Name = "Vanta";
    public const string Tagline = "Single-player trainer";
    public const string DataFolder = "Vanta";          // %LOCALAPPDATA%\Vanta
    public const string UiHost = "vanta.example";          // virtual origin for the embedded UI (https://vanta.example/)
    public const string MutexName = "Local\\Vanta.SingleInstance";
    public static string Version => typeof(Branding).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";
}
