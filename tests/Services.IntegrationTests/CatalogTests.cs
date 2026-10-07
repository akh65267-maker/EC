using System.Net;
using System.Net.Http.Json;
using BuildingBlocks;
using Catalog.API.Controllers;
using Catalog.API.Data;

namespace Services.IntegrationTests;

[Collection(InfraCollection.Name)]
public class CatalogTests(InfraFixture infra) : IAsyncLifetime
{
    private readonly ServiceFactory<CatalogDbContext> _factory = infra.Catalog();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpClient Admin() => _factory.CreateClientAs("admin", ServiceDefaults.AdminRole);

    [Fact]
    public async Task Health_is_ok() =>
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().GetAsync("/health")).StatusCode);

    [Fact]
    public async Task Anonymous_cannot_create_product()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/products", new ProductRequest("X", null, 1, 1));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Non_admin_cannot_create_product()
    {
        var res = await _factory.CreateClientAs("u1").PostAsJsonAsync("/api/products", new ProductRequest("X", null, 1, 1));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_creates_product_and_anyone_can_read_it()
    {
        var res = await Admin().PostAsJsonAsync("/api/products", new ProductRequest("Integration Shoe", "Blue", 49.99m, 5));
        res.EnsureSuccessStatusCode();
        var created = (await res.Content.ReadFromJsonAsync<Product>())!;

        var fetched = await _factory.CreateClient().GetFromJsonAsync<Product>($"/api/products/{created.Id}");

        Assert.Equal(("Integration Shoe", 49.99m), (fetched!.Name, fetched.Price));
    }

    [Fact]
    public async Task Upload_image_stores_blob_in_storage()
    {
        var admin = Admin();
        var product = (await (await admin.PostAsJsonAsync("/api/products", new ProductRequest("Pic", null, 1, 1)))
            .Content.ReadFromJsonAsync<Product>())!;

        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        image.Headers.ContentType = new("image/png");
        form.Add(image, "file", "photo.png");
        var res = await admin.PostAsync($"/api/products/{product.Id}/image", form);
        res.EnsureSuccessStatusCode();
        var updated = (await res.Content.ReadFromJsonAsync<Product>())!;

        // Container is public, so the image URL is readable without credentials.
        using var http = new HttpClient();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], await http.GetByteArrayAsync(updated.ImageUrl));
    }
}
