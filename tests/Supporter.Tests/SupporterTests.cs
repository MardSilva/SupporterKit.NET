using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Supporter.Client;
using Supporter.Core;
using Supporter.Infrastructure;

namespace Supporter.Tests;

public sealed class SupporterTests
{
    [Fact]
    public void SupportDurationUsesCalendarMonthsAndYears()
    {
        var now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        Assert.Equal("Apoiante", SupportDuration.Format(null, now));
        Assert.Equal("Apoiante", SupportDuration.Format(now.AddDays(1), now));
        Assert.Equal("Novo apoiante", SupportDuration.Format(now.AddDays(-6), now));
        Assert.Equal("Apoiando há 7 dias", SupportDuration.Format(now.AddDays(-7), now));
        Assert.Equal("Apoiando há 1 mês", SupportDuration.Format(now.AddMonths(-1), now));
        Assert.Equal("Apoiando há 1 mês e 5 dias", SupportDuration.Format(now.AddMonths(-1).AddDays(-5), now));
        Assert.Equal("Apoiando há 2 meses e 1 dia", SupportDuration.Format(now.AddMonths(-2).AddDays(-1), now));
        Assert.Equal("Apoiando há 1 ano", SupportDuration.Format(now.AddYears(-1), now));
        Assert.Equal("Apoiando há 1 ano e 2 meses", SupportDuration.Format(now.AddYears(-1).AddMonths(-2), now));
        Assert.Equal("Apoiando há 2 anos", SupportDuration.Format(now.AddYears(-2), now));
        Assert.Equal("Apoiando há 1 mês", SupportDuration.Format(
            DateTimeOffset.Parse("2025-01-31T00:00:00Z"), DateTimeOffset.Parse("2025-02-28T00:00:00Z")));
        Assert.Equal("Apoiando há 1 ano", SupportDuration.Format(
            DateTimeOffset.Parse("2024-02-29T00:00:00Z"), DateTimeOffset.Parse("2025-02-28T00:00:00Z")));
        Assert.Equal("Apoiando há 7 dias", SupportDuration.Format(
            DateTimeOffset.Parse("2026-10-03T23:30:00-03:00"), DateTimeOffset.Parse("2026-10-11T02:30:00Z")));
    }

    [Theory]
    [InlineData("active_patron", "Paid", 500, 500, "fake-campaign", true, true)]
    [InlineData("former_patron", "Paid", 500, 0, "fake-campaign", true, false)]
    [InlineData("former_patron", "Paid", 500, 500, "fake-campaign", true, true)]
    [InlineData("declined_patron", "Declined", 500, 500, "fake-campaign", true, false)]
    [InlineData(null, null, 0, 0, "fake-campaign", false, false)]
    [InlineData("active_patron", "Paid", 500, 500, "other", false, false)]
    public void RecognitionKeepsHistoryAndUsesOnlyConfirmedCampaignBenefits(string? status, string? charge,
        long lifetime, long amount, string campaign, bool historical, bool entitled)
    {
        var tiers = Enumerable.Range(1, 12).ToDictionary(i => "tier-" + i, i => new TierRule("flow", ["benefit-" + i]));
        var member = new MemberSnapshot("user", null, campaign, "member", status, charge, lifetime, amount, ["tier-12", "unmapped"]);
        var result = RecognitionRules.Evaluate(member, "fake-campaign", "flow", tiers);
        Assert.Equal(historical, result.HasSupported);
        Assert.Equal(entitled ? ["benefit-12"] : Array.Empty<string>(), result.BenefitCodes);
        Assert.Empty(RecognitionRules.Evaluate(member, "fake-campaign", "unknown", tiers).BenefitCodes);
        Assert.True(RecognitionRules.Evaluate(null, "fake-campaign", "flow", tiers, true).HasSupported);
    }

