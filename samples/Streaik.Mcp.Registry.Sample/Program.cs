using Streaik.Mcp.Registry;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapStreaikMcpRegistry();
await app.RunAsync();
