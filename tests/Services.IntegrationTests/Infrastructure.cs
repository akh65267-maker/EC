using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.ServiceBus;

namespace Services.IntegrationTests;

/// <summary>Real PostgreSQL, Redis, Azurite and Service Bus emulator, shared by all tests.</summary>
public sealed class InfraFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:16").Build();
    public RedisContainer Redis { get; } = new RedisBuilder("redis:7-alpine").Build();
    public AzuriteContainer Azurite { get; } = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithCommand("--skipApiVersionCheck").Build();
    public ServiceBusContainer ServiceBus { get; } = new ServiceBusBuilder("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
        .WithAcceptLicenseAgreement(true)
        .WithResourceMapping(File.ReadAllBytes("servicebus-config.json"), "/ServiceBus_Emulator/ConfigFiles/Config.json")
        .Build();

    public const string Queue = "basket-checkout";

    public Task InitializeAsync() =>
        Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync(), Azurite.StartAsync(), ServiceBus.StartAsync());

    public Task DisposeAsync() => Task.WhenAll(
        Postgres.DisposeAsync().AsTask(), Redis.DisposeAsync().AsTask(),
        Azurite.DisposeAsync().AsTask(), ServiceBus.DisposeAsync().AsTask());

    public string Db(string name) => $"{Postgres.GetConnectionString()};Database={name}";

    public ServiceFactory<Catalog.API.Data.CatalogDbContext> Catalog() => new(new()
    {
        ["ConnectionStrings:CatalogDb"] = Db("catalogdb"),
        ["ConnectionStrings:Blob"] = Azurite.GetConnectionString(),
    });

    public ServiceFactory<Basket.API.Models.ShoppingCart> Basket() => new(new()
    {
        ["ConnectionStrings:Redis"] = Redis.GetConnectionString(),
        ["ConnectionStrings:ServiceBus"] = ServiceBus.GetConnectionString(),
        ["ServiceBus:QueueName"] = Queue,
    });

    public ServiceFactory<Order.API.Data.OrderDbContext> Order() => new(new()
    {
        ["ConnectionStrings:OrderDb"] = Db("orderdb"),
        ["ConnectionStrings:ServiceBus"] = ServiceBus.GetConnectionString(),
        ["ServiceBus:QueueName"] = Queue,
    });
}

[CollectionDefinition(Name)]
public class InfraCollection : ICollectionFixture<InfraFixture>
{
    public const string Name = "infra";
}

/// <summary>Hosts a service in-memory. TMarker is any type from the service's assembly.</summary>
public class ServiceFactory<TMarker>(Dictionary<string, string> settings) : WebApplicationFactory<TMarker>
    where TMarker : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development, so appsettings.Development.json (localhost emulators) isn't loaded.
        builder.UseEnvironment("Testing");

        // UseSetting is visible to config read eagerly in Program.cs.
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services => services
            .AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null));
    }

    public HttpClient CreateClientAs(string? userId, params string[] roles)
    {
        var client = CreateClient();
        if (userId is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }
        return client;
    }
}

/// <summary>Stands in for Entra ID: the caller is taken from request headers.</summary>
public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("oid", user!), new("name", $"user-{user}") };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("roles", r)));
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
