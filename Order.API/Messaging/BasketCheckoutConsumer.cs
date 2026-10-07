using System.Text.Json;
using Azure.Messaging.ServiceBus;
using BuildingBlocks;
using Order.API.Data;

namespace Order.API.Messaging;

public class BasketCheckoutConsumer(ServiceBusClient client, IServiceScopeFactory scopes, IConfiguration config,
    ILogger<BasketCheckoutConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = client.CreateProcessor(config["ServiceBus:QueueName"],
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 4 });

        processor.ProcessMessageAsync += HandleAsync;
        processor.ProcessErrorAsync += e =>
        {
            logger.LogError(e.Exception, "Service Bus error on {Entity}", e.EntityPath);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        await processor.StopProcessingAsync();
    }

    internal async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var evt = JsonSerializer.Deserialize<BasketCheckoutEvent>(args.Message.Body)!;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        // EventId doubles as the order id, so redelivered messages are idempotent.
        if (await db.Orders.FindAsync([evt.EventId], args.CancellationToken) is null)
        {
            db.Orders.Add(new CustomerOrder
            {
                Id = evt.EventId,
                UserId = evt.UserId,
                UserName = evt.UserName,
                ShippingAddress = evt.ShippingAddress,
                Total = evt.Total,
                CreatedAt = evt.CreatedAt,
                Items = evt.Items.Select(i => new OrderItem
                {
                    ProductId = i.ProductId, ProductName = i.ProductName, Price = i.Price, Quantity = i.Quantity
                }).ToList()
            });
            await db.SaveChangesAsync(args.CancellationToken);
            logger.LogInformation("Order {OrderId} created for {UserId}", evt.EventId, evt.UserId);
        }

        await args.CompleteMessageAsync(args.Message);
    }
}
