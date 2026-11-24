using System.Text.Json.Nodes;

namespace Streaik.Mcp.Registry;

internal static class BundledServerCatalog
{
    internal const string BaseUrlSetting = "McpRegistry:DefaultServersBaseUrl";
    private const string DefaultOrigin = "https://default-mcp-host.invalid";

    internal static string CreateResponse(string? configuredUrl)
    {
        var items = new JsonArray();
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            var baseUrl = ReadBaseUrl(configuredUrl);
            using var stream = typeof(BundledServerCatalog).Assembly.GetManifestResourceStream("Streaik.Mcp.Catalog.json")
                ?? throw new InvalidOperationException("The bundled MCP catalog is missing.");
            var servers = JsonNode.Parse(stream)?["servers"]?.AsArray()
                ?? throw new InvalidOperationException("The bundled MCP catalog must contain a servers array.");

            foreach (var document in servers.OrderBy(server => server?["name"]?.GetValue<string>(), StringComparer.Ordinal))
            {
                var server = document?.DeepClone() ?? throw new InvalidOperationException("The bundled MCP catalog contains a null server.");
                var remotes = server["remotes"]?.AsArray()
                    ?? throw new InvalidOperationException("A bundled server is missing its remotes.");
                foreach (var remote in remotes)
                {
                    var url = remote?["url"]?.GetValue<string>();
                    if (url is null || !url.StartsWith(DefaultOrigin + "/", StringComparison.Ordinal) || url.Length == DefaultOrigin.Length + 1)
                        throw new InvalidOperationException($"Bundled remotes must use {DefaultOrigin} and a route.");

                    // Append rather than resolving a rooted URI, which would discard any deployment prefix.
                    remote!["url"] = baseUrl + url[DefaultOrigin.Length..];
                }

                items.Add(new JsonObject { ["server"] = server, ["_meta"] = null });
            }
        }

        return new JsonObject
        {
            ["servers"] = items,
            ["metadata"] = new JsonObject { ["count"] = items.Count }
        }.ToJsonString();
    }

    private static string ReadBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !url.IsWellFormedOriginalString() ||
            (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
            url.Host.Length == 0 || url.GetLeftPart(UriPartial.Authority).Contains('@') ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidOperationException($"{BaseUrlSetting} must be an absolute HTTPS URL (HTTP is allowed on loopback only), without user info, a query, or a fragment.");

        return url.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
