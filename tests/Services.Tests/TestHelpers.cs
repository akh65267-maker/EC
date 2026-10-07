using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Services.Tests;

public static class TestHelpers
{
    public static T NewDb<T>(Func<DbContextOptions<T>, T> create) where T : DbContext =>
        create(new DbContextOptionsBuilder<T>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public static T WithUser<T>(this T controller, string userId, params string[] roles) where T : ControllerBase
    {
        var claims = new List<Claim> { new("oid", userId), new("name", $"user-{userId}") };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "name", "roles"))
            }
        };
        return controller;
    }
}
