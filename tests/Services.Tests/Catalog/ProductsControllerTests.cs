using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Catalog.API.Controllers;
using Catalog.API.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Services.Tests.Catalog;

public class ProductsControllerTests
{
    private readonly CatalogDbContext _db = TestHelpers.NewDb<CatalogDbContext>(o => new(o));
    private readonly BlobServiceClient _blobs = Substitute.For<BlobServiceClient>();
    private readonly ProductsController _sut;

    public ProductsControllerTests() =>
        _sut = new ProductsController(_db, _blobs, new ConfigurationBuilder().Build());

    private async Task<Product> Seed(string name = "Shoe", decimal price = 10)
    {
        var p = new Product { Name = name, Price = price };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task GetAll_returns_page_sorted_by_name()
    {
        await Seed("B"); await Seed("A"); await Seed("C");

        var result = (await _sut.GetAll(page: 0, size: 2)).ToList();

        Assert.Equal(["A", "B"], result.Select(p => p.Name));
    }

    [Fact]
    public async Task Get_unknown_id_returns_not_found()
    {
        var result = await _sut.Get(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_persists_product()
    {
        var result = await _sut.Create(new ProductRequest("Hat", "Red", 25, 3));

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var product = Assert.IsType<Product>(created.Value);
        Assert.Equal("Hat", (await _db.Products.FindAsync(product.Id))!.Name);
    }

    [Fact]
    public async Task Update_changes_fields()
    {
        var p = await Seed();

        var result = await _sut.Update(p.Id, new ProductRequest("Boot", null, 99, 7));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(("Boot", 99m, 7), (p.Name, p.Price, p.Stock));
    }

    [Fact]
    public async Task Update_unknown_id_returns_not_found() =>
        Assert.IsType<NotFoundResult>(await _sut.Update(Guid.NewGuid(), new ProductRequest("X", null, 1, 1)));

    [Fact]
    public async Task Delete_removes_product()
    {
        var p = await Seed();

        Assert.IsType<NoContentResult>(await _sut.Delete(p.Id));
        Assert.Empty(_db.Products);
    }

    [Fact]
    public async Task UploadImage_stores_blob_and_sets_url()
    {
        var p = await Seed();
        var container = Substitute.For<BlobContainerClient>();
        var blob = Substitute.For<BlobClient>();
        var blobUri = new Uri($"https://st.blob.core.windows.net/product-images/{p.Id}.png");
        _blobs.GetBlobContainerClient("product-images").Returns(container);
        container.GetBlobClient($"{p.Id}.png").Returns(blob);
        blob.Uri.Returns(blobUri);

        var file = Substitute.For<IFormFile>();
        file.FileName.Returns("photo.png");
        file.ContentType.Returns("image/png");
        file.OpenReadStream().Returns(new MemoryStream([1, 2, 3]));

        var result = await _sut.UploadImage(p.Id, file);

        Assert.Equal(blobUri.ToString(), result.Value!.ImageUrl);
        await blob.Received(1).UploadAsync(Arg.Any<Stream>(),
            Arg.Is<BlobUploadOptions>(o => o.HttpHeaders.ContentType == "image/png"), Arg.Any<CancellationToken>());
    }
}
