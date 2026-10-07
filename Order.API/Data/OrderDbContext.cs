using Microsoft.EntityFrameworkCore;

namespace Order.API.Data;

public class OrderItem
{
    public Guid ProductId { get; set; }
    public required string ProductName { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public class CustomerOrder
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public string? UserName { get; set; }
    public required string ShippingAddress { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerOrder>(o =>
        {
            o.HasIndex(x => x.UserId);
            o.OwnsMany(x => x.Items, i => i.ToJson());
        });
    }
}
