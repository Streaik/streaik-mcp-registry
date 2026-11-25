using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Streaik.Mcp.Registry;

var builder = WebApplication.CreateBuilder(args);
/*var entra = builder.Configuration.GetSection("AzureAd");
if (!Guid.TryParseExact(entra["TenantId"], "D", out var tenantId) || tenantId == Guid.Empty ||
    !Guid.TryParseExact(entra["ClientId"], "D", out var clientId) || clientId == Guid.Empty)
    throw new InvalidOperationException("Set AzureAd:TenantId and AzureAd:ClientId to the Entra tenant and registry API application IDs.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(entra);
builder.Services.AddAuthorization();*/
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin().AllowAnyHeader().WithMethods("GET")));

var app = builder.Build();
app.UseCors();
//app.UseAuthentication();
//app.UseAuthorization();
app.MapStreaikMcpRegistry().AllowAnonymous();
await app.RunAsync();
