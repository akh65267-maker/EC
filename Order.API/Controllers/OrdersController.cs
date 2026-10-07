using BuildingBlocks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Order.API.Data;

namespace Order.API.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController(OrderDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<CustomerOrder>> GetMine()
    {
        var userId = User.GetUserId();
        return await db.Orders.AsNoTracking().Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt).ToListAsync();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CustomerOrder>> Get(Guid id)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
        if (order is null) return NotFound();
        return order.UserId == User.GetUserId() || User.IsInRole(ServiceDefaults.AdminRole) ? order : Forbid();
    }

    [HttpGet("all"), Authorize(Policy = ServiceDefaults.AdminPolicy)]
    public async Task<IEnumerable<CustomerOrder>> GetAll(int page = 0, int size = 50) =>
        await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).Skip(page * size).Take(size).ToListAsync();
}
