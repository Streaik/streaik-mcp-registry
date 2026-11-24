using System.Text.Json.Nodes;

namespace Streaik.Mcp.Registry;

internal static class BundledServerCatalog
{
    internal const string BaseUrlSetting = "McpRegistry:DefaultServersBaseUrl";
    internal const string EntraBaseUrlSetting = "McpRegistry:EntraServersBaseUrl";
    private const string DefaultOrigin = "https://default-mcp-host.invalid";
    private const string ResourcePrefix = "Streaik.Mcp.Catalog.";

    internal static string CreateResponse(string? configuredUrl, string? configuredEntraUrl)
    {
        var items = new JsonArray();
        var baseUrl = ReadBaseUrl(configuredUrl, BaseUrlSetting);
        var entraUrl = ReadBaseUrl(configuredEntraUrl, EntraBaseUrlSetting);
        if (baseUrl is not null && string.Equals(baseUrl, entraUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{BaseUrlSetting} and {EntraBaseUrlSetting} must not publish the same transport URLs.");
        if (baseUrl is not null || entraUrl is not null)
        {
            var assembly = typeof(BundledServerCatalog).Assembly;
            var files = assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("The bundled MCP catalog is missing.");

            var entries = files.Select(file =>
            {
                using var stream = assembly.GetManifestResourceStream(file)!;
                var entry = JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidOperationException("A bundled MCP catalog file is empty.");
                return (Id: file[ResourcePrefix.Length..^5], Entry: entry);
            }).OrderBy(item => item.Entry["server"]?["name"]?.GetValue<string>(), StringComparer.Ordinal);

            foreach (var (id, entry) in entries)
            {
                var server = entry["server"]?.AsObject() ?? throw new InvalidOperationException("A bundled MCP catalog entry is missing its registry document.");
                var delegated = entry.ContainsKey("obo");
                var remotes = server["remotes"]?.AsArray()
                    ?? throw new InvalidOperationException("A bundled server is missing its remotes.");
                var published = new JsonArray();
                foreach (var remote in remotes)
                {
                    var url = remote?["url"]?.GetValue<string>();
                    var path = "/" + id;
                    if (url != DefaultOrigin + path)
                        throw new InvalidOperationException($"The bundled MCP transport URL must match its catalog filename: '{id}'.");

                    if (baseUrl is not null && !delegated)
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

                if (published.Count > 0)
                {
                    server["remotes"] = published;
                    items.Add(new JsonObject { ["server"] = server.DeepClone(), ["_meta"] = null });
                }
            }
        }

        return new JsonObject { ["servers"] = items, ["metadata"] = new JsonObject { ["count"] = items.Count } }.ToJsonString();
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
