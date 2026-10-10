using System.Net.Http.Headers;
using System.Net.Http.Json;
using Supporter.Core;

namespace Supporter.Client;

public sealed class SupporterClient(HttpClient httpClient)
{
    public async Task<SupporterStatus> GetMeAsync(string key, string productId, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "api/v1/supporter/me?productId=" + Uri.EscapeDataString(productId), key);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SupporterStatus>(cancellationToken)
            ?? throw new InvalidDataException("Empty supporter response.");
    }

    public async Task UpdatePublicProfileAsync(string key, PublicProfileUpdate profile, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Put, "api/v1/supporter/public-profile", key);
        request.Content = JsonContent.Create(profile);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PublicSupporterPage> ListPublicAsync(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<PublicSupporterPage>($"api/v1/supporters?page={page}&pageSize={pageSize}", cancellationToken)
        ?? throw new InvalidDataException("Empty public response.");

    private HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var origin = httpClient.BaseAddress ?? throw new InvalidOperationException("Set BaseAddress.");
        if (origin.Scheme != Uri.UriSchemeHttps && !(origin.Scheme == "http" && origin.IsLoopback))
            throw new InvalidOperationException("HTTPS is required outside localhost.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
