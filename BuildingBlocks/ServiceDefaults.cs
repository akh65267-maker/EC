using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace BuildingBlocks;

public static class ServiceDefaults
{
    public const string AdminPolicy = "Admin";
    public const string AdminRole = "Catalog.Admin";

    // Managed identity in Azure (via AZURE_CLIENT_ID), your az / Visual Studio login locally.
    public static readonly TokenCredential Credential = new DefaultAzureCredential();

    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        services.AddSingleton(Credential);

        if (!string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            services.AddOpenTelemetry().UseAzureMonitor();

        // Microsoft Entra ID replaces a custom user service.
        var entra = config.GetSection("EntraId");
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = $"https://login.microsoftonline.com/{entra["TenantId"]}/v2.0";
                o.MapInboundClaims = false;
                o.TokenValidationParameters.ValidAudiences = [entra["ClientId"], entra["Audience"]];
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.NameClaimType = "name";
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, p => p.RequireRole(AdminRole));

        services.AddHealthChecks();
        services.AddControllers();
        services.AddOpenApi();
        return builder;
    }

    // Passwordless PostgreSQL: an Entra token is used as password when none is configured.
    public static WebApplicationBuilder AddPostgresDbContext<TContext>(this WebApplicationBuilder builder, string name)
        where TContext : DbContext
    {
        var dsb = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString(name));
        if (string.IsNullOrEmpty(dsb.ConnectionStringBuilder.Password))
        {
            dsb.UsePeriodicPasswordProvider(async (_, ct) =>
                (await Credential.GetTokenAsync(
                    new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct)).Token,
                TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
        }
        var dataSource = dsb.Build();
        builder.Services.AddDbContext<TContext>(o => o.UseNpgsql(dataSource));
        return builder;
    }

    public static async Task EnsureDatabaseAsync<TContext>(this WebApplication app) where TContext : DbContext
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync();
    }

    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
            app.MapOpenApi();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapControllers();
        return app;
    }

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value
        ?? throw new InvalidOperationException("Token has no user id.");
}
