using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Basket.API.Models;
using BuildingBlocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;

namespace Basket.API.Controllers;

[ApiController]
[Route("api/basket")]
[Authorize]
public class BasketController(IConnectionMultiplexer redis, ServiceBusSender sender) : ControllerBase
{
    private IDatabase Db => redis.GetDatabase();
    private string Key => $"basket:{User.GetUserId()}";

    [HttpGet]
    public async Task<ShoppingCart> Get()
    {
        var value = await Db.StringGetAsync(Key);
        return value.HasValue
            ? JsonSerializer.Deserialize<ShoppingCart>(value.ToString())!
            : new ShoppingCart { UserId = User.GetUserId() };
    }

    [HttpPut]
    public async Task<ShoppingCart> Update(List<CartItem> items)
    {
        var cart = new ShoppingCart { UserId = User.GetUserId(), Items = items };
        await Db.StringSetAsync(Key, JsonSerializer.Serialize(cart), TimeSpan.FromDays(7));
        return cart;
    }

    [HttpDelete]
    public async Task<IActionResult> Delete()
    {
        await Db.KeyDeleteAsync(Key);
        return NoContent();
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> Checkout(CheckoutRequest req)
    {
        var cart = await Get();
        if (cart.Items.Count == 0) return BadRequest("Basket is empty.");

        var evt = new BasketCheckoutEvent(Guid.NewGuid(), cart.UserId, User.Identity?.Name, req.ShippingAddress,
            cart.Total, cart.Items.Select(i => new BasketCheckoutItem(i.ProductId, i.ProductName, i.Price, i.Quantity)).ToList(),
            DateTimeOffset.UtcNow);

        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(evt))
        {
            MessageId = evt.EventId.ToString(),
            ContentType = "application/json"
        });
        await Db.KeyDeleteAsync(Key);
        return Accepted(new { evt.EventId });
    }
}
