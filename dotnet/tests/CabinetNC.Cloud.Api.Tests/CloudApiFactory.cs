using System.Net.Http.Headers;
using System.Text;
using CabinetNC.Cloud.Contracts;
using CabinetNC.Cloud.Infrastructure.Storage;
using CabinetNC.Cloud.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CabinetNC.Cloud.Api.Tests;

/// <summary>
/// Boots the real API (migrations + bootstrap admin) against the fixture's PostgreSQL with test-only
/// configuration and a controllable clock. Secrets below are test fixtures, not real credentials.
/// </summary>
public sealed class CloudApiFactory(
    string connectionString,
    ManualClock clock,
    bool includeBootstrap = true) : WebApplicationFactory<Program>
{
    public const string Tenant = "acme";
    public const string AdminEmail = "admin@acme.test";
    public const string AdminPassword = "Correct-Horse-Battery-Staple-42";
    public const string SigningKey = "test-only-signing-key-0123456789abcdef0123456789abcdef-not-for-production";

    public TestObjectStore ObjectStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(new CloudApiOptions
            {
                DbConnection = connectionString,
                JwtSigningKey = SigningKey,
                BootstrapTenant = includeBootstrap ? Tenant : null,
                BootstrapAdminEmail = includeBootstrap ? AdminEmail : null,
                BootstrapAdminPassword = includeBootstrap ? AdminPassword : null,
            }));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            services.Replace(ServiceDescriptor.Singleton<IObjectStore>(ObjectStore));
        });
    }
}

public static class ApiClientExtensions
{
    public static async Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string route, T body, string? bearer = null, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(CloudJson.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (correlationId is not null)
            request.Headers.Add(ApiHeaders.CorrelationId, correlationId);
        return await client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        CloudJson.Deserialize<T>(await response.Content.ReadAsStringAsync());

    public static async Task<ApiError> ReadErrorAsync(this HttpResponseMessage response) =>
        await response.ReadAsync<ApiError>();
}
