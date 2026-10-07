using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BuildingBlocks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Order.API.Controllers;
using Order.API.Data;
using Order.API.Messaging;

namespace Services.Tests.Order;

public class OrdersControllerTests
{
    private readonly OrderDbContext _db = TestHelpers.NewDb<OrderDbContext>(o => new(o));

    private async Task<CustomerOrder> Seed(string userId, DateTimeOffset? createdAt = null)
    {
        var o = new CustomerOrder
        {
            Id = Guid.NewGuid(), UserId = userId, ShippingAddress = "A", CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
        _db.Orders.Add(o);
        await _db.SaveChangesAsync();
        return o;
    }

    [Fact]
    public async Task GetMine_returns_only_callers_orders_newest_first()
    {
        var older = await Seed("u1", DateTimeOffset.UtcNow.AddDays(-1));
        var newer = await Seed("u1");
        await Seed("u2");

        var result = await new OrdersController(_db).WithUser("u1").GetMine();

        Assert.Equal([newer.Id, older.Id], result.Select(o => o.Id));
    }

    [Fact]
    public async Task Get_other_users_order_is_forbidden()
    {
        var o = await Seed("u2");

        var result = await new OrdersController(_db).WithUser("u1").Get(o.Id);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Admin_can_get_any_order()
    {
        var o = await Seed("u2");

        var result = await new OrdersController(_db).WithUser("admin", ServiceDefaults.AdminRole).Get(o.Id);

        Assert.Equal(o.Id, result.Value!.Id);
    }

    [Fact]
    public async Task Get_unknown_order_returns_not_found() =>
        Assert.IsType<NotFoundResult>((await new OrdersController(_db).WithUser("u1").Get(Guid.NewGuid())).Result);
}

public class BasketCheckoutConsumerTests
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceBusReceiver _receiver = Substitute.For<ServiceBusReceiver>();
    private readonly BasketCheckoutConsumer _sut;
    private readonly ServiceProvider _services;

    public BasketCheckoutConsumerTests()
    {
        _services = new ServiceCollection()
            .AddDbContext<OrderDbContext>(o => Microsoft.EntityFrameworkCore.InMemoryDbContextOptionsExtensions
                .UseInMemoryDatabase(o, _dbName))
            .BuildServiceProvider();
        _sut = new BasketCheckoutConsumer(Substitute.For<ServiceBusClient>(),
            _services.GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(),
            NullLogger<BasketCheckoutConsumer>.Instance);
    }

    private ProcessMessageEventArgs Message(BasketCheckoutEvent evt) =>
        new(ServiceBusModelFactory.ServiceBusReceivedMessage(new BinaryData(JsonSerializer.Serialize(evt))),
            _receiver, CancellationToken.None);

    private static BasketCheckoutEvent NewEvent() => new(Guid.NewGuid(), "u1", "User", "Street 1", 20,
        [new BasketCheckoutItem(Guid.NewGuid(), "Shoe", 10, 2)], DateTimeOffset.UtcNow);

    private List<CustomerOrder> Orders()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.ToList();
    }

    [Fact]
    public async Task Creates_order_and_completes_message()
    {
        var evt = NewEvent();
        var args = Message(evt);

        await _sut.HandleAsync(args);

        var order = Assert.Single(Orders());
        Assert.Equal((evt.EventId, "u1", 20m, "Pending"), (order.Id, order.UserId, order.Total, order.Status));
        Assert.Equal("Shoe", Assert.Single(order.Items).ProductName);
        await _receiver.Received(1).CompleteMessageAsync(args.Message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Duplicate_message_does_not_create_second_order()
    {
        var evt = NewEvent();

        await _sut.HandleAsync(Message(evt));
        await _sut.HandleAsync(Message(evt));

        Assert.Single(Orders());
        await _receiver.Received(2).CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>());
    }
}
