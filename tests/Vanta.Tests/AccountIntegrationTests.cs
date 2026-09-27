using System.Net;
using Vanta.Core;
using Vanta.Core.Account;
using Xunit;

namespace Vanta.Tests;

/// <summary>
/// Against the local Supabase-like stack (Postgres + the Vanta migrations + PostgREST + mock Supabase Auth):
/// sh supabase/tests/local-stack.sh up, then
/// VANTA_IT_SUPABASE_URL=http://127.0.0.1:54321 dotnet test --filter AccountIntegration
/// </summary>
public class AccountIntegrationTests
{
    private static readonly string? Backend = Environment.GetEnvironmentVariable("VANTA_IT_SUPABASE_URL");
    private static readonly string Key = Environment.GetEnvironmentVariable("VANTA_IT_SUPABASE_KEY") ?? "sb_publishable_localtest";

    /// <summary>Plays the system browser: follows the redirects Supabase Auth -> (Discord) -> Vanta's loopback listener.</summary>
    public static Func<string, Task> Browser(List<string>? hops = null) => url =>
    {
        _ = Task.Run(async () =>
        {
            using var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var next = url;
            for (int i = 0; i < 6; i++)
            {
                hops?.Add(next);
                using var r = await h.GetAsync(next);
                if (r.StatusCode != HttpStatusCode.Found || r.Headers.Location == null) break;
                next = r.Headers.Location.IsAbsoluteUri ? r.Headers.Location.ToString() : new Uri(new Uri(next), r.Headers.Location).ToString();
            }
        });
        return Task.CompletedTask;
    };

    [SkippableFact]
    public async Task Full_flow_against_local_supabase()
    {
        Skip.If(string.IsNullOrEmpty(Backend), "VANTA_IT_SUPABASE_URL not set (supabase/tests/local-stack.sh up)");
        var hops = new List<string>();
        var store = new MemoryTokenStore();
        var sent = new List<object>();
        var svc = new AccountService(new Settings { ShareUsage = true }, store, null, Browser(hops), o => { lock (sent) sent.Add(o); }, new SupabaseConfig(new Uri(Backend!), Key), startTimer: false);
        await svc.LoginAsync();
        Assert.NotNull(svc.Session);
        Assert.Contains(hops, u => u.Contains("/auth/v1/authorize?provider=discord"));
        Assert.Contains(hops, u => u.StartsWith("http://127.0.0.1:") && u.Contains("/callback?state=") && u.Contains("&code="));
        Assert.NotNull(svc.Session.RefreshToken);
        var game = "it-dotnet-" + Guid.NewGuid().ToString("N")[..6];
        var item = new ReportItem(game, "godmode", "fileVersion=1.0.0", "broken", "valt door de vloer", Branding.Version, "1.0", "IT Game", "God mode");
        var r = await svc.ReportAsync(item);
        Assert.True(r.ok, r.error);
        Assert.Equal(1, r.community!.Broken);
        var comm = await svc.CommunityAsync(game, "fileVersion=1.0.0", force: true);
        Assert.Equal(1, comm!["godmode"].Broken);
        Assert.True((await svc.ReportAsync(item, withdraw: true)).ok);
        Assert.Equal(0, (await svc.CommunityAsync(game, "fileVersion=1.0.0", force: true))!["godmode"].Broken);
        svc.CountUsage(game, "godmode");
        await svc.FlushUsageAsync();
        var me = await svc.Client!.MeAsync(svc.Session!.Token);
        Assert.Equal(svc.Session.User.Id, me.Id);
        var before = svc.Session;
        Assert.False(string.IsNullOrEmpty(await svc.TokenAsync(force: true)));   // refresh token rotation
        Assert.NotEqual(before.RefreshToken, store.Load()!.RefreshToken);
        await Assert.ThrowsAsync<AccountException>(() => svc.Client.RefreshAsync(before.RefreshToken!));   // old refresh token is spent
        var old = svc.Session!.Token;
        Assert.True(await svc.DeleteAsync());
        Assert.Null(store.Load());
        var e = await Assert.ThrowsAsync<AccountException>(() => svc.Client.MeAsync(old));
        Assert.Equal("user_not_found", e.Code);
        var e2 = await Assert.ThrowsAsync<AccountException>(() => svc.Client.MeAsync("eyJ.bad.token"));
        Assert.Equal(401, e2.Status);
    }
}
