using System.Net;
using System.Net.Http.Json;
using Basket.API.Models;
using Order.API.Data;

namespace Services.IntegrationTests;

/// <summary>Basket (Redis) -> Service Bus -> Order (PostgreSQL), end to end.</summary>
[Collection(InfraCollection.Name)]
public class CheckoutFlowTests(InfraFixture infra) : IAsyncLifetime
{
    private readonly ServiceFactory<ShoppingCart> _basket = infra.Basket();
    private readonly ServiceFactory<OrderDbContext> _order = infra.Order();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        await _basket.DisposeAsync();
        await _order.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_cannot_read_basket() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _basket.CreateClient().GetAsync("/api/basket")).StatusCode);

    [Fact]
    public async Task Basket_is_stored_per_user()
    {
        var alice = _basket.CreateClientAs("alice");
        await alice.PutAsJsonAsync("/api/basket", new[] { new CartItem(Guid.NewGuid(), "Hat", 5, 2) });

        var aliceCart = await alice.GetFromJsonAsync<ShoppingCart>("/api/basket");
        var bobCart = await _basket.CreateClientAs("bob").GetFromJsonAsync<ShoppingCart>("/api/basket");

        Assert.Equal(10, aliceCart!.Total);
        Assert.Empty(bobCart!.Items);
    }

    [Fact]
    public async Task Checkout_creates_order_and_empties_basket()
    {
        var user = $"buyer-{Guid.NewGuid():N}";
        var orders = _order.CreateClientAs(user); // starts Order.API and its queue consumer
        var basket = _basket.CreateClientAs(user);
        await basket.PutAsJsonAsync("/api/basket", new[] { new CartItem(Guid.NewGuid(), "Shoe", 20, 3) });

        var checkout = await basket.PostAsJsonAsync("/api/basket/checkout", new CheckoutRequest("Main St 1"));
        Assert.Equal(HttpStatusCode.Accepted, checkout.StatusCode);

        var order = await WaitForOrder(orders);
        Assert.Equal((60m, "Main St 1", "Pending"), (order.Total, order.ShippingAddress, order.Status));
        Assert.Equal("Shoe", Assert.Single(order.Items).ProductName);
        Assert.Empty((await basket.GetFromJsonAsync<ShoppingCart>("/api/basket"))!.Items);
    }

    private static async Task<CustomerOrder> WaitForOrder(HttpClient orders)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var mine = await orders.GetFromJsonAsync<List<CustomerOrder>>("/api/orders", timeout.Token);
            if (mine is [var order]) return order;
            await Task.Delay(500, timeout.Token);
        }
    }
}
