using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Basket.API.Controllers;
using Basket.API.Models;
using BuildingBlocks;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using StackExchange.Redis;

namespace Services.Tests.Basket;

public class BasketControllerTests
{
    private const string UserId = "user-1";
    private const string Key = $"basket:{UserId}";

    private readonly IDatabase _redis = Substitute.For<IDatabase>();
    private readonly ServiceBusSender _sender = Substitute.For<ServiceBusSender>();
    private readonly BasketController _sut;

    public BasketControllerTests()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_redis);
        _sut = new BasketController(mux, _sender).WithUser(UserId);
    }

    private void StoreCart(params CartItem[] items) =>
        _redis.StringGetAsync((RedisKey)Key, Arg.Any<CommandFlags>())
            .Returns(JsonSerializer.Serialize(new ShoppingCart { UserId = UserId, Items = [.. items] }));

    [Fact]
    public async Task Get_returns_empty_cart_when_missing()
    {
        _redis.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        var cart = await _sut.Get();

        Assert.Equal(UserId, cart.UserId);
        Assert.Empty(cart.Items);
    }

    [Fact]
    public async Task Get_returns_stored_cart_with_total()
    {
        StoreCart(new(Guid.NewGuid(), "Shoe", 10, 2), new(Guid.NewGuid(), "Hat", 5, 1));

        var cart = await _sut.Get();

        Assert.Equal(25, cart.Total);
    }

    [Fact]
    public async Task Update_saves_cart_under_user_key()
    {
        var cart = await _sut.Update([new CartItem(Guid.NewGuid(), "Shoe", 10, 1)]);

        Assert.Single(cart.Items);
        await _redis.Received(1).StringSetAsync((RedisKey)Key, Arg.Any<RedisValue>(), TimeSpan.FromDays(7),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Checkout_empty_cart_returns_bad_request()
    {
        _redis.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);

        var result = await _sut.Checkout(new CheckoutRequest("Street 1"));

        Assert.IsType<BadRequestObjectResult>(result);
        await _sender.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default);
    }

    [Fact]
    public async Task Checkout_publishes_event_and_clears_cart()
    {
        var productId = Guid.NewGuid();
        StoreCart(new CartItem(productId, "Shoe", 10, 3));
        ServiceBusMessage? sent = null;
        await _sender.SendMessageAsync(Arg.Do<ServiceBusMessage>(m => sent = m), Arg.Any<CancellationToken>());

        var result = await _sut.Checkout(new CheckoutRequest("Street 1"));

        Assert.IsType<AcceptedResult>(result);
        var evt = JsonSerializer.Deserialize<BasketCheckoutEvent>(sent!.Body)!;
        Assert.Equal((UserId, "Street 1", 30m), (evt.UserId, evt.ShippingAddress, evt.Total));
        Assert.Equal(productId, Assert.Single(evt.Items).ProductId);
        Assert.Equal(evt.EventId.ToString(), sent.MessageId);
        await _redis.Received(1).KeyDeleteAsync((RedisKey)Key, Arg.Any<CommandFlags>());
    }
}
