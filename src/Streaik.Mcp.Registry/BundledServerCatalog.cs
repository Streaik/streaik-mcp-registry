using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Streaik.Mcp.Registry;

internal static class BundledServerCatalog
{
    internal const string BaseUrlSetting = "McpRegistry:DefaultServersBaseUrl";
    internal const string EntraBaseUrlSetting = "McpRegistry:EntraServersBaseUrl";
    private const string DefaultOrigin = "https://default-mcp-host.invalid";
    private const string ResourcePrefix = "Streaik.Mcp.Catalog.";
    private const string EntraProtectedRemotes = "net.streaik/entra-protected-remotes";

    private sealed record RuntimeBase(string Url, string Host, int Port, string NamePrefix);

    internal static string CreateResponse(string? configuredUrl, string? configuredEntraUrl)
    {
        var items = new JsonArray();
        var baseUrl = ReadBaseUrl(configuredUrl, BaseUrlSetting);
        var entraUrl = ReadBaseUrl(configuredEntraUrl, EntraBaseUrlSetting);
        if (baseUrl is not null && entraUrl is not null && string.Equals(baseUrl.Url, entraUrl.Url, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{BaseUrlSetting} and {EntraBaseUrlSetting} must not publish the same transport URLs.");
        if (baseUrl is not null && entraUrl is not null && baseUrl.NamePrefix == entraUrl.NamePrefix &&
            (baseUrl.Host != entraUrl.Host || baseUrl.Port != entraUrl.Port))
            throw new InvalidOperationException($"{BaseUrlSetting} and {EntraBaseUrlSetting} must not map different authorities to the same registry namespace.");
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
                var templateName = server["name"]?.GetValue<string>() ?? "";
                var separator = templateName.IndexOf('/');
                var suffix = separator < 0 ? "" : templateName[(separator + 1)..];
                if (!Regex.IsMatch(suffix, @"\A[a-zA-Z0-9._-]+\z"))
                    throw new InvalidOperationException($"The bundled MCP catalog entry '{id}' must have a valid server name suffix.");

                var remotes = server["remotes"]?.AsArray()
                    ?? throw new InvalidOperationException("A bundled server is missing its remotes.");
                var delegated = entry.ContainsKey("obo");
                var path = "/" + id;
                foreach (var remote in remotes)
                {
                    if (remote?["url"]?.GetValue<string>() != DefaultOrigin + path)
                        throw new InvalidOperationException($"The bundled MCP transport URL must match its catalog filename: '{id}'.");
                }

                (RuntimeBase? Base, bool Entra)[] runtimes = [(delegated ? null : baseUrl, false), (entraUrl, true)];
                foreach (var group in runtimes.Where(runtime => runtime.Base is not null).GroupBy(runtime => runtime.Base!.NamePrefix))
                {
                    var name = $"{group.Key}/{suffix}";
                    if (name.Length > 200)
                        throw new InvalidOperationException($"The published MCP server name for '{id}' exceeds 200 characters.");

                    var published = new JsonArray();
                    var protectedUrls = new JsonArray();
                    foreach (var (runtime, entra) in group)
                    {
                        foreach (var remote in remotes)
                        {
                            var url = runtime!.Url + path;
                            var transport = (JsonObject)remote!.DeepClone();
                            transport["url"] = url;
                            if (entra)
                            {
                                if (delegated || transport["headers"] is JsonArray { Count: > 0 }) protectedUrls.Add(url);
                                transport.Remove("headers"); // OAuth discovery stays on the runtime.
                            }
                            published.Add(transport);
                        }
                    }

                    if (published.Count == 0) continue;
                    var document = (JsonObject)server.DeepClone();
                    document["name"] = name;
                    document["remotes"] = published;
                    if (protectedUrls.Count > 0)
                    {
                        if (document["_meta"] is not JsonObject meta)
                            document["_meta"] = meta = new JsonObject();
                        meta[EntraProtectedRemotes] = protectedUrls;
                    }
                    items.Add(new JsonObject { ["server"] = document, ["_meta"] = null });
                }
            }
        }

        return new JsonObject { ["servers"] = items, ["metadata"] = new JsonObject { ["count"] = items.Count } }.ToJsonString();
    }

    private static RuntimeBase? ReadBaseUrl(string? value, string setting)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !url.IsWellFormedOriginalString() ||
            (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)) ||
            url.Host.Length == 0 || url.GetLeftPart(UriPartial.Authority).Contains('@') ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidOperationException($"{setting} must be an absolute HTTPS URL (HTTP is allowed on loopback only), without user info, a query, or a fragment.");

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        var prefix = url.HostNameType switch
        {
            UriHostNameType.Dns => string.Join('.', host.Split('.').Reverse()),
            UriHostNameType.IPv4 => host,
            UriHostNameType.IPv6 when IPAddress.TryParse(host, out var address) && address.ScopeId == 0 =>
                "ipv6-" + Convert.ToHexStringLower(address.GetAddressBytes()),
            _ => ""
        };
        if (!Regex.IsMatch(prefix, @"\A[a-z0-9-]+(?:\.[a-z0-9-]+)*\z"))
            throw new InvalidOperationException($"{setting} must have a host usable as a registry namespace.");
        if (!url.IsDefaultPort) prefix += "-" + url.Port.ToString(CultureInfo.InvariantCulture);

        return new RuntimeBase(url.GetLeftPart(UriPartial.Path).TrimEnd('/'), host, url.Port, prefix);
    }
}
