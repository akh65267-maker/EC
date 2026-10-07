using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BuildingBlocks;
using Catalog.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Controllers;

public record ProductRequest(string Name, string? Description, decimal Price, int Stock);

[ApiController]
[Route("api/products")]
[Authorize(Policy = ServiceDefaults.AdminPolicy)]
public class ProductsController(CatalogDbContext db, BlobServiceClient blobs, IConfiguration config) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IEnumerable<Product>> GetAll(int page = 0, int size = 20) =>
        await db.Products.AsNoTracking().OrderBy(p => p.Name).Skip(page * size).Take(size).ToListAsync();

    [HttpGet("{id:guid}"), AllowAnonymous]
    public async Task<ActionResult<Product>> Get(Guid id) =>
        await db.Products.FindAsync(id) is { } p ? p : NotFound();

    [HttpPost]
    public async Task<ActionResult<Product>> Create(ProductRequest req)
    {
        var p = new Product { Name = req.Name, Description = req.Description, Price = req.Price, Stock = req.Stock };
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = p.Id }, p);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, ProductRequest req)
    {
        if (await db.Products.FindAsync(id) is not { } p) return NotFound();
        (p.Name, p.Description, p.Price, p.Stock) = (req.Name, req.Description, req.Price, req.Stock);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (await db.Products.FindAsync(id) is not { } p) return NotFound();
        db.Products.Remove(p);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // Product images go to Blob Storage via managed identity.
    [HttpPost("{id:guid}/image")]
    public async Task<ActionResult<Product>> UploadImage(Guid id, IFormFile file)
    {
        if (await db.Products.FindAsync(id) is not { } p) return NotFound();

        var container = blobs.GetBlobContainerClient(config["Storage:ImagesContainer"] ?? "product-images");
        await container.CreateIfNotExistsAsync(PublicAccessType.Blob);
        var blob = container.GetBlobClient($"{id}{Path.GetExtension(file.FileName)}");
        await using var stream = file.OpenReadStream();
        await blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = new() { ContentType = file.ContentType } });

        p.ImageUrl = blob.Uri.ToString();
        await db.SaveChangesAsync();
        return p;
    }
}
