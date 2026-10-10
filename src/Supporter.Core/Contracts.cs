namespace Supporter.Core;

public sealed record MemberSnapshot(string UserId, string? DisplayName, string CampaignId, string? MemberId,
    string? PatronStatus, string? LastChargeStatus, long? LifetimeSupportCents,
    long? EntitledAmountCents, string[] EntitledTierIds, DateTimeOffset? SupportStartedAt = null);
public sealed record ProviderIdentity(string UserId, string? DisplayName, MemberSnapshot? Membership);
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record TierRule(string ProductId, string[] BenefitCodes);
public sealed record SupporterStatus(Guid Id, string? DisplayName, bool HasSupported, string CurrentStatus,
    string[] TierIds, string[] BenefitCodes, DateTimeOffset? LastSuccessfulSyncAt, bool IsStale,
    bool PublicRecognitionEnabled = false, string RecognitionDisplayName = "Apoiante anônimo",
    DateTimeOffset? CurrentSupportStartedAt = null, string SupportDurationLabel = "Apoiante");
public sealed record PublicProfileUpdate(bool OptIn, string? Pseudonym);
public sealed record PublicSupporter(Guid Id, string DisplayName);
public sealed record PublicSupporterPage(PublicSupporter[] Items, int Page, int PageSize, bool HasMore);
public sealed record IssuedKey(string Key);
public sealed record Recognition(bool HasSupported, string Status, string[] TierIds, string[] BenefitCodes);

public interface ISupporterProvider
{
    Task<ProviderIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemberSnapshot>> GetCampaignMembersAsync(string accessToken, CancellationToken cancellationToken);
    Task<OAuthTokens> ExchangeCodeAsync(string code, CancellationToken cancellationToken);
    Task<OAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

public static class RecognitionRules
{
    public static Recognition Evaluate(MemberSnapshot? member, string campaignId, string productId,
        IReadOnlyDictionary<string, TierRule> tiers, bool previousSupport = false)
    {
        if (member is null || member.CampaignId != campaignId)
            return new(previousSupport, "none", [], []);
        var historical = previousSupport || member.LifetimeSupportCents > 0;
        var declined = member.PatronStatus == "declined_patron" || member.LastChargeStatus == "Declined";
        // A positive entitlement distinguishes paid benefits from a free tier.
        var entitled = !declined && member.EntitledAmountCents > 0;
        var allowed = entitled ? member.EntitledTierIds
            .Where(id => tiers.TryGetValue(id, out var rule) && rule.ProductId == productId)
            .Distinct().Order().ToArray() : [];
        var benefits = allowed.SelectMany(id => tiers[id].BenefitCodes).Distinct().Order().ToArray();
        var status = declined ? "declined" : entitled ? "entitled" :
            member.PatronStatus == "former_patron" ? "former" :
            member.PatronStatus is null ? "free" : "unknown";
        return new(historical, status, allowed, benefits);
    }
}
