using Azure.Messaging.ServiceBus;
using BuildingBlocks;
using Order.API.Data;
using Order.API.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDbContext<OrderDbContext>("OrderDb");
builder.Services.AddSingleton(_ => builder.Configuration.GetConnectionString("ServiceBus") is { Length: > 0 } sbcs
    ? new ServiceBusClient(sbcs)
    : new ServiceBusClient(builder.Configuration["ServiceBus:FullyQualifiedNamespace"], ServiceDefaults.Credential));
builder.Services.AddHostedService<BasketCheckoutConsumer>();

var app = builder.Build();

await app.EnsureDatabaseAsync<OrderDbContext>();
app.MapServiceDefaults();

app.Run();
