using BuildingBlocks;
using Microsoft.AspNetCore.Builder;

namespace Services.Tests;

public class DevAuthTests
{
    private static WebApplicationBuilder Builder(string environment, bool devBypass)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration["Auth:DevBypass"] = devBypass.ToString();
        return builder;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void DevBypass_outside_Development_refuses_to_start(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Builder(environment, devBypass: true).AddServiceDefaults());

        Assert.Contains("Development", ex.Message);
    }

    [Fact]
    public void DevBypass_in_Development_is_allowed() =>
        Builder("Development", devBypass: true).AddServiceDefaults();

    [Fact]
    public void Production_without_DevBypass_is_allowed() =>
        Builder("Production", devBypass: false).AddServiceDefaults();
}
