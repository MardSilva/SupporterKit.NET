using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Supporter.Core;

namespace Supporter.Infrastructure;

public static class Registration
{
    public static IServiceCollection AddSupporterKit(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Resolve after all host configuration sources have been applied.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            var options = config.GetSection("Supporter").Get<SupporterOptions>() ?? new();
            options.Validate(env);
            options.DataDirectory = Path.GetFullPath(options.DataDirectory, env.ContentRootPath);
            options.FixturesPath = Path.GetFullPath(options.FixturesPath, env.ContentRootPath);
            Directory.CreateDirectory(options.DataDirectory);
            return options;
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenRefreshGate>();
        services.AddSingleton<SyncGate>();
        services.AddSingleton<SupporterProtection>(sp =>
        {
            var options = sp.GetRequiredService<SupporterOptions>();
            var config = sp.GetRequiredService<IConfiguration>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            var path = config["Supporter:KeyCertificatePath"];
            var local = env.IsDevelopment() || env.IsEnvironment("Testing");
            if (string.IsNullOrWhiteSpace(path) && !local)
                throw new InvalidOperationException("Production requires KeyCertificatePath for encryption of the key ring.");
            return new SupporterProtection(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(options.DataDirectory, "keys")), protection =>
            {
                protection.SetApplicationName("SupporterKit.NET");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var cert = X509CertificateLoader.LoadPkcs12FromFile(path, config["Supporter:KeyCertificatePassword"]);
                    if (!cert.HasPrivateKey || cert.GetRSAPrivateKey() is null)
                        throw new InvalidOperationException("The key-ring certificate needs an RSA private key.");
                    protection.ProtectKeysWithCertificate(cert);
                }
            }));
        });
        services.AddDbContext<SupporterDb>((sp, db) => db.UseSqlite(
            $"Data Source={Path.Combine(sp.GetRequiredService<SupporterOptions>().DataDirectory, "supporter.db")};Default Timeout=30"));
        services.AddScoped<SupporterService>();
        services.AddScoped<TokenService>();
        services.AddScoped<WebhookInbox>();
        services.AddScoped<CampaignSynchronizer>();
        services.AddSingleton<FakeProvider>();
        services.AddHttpClient<PatreonProvider>((sp, http) =>
        {
            var options = sp.GetRequiredService<SupporterOptions>();
            http.BaseAddress = new Uri("https://www.patreon.com");
            http.Timeout = TimeSpan.FromSeconds(30);
            http.MaxResponseContentBufferSize = 8 * 1024 * 1024;
            http.DefaultRequestHeaders.UserAgent.ParseAdd(options.Patreon.UserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
        services.AddScoped<ISupporterProvider>(sp => sp.GetRequiredService<SupporterOptions>().IsFake
            ? sp.GetRequiredService<FakeProvider>() : sp.GetRequiredService<PatreonProvider>());
        services.AddHostedService<SyncWorker>();
        return services;
    }

    public static async Task InitializeSupporterKitAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<SupporterProtection>();
        await scope.ServiceProvider.GetRequiredService<SupporterDb>().Database.MigrateAsync(ct);
    }
}

public sealed record SupporterProtection(IDataProtectionProvider Provider);
