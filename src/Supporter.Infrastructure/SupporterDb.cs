using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Supporter.Infrastructure;

public sealed class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "";
    public string ExternalUserId { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool HasSupported { get; set; }
    public bool PublicOptIn { get; set; }
    public string? Pseudonym { get; set; }
    public DateTimeOffset? ConsentChangedAt { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public Membership? Membership { get; set; }
}
public sealed class Membership
{
    public Guid PersonId { get; set; }
    public string CampaignId { get; set; } = "";
    public string? MemberId { get; set; }
    public string? PatronStatus { get; set; }
    public string? LastChargeStatus { get; set; }
    public bool HistoricalEvidence { get; set; }
    public long? EntitledAmountCents { get; set; }
    public string TierIdsJson { get; set; } = "[]";
    public DateTimeOffset? SupportStartedAt { get; set; }
    public DateTimeOffset LastSuccessfulSyncAt { get; set; }
}
public sealed class AppCredential
{
    public string Hash { get; set; } = "";
    public Guid PersonId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
public sealed class TokenRecord
{
    public string Owner { get; set; } = "";
    public string ProtectedTokens { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class OAuthAttempt
{
    public string StateHash { get; set; } = "";
    public string SessionHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class InboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Digest { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool DeadLetter { get; set; }
}
public sealed class SyncState
{
    public string Id { get; set; } = "campaign";
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public DateTimeOffset? NextAllowedAt { get; set; }
    public string? LastErrorCode { get; set; }
}

public sealed class SupporterDb(DbContextOptions<SupporterDb> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<AppCredential> Credentials => Set<AppCredential>();
    public DbSet<TokenRecord> Tokens => Set<TokenRecord>();
    public DbSet<OAuthAttempt> OAuthAttempts => Set<OAuthAttempt>();
    public DbSet<InboxEvent> Inbox => Set<InboxEvent>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Person>().HasIndex(p => new { p.Provider, p.ExternalUserId }).IsUnique();
        builder.Entity<Person>().HasOne(p => p.Membership).WithOne().HasForeignKey<Membership>(m => m.PersonId);
        builder.Entity<Membership>().HasKey(m => m.PersonId);
        builder.Entity<Membership>().HasIndex(m => new { m.CampaignId, m.MemberId }).IsUnique();
        builder.Entity<AppCredential>().HasKey(c => c.Hash);
        builder.Entity<AppCredential>().HasOne<Person>().WithMany().HasForeignKey(c => c.PersonId);
        builder.Entity<AppCredential>().HasIndex(c => c.PersonId);
        builder.Entity<TokenRecord>().HasKey(t => t.Owner);
        builder.Entity<OAuthAttempt>().HasKey(a => a.StateHash);
        builder.Entity<InboxEvent>().HasIndex(e => e.Digest).IsUnique();
        builder.Entity<InboxEvent>().HasIndex(e => new { e.CompletedAt, e.NextAttemptAt });
        builder.Entity<SyncState>().HasKey(s => s.Id);
    }
}
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
