using System.Text.Json.Nodes;

namespace Streaik.Mcp.Registry;

internal static class BundledServerCatalog
{
    internal const string BaseUrlSetting = "McpRegistry:DefaultServersBaseUrl";
    internal const string EntraBaseUrlSetting = "McpRegistry:EntraServersBaseUrl";
    private const string DefaultOrigin = "https://default-mcp-host.invalid";

    internal static string CreateResponse(string? configuredUrl, string? configuredEntraUrl)
    {
        var items = new JsonArray();
        var baseUrl = ReadBaseUrl(configuredUrl, BaseUrlSetting);
        var entraUrl = ReadBaseUrl(configuredEntraUrl, EntraBaseUrlSetting);
        if (baseUrl is not null && string.Equals(baseUrl, entraUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{BaseUrlSetting} and {EntraBaseUrlSetting} must not publish the same transport URLs.");
        if (baseUrl is not null || entraUrl is not null)
        {
            using var stream = typeof(BundledServerCatalog).Assembly.GetManifestResourceStream("Streaik.Mcp.Catalog.json")
                ?? throw new InvalidOperationException("The bundled MCP catalog is missing.");
            var servers = JsonNode.Parse(stream)?["servers"]?.AsArray()
                ?? throw new InvalidOperationException("The bundled MCP catalog must contain a servers array.");

            foreach (var document in servers.OrderBy(server => server?["name"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                var server = document?.DeepClone() ?? throw new InvalidOperationException("The bundled MCP catalog contains a null server.");
                var remotes = server["remotes"]?.AsArray()
                    ?? throw new InvalidOperationException("A bundled server is missing its remotes.");
                var published = new JsonArray();
                foreach (var remote in remotes)
                {
                    var url = remote?["url"]?.GetValue<string>();
                    if (url is null || !url.StartsWith(DefaultOrigin + "/", StringComparison.Ordinal) || url.Length == DefaultOrigin.Length + 1)
                        throw new InvalidOperationException($"Bundled remotes must use {DefaultOrigin} and a route.");

                    // Append rather than resolving a rooted URI, which would discard any deployment prefix.
                    var path = url[DefaultOrigin.Length..];
                    if (baseUrl is not null)
                    {
                        var byok = remote!.DeepClone();
                        byok["url"] = baseUrl + path;
                        published.Add(byok);
                    }
                    if (entraUrl is not null)
                    {
                        var entra = (JsonObject)remote!.DeepClone();
                        entra["url"] = entraUrl + path;
                        entra.Remove("headers"); // OAuth metadata comes from the runtime, not the registry.
                        published.Add(entra);
                    }
                }

                server["remotes"] = published;
                items.Add(new JsonObject { ["server"] = server, ["_meta"] = null });
            }
        }

        return new JsonObject
        {
            ["servers"] = items,
            ["metadata"] = new JsonObject { ["count"] = items.Count }
        }.ToJsonString();
    }

    private static string? ReadBaseUrl(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !url.IsWellFormedOriginalString() ||
            (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
            url.Host.Length == 0 || url.GetLeftPart(UriPartial.Authority).Contains('@') ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidOperationException($"{setting} must be an absolute HTTPS URL (HTTP is allowed on loopback only), without user info, a query, or a fragment.");

        return url.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
