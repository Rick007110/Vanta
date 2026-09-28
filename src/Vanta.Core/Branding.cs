namespace Vanta.Core;

/// <summary>All product naming in one place (UI gets it via the host library; see docs/BRANDING.md).</summary>
public static class Branding
{
    public const string Name = "Vanta";
    public const string Tagline = "Single-player trainer";
    public const string DataFolder = "Vanta";          // %LOCALAPPDATA%\Vanta
    public const string UiHost = "vanta.example";          // virtual origin for the embedded UI (https://vanta.example/)
    public const string MutexName = "Local\\Vanta.SingleInstance";
    /// <summary>Community reports (Supabase): project URL, e.g. "https://abcd1234.supabase.co". Empty = accounts/reports off.
    /// Can be overridden per PC with "supabaseUrl" in settings.json or the VANTA_SUPABASE_URL environment variable.</summary>
    public const string SupabaseUrl = "https://zwglyogpkcynnopafvsb.supabase.co";
    /// <summary>The project's public publishable key (sb_publishable_...) or legacy anon key. Never the secret/service_role key
    /// (Vanta refuses those). Override: "supabaseKey" in settings.json or VANTA_SUPABASE_KEY.</summary>
    public const string SupabaseKey = "sb_publishable_x1Kh8U1AbRkcjKMRYG5wxQ_hj2Tr0q3";
    public static string Version => typeof(Branding).Assembly.GetName().Version?.ToString(3) ?? "0.3.1";
}
