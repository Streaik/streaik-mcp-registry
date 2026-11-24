using Streaik.Mcp.Registry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
           .AllowAnyHeader()
           .AllowAnyOrigin()
           .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")));

var app = builder.Build();
app.UseCors();
app.MapStreaikMcpRegistry();
await app.RunAsync();
