using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
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

        // Local testing without Entra: never allowed outside Development.
        var devBypass = config.GetValue<bool>("Auth:DevBypass");
        if (devBypass && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Auth:DevBypass is only allowed in the Development environment.");

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = devBypass ? DevAuthHandler.SchemeName : JwtBearerDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        });
        if (devBypass)
            auth.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
        auth.AddJwtBearer(o =>
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
        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            // Adds the "Authorize" button to Swagger UI for pasting an Entra access token.
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Entra ID access token (az account get-access-token --scope <entraScope>)"
            };
            doc.Security ??= [];
            doc.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", doc)] = [] });

            if (devBypass)
            {
                // Fill these in Swagger UI's "Authorize" dialog to call endpoints without Entra.
                doc.Components.SecuritySchemes["DevUser"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = DevAuthHandler.UserHeader,
                    Description = "Dev bypass: any user id, e.g. alice"
                };
                doc.Components.SecuritySchemes["DevRoles"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = DevAuthHandler.RolesHeader,
                    Description = $"Dev bypass: comma-separated roles, e.g. {AdminRole}"
                };
                doc.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("DevUser", doc)] = [],
                    [new OpenApiSecuritySchemeReference("DevRoles", doc)] = []
                });
            }
            return Task.CompletedTask;
        }));
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
        {
            app.MapOpenApi();
            app.UseSwaggerUI(o =>
            {
                o.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
                o.RoutePrefix = "swagger";
            });
        }

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
