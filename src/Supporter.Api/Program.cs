using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Supporter.Api;
using Supporter.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue<bool>("Setup:Enabled"))
{
    await SetupApplication.RunAsync(builder);
    return;
}
builder.Services.AddSupporterKit(builder.Configuration, builder.Environment);
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IDataProtectionProvider>(sp => sp.GetRequiredService<SupporterProtection>().Provider);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "supporter.csrf";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<AuthenticationSchemeOptions, KeyAuthenticationHandler>("ApiKey", _ => { })
    .AddCookie("Management", options =>
    {
        options.Cookie.Name = "supporter.management";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
            ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context => Results.Problem(statusCode: 401, title: "Sessão de gestão necessária.").ExecuteAsync(context.HttpContext);
        options.Events.OnRedirectToAccessDenied = context => Results.Problem(statusCode: 403, title: "Acesso recusado.").ExecuteAsync(context.HttpContext);
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Management", policy => policy.AddAuthenticationSchemes("Management").RequireAuthenticatedUser());
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return new ValueTask(Results.Problem(statusCode: 429, title: "Limite de chamadas atingido.").ExecuteAsync(context.HttpContext));
    };
    options.AddPolicy("public", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("sensitive", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
var app = builder.Build();
var settings = app.Services.GetRequiredService<SupporterOptions>();
var origin = new Uri(settings.PublicOrigin);
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    ForwardLimit = 1
};
forwarded.KnownProxies.Clear();
forwarded.KnownIPNetworks.Clear();
foreach (var proxy in settings.TrustedProxies) forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
if (settings.TrustedProxies.Length > 0) app.UseForwardedHeaders(forwarded);
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/health")) { await next(context); return; }
    if (!string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase) ||
        (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing") && !context.Request.IsHttps))
    {
        await Results.Problem(statusCode: 400, title: "Host ou protocolo inválido.").ExecuteAsync(context);
        return;
    }
    if (!context.Request.Path.Equals("/api/v1/supporters"))
        context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (ArgumentException) { await Results.Problem(statusCode: 400, title: "Dados inválidos.").ExecuteAsync(context); }
    catch (System.Text.Json.JsonException) { await Results.Problem(statusCode: 400, title: "JSON inválido.").ExecuteAsync(context); }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        await Results.Problem(statusCode: 400, title: "Validação CSRF falhou.").ExecuteAsync(context);
    }
    catch (ProviderException) { await Results.Problem(statusCode: 503, title: "Provedor indisponível; tente novamente.").ExecuteAsync(context); }
    catch (HttpRequestException) { await Results.Problem(statusCode: 503, title: "Provedor indisponível; tente novamente.").ExecuteAsync(context); }
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapSupporterEndpoints();
app.MapOpenApi().RequireRateLimiting("public");
await app.Services.InitializeSupporterKitAsync();
await app.RunAsync();

public partial class Program { }
