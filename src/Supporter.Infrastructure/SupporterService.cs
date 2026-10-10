using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Supporter.Core;

namespace Supporter.Infrastructure;

public sealed class SupporterService(SupporterDb db, SupporterOptions options, TimeProvider time)
{
    public static string RandomSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<Person> ApplyIdentityAsync(ProviderIdentity identity, CancellationToken ct)
    {
        var person = await db.People.Include(p => p.Membership)
            .SingleOrDefaultAsync(p => p.Provider == options.Provider && p.ExternalUserId == identity.UserId, ct);
        if (person is null)
        {
            person = new Person { Provider = options.Provider, ExternalUserId = identity.UserId };
            db.People.Add(person);
        }
        person.DisplayName = identity.DisplayName;
        if (identity.Membership is { } member && member.CampaignId == options.CampaignId)
        {
            person.Membership ??= new Membership { PersonId = person.Id, CampaignId = options.CampaignId };
            var record = person.Membership;
            record.MemberId = member.MemberId;
            record.PatronStatus = member.PatronStatus;
            record.LastChargeStatus = member.LastChargeStatus;
            record.HistoricalEvidence |= member.LifetimeSupportCents > 0;
            record.EntitledAmountCents = member.EntitledAmountCents;
            record.TierIdsJson = JsonSerializer.Serialize(member.EntitledTierIds);
            record.SupportStartedAt = member.SupportStartedAt;
            record.LastSuccessfulSyncAt = time.GetUtcNow();
            person.HasSupported |= record.HistoricalEvidence;
        }
        else if (person.Membership is { } previous)
        {
            previous.TierIdsJson = "[]";
            previous.EntitledAmountCents = 0;
            previous.PatronStatus = "former_patron";
            previous.SupportStartedAt = null;
            previous.LastSuccessfulSyncAt = time.GetUtcNow();
        }
        person.LastSuccessfulSyncAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return person;
    }

    public async Task<string> RotateKeyAsync(Guid personId, CancellationToken ct)
    {
        var key = RandomSecret();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Credentials.Where(c => c.PersonId == personId && c.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.RevokedAt, time.GetUtcNow()), ct);
        db.Credentials.Add(new AppCredential { Hash = Hash(key), PersonId = personId, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return key;
    }

    public async Task<Guid?> AuthenticateKeyAsync(string key, CancellationToken ct)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) return null;
        var hash = Hash(key);
        return await db.Credentials.Where(c => c.Hash == hash && c.RevokedAt == null)
            .Select(c => (Guid?)c.PersonId).SingleOrDefaultAsync(ct);
    }

    public async Task<SupporterStatus?> GetStatusAsync(Guid id, string productId, CancellationToken ct)
    {
        if (!options.Products.Contains(productId)) throw new ArgumentException("Unknown product.");
        var person = await db.People.Include(p => p.Membership).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (person is null) return null;
        var m = person.Membership;
        var snapshot = m is null ? null : new MemberSnapshot(person.ExternalUserId, person.DisplayName,
            m.CampaignId, m.MemberId, m.PatronStatus, m.LastChargeStatus, m.HistoricalEvidence ? 1 : 0,
            m.EntitledAmountCents, JsonSerializer.Deserialize<string[]>(m.TierIdsJson)!, m.SupportStartedAt);
        var recognized = RecognitionRules.Evaluate(snapshot, options.CampaignId, productId, options.Tiers, person.HasSupported);
        var stale = person.LastSuccessfulSyncAt is null ||
            time.GetUtcNow() - person.LastSuccessfulSyncAt > TimeSpan.FromSeconds(options.StaleAfterSeconds);
        var recognitionName = person.PublicOptIn
            ? person.Pseudonym ?? (string.IsNullOrWhiteSpace(person.DisplayName) ? "Apoiante anônimo" : person.DisplayName)
            : "Apoiante anônimo";
        var now = time.GetUtcNow();
        var supportStart = recognized.Status == "entitled" && m?.SupportStartedAt <= now
            ? m.SupportStartedAt : null;
        return new(person.Id, person.DisplayName, recognized.HasSupported, recognized.Status,
            recognized.TierIds, recognized.BenefitCodes, person.LastSuccessfulSyncAt, stale,
            person.PublicOptIn, recognitionName, supportStart, SupportDuration.Format(supportStart, now));
    }

    public async Task UpdatePublicProfileAsync(Guid id, PublicProfileUpdate update, CancellationToken ct)
    {
        if (update.Pseudonym?.Length > 80 || update.Pseudonym?.Any(char.IsControl) == true)
            throw new ArgumentException("Pseudonym must be at most 80 characters without control characters.");
        var person = await db.People.SingleAsync(p => p.Id == id, ct);
        person.PublicOptIn = update.OptIn;
        person.Pseudonym = string.IsNullOrWhiteSpace(update.Pseudonym) ? null : update.Pseudonym.Trim();
        person.ConsentChangedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<PublicSupporterPage> GetPublicAsync(int page, int size, CancellationToken ct)
    {
        if (page is < 1 or > 10000 || size is < 1 or > 100) throw new ArgumentException("Invalid pagination.");
        var records = await db.People.Where(p => p.PublicOptIn).OrderBy(p => p.Id)
            .Skip((page - 1) * size).Take(size + 1)
            .Select(p => new PublicSupporter(p.Id, p.Pseudonym ?? p.DisplayName ?? "Apoiante anônimo")).ToArrayAsync(ct);
        return new(records.Take(size).ToArray(), page, size, records.Length > size);
    }

    public async Task<(string State, string Session)> StartOAuthAsync(CancellationToken ct)
    {
        var state = RandomSecret();
        var session = RandomSecret();
        db.OAuthAttempts.Add(new OAuthAttempt
        {
            StateHash = Hash(state),
            SessionHash = Hash(session),
            ExpiresAt = time.GetUtcNow().AddMinutes(10)
        });
        await db.SaveChangesAsync(ct);
        return (state, session);
    }

    public Task<int> ConsumeOAuthAsync(string state, string session, CancellationToken ct) =>
        db.OAuthAttempts.Where(a => a.StateHash == Hash(state) && a.SessionHash == Hash(session) && a.ExpiresAt > time.GetUtcNow())
            .ExecuteDeleteAsync(ct);
}
