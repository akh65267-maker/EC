namespace Basket.API.Models;

public record CartItem(Guid ProductId, string ProductName, decimal Price, int Quantity);

public class ShoppingCart
{
    public string UserId { get; set; } = "";
    public List<CartItem> Items { get; set; } = [];
    public decimal Total => Items.Sum(i => i.Price * i.Quantity);
}

public record CheckoutRequest(string ShippingAddress);