    [Fact]
    public async Task BrowserFlowProtectsKeysAndPublicConsent()
    {
        await using var factory = new Factory();
        using var browser = factory.Browser();
        var csrf = await LoginAsync(browser);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/api/v1/supporter/keys", null)).StatusCode);
        var key = await IssueAsync(browser, csrf);
        var sdk = new SupporterClient(browser);
        var me = await sdk.GetMeAsync(key, "flow");
        Assert.True(me.HasSupported);
        Assert.Contains("supporter-a", me.BenefitCodes);
        Assert.False(me.IsStale);
        Assert.False(me.PublicRecognitionEnabled);
        Assert.Equal("Apoiante anônimo", me.RecognitionDisplayName);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00Z"), me.CurrentSupportStartedAt);
        Assert.Empty((await sdk.ListPublicAsync()).Items);
        await sdk.UpdatePublicProfileAsync(key, new(true, "Pseudônimo fictício"));
        var visible = await sdk.GetMeAsync(key, "flow");
        Assert.True(visible.PublicRecognitionEnabled);
        Assert.Equal("Pseudônimo fictício", visible.RecognitionDisplayName);
        Assert.Equal(me.BenefitCodes, visible.BenefitCodes);
        var page = await sdk.ListPublicAsync();
        Assert.Equal("Pseudônimo fictício", Assert.Single(page.Items).DisplayName);
        var publicJson = await browser.GetStringAsync("/api/v1/supporters");
        Assert.DoesNotContain("fake-user", publicJson);
        Assert.DoesNotContain(key, publicJson);
        await sdk.UpdatePublicProfileAsync(key, new(false, null));
        var anonymous = await sdk.GetMeAsync(key, "flow");
        Assert.False(anonymous.PublicRecognitionEnabled);
        Assert.Equal("Apoiante anônimo", anonymous.RecognitionDisplayName);
        Assert.Equal(me.BenefitCodes, anonymous.BenefitCodes);
        Assert.Equal(me.CurrentSupportStartedAt, anonymous.CurrentSupportStartedAt);
        Assert.Empty((await sdk.ListPublicAsync()).Items);
        var replacement = await IssueAsync(browser, csrf);
        using var revoked = new HttpRequestMessage(HttpMethod.Get, "/api/v1/supporter/me?productId=flow");
        revoked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(revoked)).StatusCode);
        Assert.Equal(me.Id, (await sdk.GetMeAsync(replacement, "flow")).Id);
        using var unknown = new HttpRequestMessage(HttpMethod.Get, "/api/v1/supporter/me?productId=arbitrary");
        unknown.Headers.Authorization = new AuthenticationHeaderValue("Bearer", replacement);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(unknown)).StatusCode);
        using var change = new HttpRequestMessage(HttpMethod.Post, "/dev/membership") { Content = JsonContent.Create(new { profileId = "former" }) };
        change.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(change)).StatusCode);
        var former = await sdk.GetMeAsync(replacement, "flow");
        Assert.Null(former.CurrentSupportStartedAt);
        Assert.Equal("Apoiante", former.SupportDurationLabel);
        Assert.True(former.HasSupported);
        Assert.Empty(former.BenefitCodes);
        using var resume = new HttpRequestMessage(HttpMethod.Post, "/dev/membership") { Content = JsonContent.Create(new { profileId = "duration-new" }) };
        resume.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(resume)).StatusCode);
        var resumed = await sdk.GetMeAsync(replacement, "flow");
        Assert.True(resumed.HasSupported);
        Assert.Equal(DateTimeOffset.Parse("2026-10-09T00:00:00Z"), resumed.CurrentSupportStartedAt);
        Assert.NotEqual(me.CurrentSupportStartedAt, resumed.CurrentSupportStartedAt);
        using var missingDate = new HttpRequestMessage(HttpMethod.Post, "/dev/membership") { Content = JsonContent.Create(new { profileId = "anonymous" }) };
        missingDate.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(missingDate)).StatusCode);
        var withoutDate = await sdk.GetMeAsync(replacement, "flow");
        Assert.Null(withoutDate.CurrentSupportStartedAt);
        Assert.Equal("Apoiante", withoutDate.SupportDurationLabel);
        Assert.NotEmpty(withoutDate.BenefitCodes);
        Assert.True((await browser.GetAsync("/manage")).Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OAuthStateIsBoundToBrowserSingleUseAndExpiring()
    {
        await using var factory = new Factory(realOAuth: true);
        using var browser = factory.Browser();
        var start = await browser.GetAsync("/auth/patreon/start");
        var match = Regex.Match(start.Headers.Location!.Query, "state=([^&]+)");
        var state = match.Groups[1].Value;
        var callback = "/auth/patreon/callback?code=test-code&state=" + state;
        using var other = factory.Browser();
        Assert.Equal(HttpStatusCode.BadRequest, (await other.GetAsync(callback)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync(callback)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(callback)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/manage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/dev")).StatusCode);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SupporterService>();
        var expired = await service.StartOAuthAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<SupporterDb>();
        await db.OAuthAttempts.Where(a => a.StateHash == SupporterService.Hash(expired.State))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(0, await service.ConsumeOAuthAsync(expired.State, expired.Session, default));
    }

    [Fact]
    public async Task PatreonHttpReadsAllPagesMaskedNamesAndRefreshForm()
    {
        var handler = new ScriptedHandler(
            File.ReadAllText(Path.Combine(Factory.RepoRoot, "fixtures/patreon-page-1.json")),
            File.ReadAllText(Path.Combine(Factory.RepoRoot, "fixtures/patreon-page-2.json")),
            File.ReadAllText(Path.Combine(Factory.RepoRoot, "fixtures/patreon-identity.json")),
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.patreon.com") };
        var options = new SupporterOptions { CampaignId = "fake-campaign", Patreon = new() { ClientId = "client", ClientSecret = "secret&value" } };
        var provider = new PatreonProvider(http, options, TimeProvider.System);
        var members = await provider.GetCampaignMembersAsync("creator", default);
        Assert.Equal(2, members.Count);
        Assert.Null(members[0].DisplayName);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T00:00:00Z"), members[0].SupportStartedAt);
        Assert.Null(members[1].SupportStartedAt);
        Assert.Contains("pledge_relationship_start", handler.Requests[0].Uri);
        Assert.Contains("page%5Bcursor%5D=cursor%26opaque", handler.Requests[1].Uri);
        var identity = await provider.GetIdentityAsync("user", default);
        Assert.Null(identity.DisplayName);
        Assert.Equal("member-1", identity.Membership!.MemberId);
        Assert.Null(identity.Membership.SupportStartedAt);
        var refreshed = await provider.RefreshAsync("refresh", default);
        Assert.Equal("new-refresh", refreshed.RefreshToken);
        Assert.Equal("/api/oauth2/token", new Uri(handler.Requests[3].Uri).AbsolutePath);
        Assert.Contains("client_secret=secret%26value", handler.Requests[3].Body);
        Assert.DoesNotContain("secret", handler.Requests[3].Uri);
        using var limitedHttp = new HttpClient(new RateLimitedHandler()) { BaseAddress = http.BaseAddress };
        var limited = await Assert.ThrowsAsync<ProviderException>(() =>
            new PatreonProvider(limitedHttp, options, TimeProvider.System).GetCampaignMembersAsync("creator", default));
        Assert.Equal("http_429", limited.Code);
        Assert.True(limited.RetryAt > DateTimeOffset.UtcNow.AddMinutes(50));
        var failure = new ScriptedHandler(File.ReadAllText(Path.Combine(Factory.RepoRoot, "fixtures/patreon-page-1.json")), """{"data":[]}""");
        using var failureHttp = new HttpClient(failure) { BaseAddress = http.BaseAddress };
        await Assert.ThrowsAnyAsync<Exception>(() => new PatreonProvider(failureHttp, options, TimeProvider.System).GetCampaignMembersAsync("creator", default));
    }

    [Fact]
    public async Task SQLiteTokensAndWebhookInboxSurviveRestartAndFailurePreservesSnapshot()
    {
        var factory = new Factory();
        Directory.CreateDirectory(factory.DataPath);
        using (var rsa = RSA.Create(2048))
        {
            var certificateRequest = new CertificateRequest("CN=SupporterKit Tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
            File.WriteAllBytes(Path.Combine(factory.DataPath, "keyring.pfx"), certificate.Export(X509ContentType.Pfx, "test-key-password"));
        }
        string key;
        Guid id;
        var webhook = Encoding.UTF8.GetBytes("""{ "data": { "type":"member", "id":"event-member", "relationships":{"campaign":{"data":{"id":"fake-campaign","type":"campaign"}}} } }""");
        var signature = Convert.ToHexString(HMACMD5.HashData(Encoding.UTF8.GetBytes("test-webhook-secret"), webhook));
        using (var browser = factory.Browser())
        {
            var csrf = await LoginAsync(browser);
            key = await IssueAsync(browser, csrf);
            id = (await new SupporterClient(browser).GetMeAsync(key, "flow")).Id;
            async Task<HttpResponseMessage> WebhookAsync(byte[] body, string sig)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/patreon") { Content = new ByteArrayContent(body) };
                request.Headers.Add("X-Patreon-Event", "members:update");
                request.Headers.Add("X-Patreon-Signature", sig);
                return await browser.SendAsync(request);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await WebhookAsync(webhook, new string('0', 32))).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await WebhookAsync(webhook, signature)).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await WebhookAsync(webhook, signature)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await WebhookAsync(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(webhook).Replace("{ ", "{")), signature)).StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SupporterDb>();
            Assert.Equal(1, await db.Inbox.CountAsync());
            Assert.Null((await db.Inbox.SingleAsync()).CompletedAt);
            var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
            await tokens.SaveAsync("test", new("expired", "active-a", DateTimeOffset.UtcNow.AddHours(-1)), default);
            Assert.Equal("active-a", await tokens.GetAccessTokenAsync("test", false, default));
            Assert.DoesNotContain("active-a", (await db.Tokens.SingleAsync()).ProtectedTokens);
            await tokens.SaveAsync("failure", new("old", "fail", DateTimeOffset.UtcNow.AddHours(-1)), default);
            await Assert.ThrowsAsync<ProviderException>(() => tokens.GetAccessTokenAsync("failure", false, default));
            factory.Services.GetRequiredService<FakeProvider>().Unavailable = true;
            await Assert.ThrowsAsync<ProviderException>(() => scope.ServiceProvider.GetRequiredService<CampaignSynchronizer>().RunAsync(default));
            await db.People.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSuccessfulSyncAt, DateTimeOffset.UtcNow.AddHours(-1)));
            var status = await scope.ServiceProvider.GetRequiredService<SupporterService>().GetStatusAsync(id, "flow", default);
            Assert.True(status!.IsStale);
            Assert.True(status.HasSupported);
            Assert.Contains("supporter-a", status.BenefitCodes);
        }
        var dataPath = factory.DataPath;
        await factory.DisposeAsync();
        await using var restarted = new Factory(dataPath, worker: true);
        using var secondBrowser = restarted.Browser();
        Assert.Equal(id, (await new SupporterClient(secondBrowser).GetMeAsync(key, "flow")).Id);
        using var restartScope = restarted.Services.CreateScope();
        Assert.Equal("active-a", await restartScope.ServiceProvider.GetRequiredService<TokenService>().GetAccessTokenAsync("test", false, default));
        var restartDb = restartScope.ServiceProvider.GetRequiredService<SupporterDb>();
        for (var i = 0; i < 60; i++)
        {
            if (await restartDb.Inbox.AsNoTracking().AnyAsync(e => e.CompletedAt != null)) break;
            await Task.Delay(100);
        }
        Assert.True(await restartDb.Inbox.AsNoTracking().AnyAsync(e => e.CompletedAt != null));
    }

    [Fact]
    public void ProductionRejectsSimulation()
    {
        var options = new SupporterOptions { Provider = "Fake", SimulationEnabled = true };
        Assert.Throws<InvalidOperationException>(() => options.Validate(new TestEnvironment()));
        using var factory = new Factory(environment: "Production");
        Assert.Throws<InvalidOperationException>(() => factory.Browser());
        options.Provider = "Real";
        Assert.Throws<InvalidOperationException>(() => options.Validate(new TestEnvironment()));
    }

    private static async Task<string> LoginAsync(HttpClient browser)
    {
        var page = await browser.GetStringAsync("/dev");
        var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
        var response = await browser.PostAsync("/dev/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["profileId"] = "active-a"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var manage = await browser.GetStringAsync("/manage");
        return WebUtility.HtmlDecode(Regex.Match(manage, "data-token=\"([^\"]+)\"").Groups[1].Value);
    }
    private static async Task<string> IssueAsync(HttpClient browser, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/supporter/keys");
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IssuedKey>())!.Key;
    }
    private sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> queue = new(responses);
        public List<(string Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK) { Content = new StringContent(queue.Dequeue(), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RateLimitedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"errors\":[{\"retry_after_seconds\":3600}]}", Encoding.UTF8, "application/json")
            });
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Factory.RepoRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
