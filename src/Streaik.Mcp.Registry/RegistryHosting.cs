using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Streaik.Mcp.Registry;

public static class RegistryHosting
{
    public static RouteHandlerBuilder MapStreaikMcpRegistry(this IEndpointRouteBuilder endpoints)
    {
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var response = BundledServerCatalog.CreateResponse(configuration[BundledServerCatalog.BaseUrlSetting]);
        return endpoints.MapGet("/v0.1/servers", () => Results.Text(response, "application/json"));
    }
}
