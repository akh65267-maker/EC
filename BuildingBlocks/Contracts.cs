namespace BuildingBlocks;

public record BasketCheckoutItem(Guid ProductId, string ProductName, decimal Price, int Quantity);

public record BasketCheckoutEvent(
    Guid EventId, string UserId, string? UserName, string ShippingAddress,
    decimal Total, IReadOnlyList<BasketCheckoutItem> Items, DateTimeOffset CreatedAt);
