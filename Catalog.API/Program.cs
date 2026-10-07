using Azure.Storage.Blobs;
using BuildingBlocks;
using Catalog.API.Data;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPostgresDbContext<CatalogDbContext>("CatalogDb");
builder.Services.AddSingleton(_ => builder.Configuration.GetConnectionString("Blob") is { Length: > 0 } cs
    ? new BlobServiceClient(cs)
    : new BlobServiceClient(new Uri(builder.Configuration["Storage:BlobEndpoint"]!), ServiceDefaults.Credential));

var app = builder.Build();

await app.EnsureDatabaseAsync<CatalogDbContext>();
app.MapServiceDefaults();

app.Run();
