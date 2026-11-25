using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Streaik.Mcp.Registry;

public static class RegistryHosting
{
    private static readonly HttpClient Metadata = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    }) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 16 * 1024 };

    public static RouteHandlerBuilder MapStreaikMcpRegistry(this IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var catalog = new BundledServerCatalog(configuration[BundledServerCatalog.BaseUrlSetting],
            configuration[BundledServerCatalog.EntraBaseUrlSetting]);
        if (catalog.AccessMetadataUrl is not { } metadataUrl)
        {
            var response = catalog.CreateResponse(null);
            return endpoints.MapGet("/v0.1/servers", () => Results.Text(response, "application/json"));
        }

        return endpoints.MapGet("/v0.1/servers", async (HttpContext context) =>
        {
            var access = await ReadAccessAsync(metadataUrl, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(catalog.CreateResponse(access), "application/json");
        });
    }

    private static async Task<RuntimeAccess?> ReadAccessAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Metadata.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RuntimeAccess>(ct) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { return null; }
    }
}

internal sealed record RuntimeAccess(Dictionary<string, string>? ProviderModes, bool GraphObo);
