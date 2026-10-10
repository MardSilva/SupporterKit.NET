using System.Collections.Concurrent;
using System.Text.Json;
using Supporter.Core;
using Microsoft.Extensions.Hosting;

namespace Supporter.Infrastructure;

public sealed record FakeProfile(string Id, string Label, ProviderIdentity Identity,
    string? TierName = null, int? MonthlyAmountCents = null);

public sealed class FakeProvider : ISupporterProvider
{
    private readonly ConcurrentDictionary<string, FakeProfile> profiles;
    private readonly SupporterOptions options;
    private readonly TimeProvider time;
    public bool Unavailable { get; set; }

    public FakeProvider(SupporterOptions options, TimeProvider time, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Fake is forbidden outside Development/Testing.");
        this.options = options;
        this.time = time;
        var fixtures = JsonSerializer.Deserialize<FakeProfile[]>(File.ReadAllText(options.FixturesPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Empty fixtures.");
        profiles = new(fixtures.ToDictionary(p => p.Id));
    }
    public FakeProfile[] Profiles => profiles.Values.OrderBy(p => p.Id).ToArray();
    public void SetMembership(string id, MemberSnapshot? member)
    {
        if (!profiles.TryGetValue(id, out var profile)) throw new ArgumentException("Unknown profile.");
        if (member is not null && member.UserId != profile.Identity.UserId) throw new ArgumentException("Identity mismatch.");
        profiles[id] = profile with { Identity = profile.Identity with { Membership = member } };
    }
    public Task<ProviderIdentity> GetIdentityAsync(string accessToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Unavailable) throw new ProviderException("fake_unavailable");
        if (!profiles.TryGetValue(accessToken, out var profile)) throw new ProviderException("unauthorized");
        return Task.FromResult(profile.Identity);
    }
    public Task<IReadOnlyList<MemberSnapshot>> GetCampaignMembersAsync(string accessToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Unavailable) throw new ProviderException("fake_unavailable");
        return Task.FromResult<IReadOnlyList<MemberSnapshot>>(profiles.Values.Select(p => p.Identity.Membership)
            .OfType<MemberSnapshot>().Where(m => m.CampaignId == options.CampaignId).ToArray());
    }
    public Task<OAuthTokens> ExchangeCodeAsync(string code, CancellationToken ct) =>
        throw new NotSupportedException("Simulation does not implement OAuth.");
    public Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (refreshToken == "fail") throw new ProviderException("token_400");
        return Task.FromResult(new OAuthTokens(refreshToken, refreshToken, time.GetUtcNow().AddHours(1)));
    }
}
