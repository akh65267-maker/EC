using Azure.Messaging.ServiceBus;
using BuildingBlocks;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.AddServiceDefaults();

// Azure Managed Redis with Entra ID auth (access keys disabled); plain connection string locally.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    if (config.GetConnectionString("Redis") is { Length: > 0 } cs)
        return ConnectionMultiplexer.Connect(cs);

    var options = ConfigurationOptions.Parse(config["Redis:Host"]!);
    options.Ssl = true;
    options.AbortOnConnectFail = false;
    options.ConfigureForAzureWithTokenCredentialAsync(ServiceDefaults.Credential).GetAwaiter().GetResult();
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton(_ => config.GetConnectionString("ServiceBus") is { Length: > 0 } sbcs
    ? new ServiceBusClient(sbcs)
    : new ServiceBusClient(config["ServiceBus:FullyQualifiedNamespace"], ServiceDefaults.Credential));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(config["ServiceBus:QueueName"]));

var app = builder.Build();

app.MapServiceDefaults();

app.Run();
