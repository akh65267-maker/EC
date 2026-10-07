using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuildingBlocks;

/// <summary>
/// Local-development-only stand-in for Entra ID: the caller is taken from the X-Dev-User / X-Dev-Roles headers.
/// Requests without X-Dev-User fall through to normal JWT validation.
/// Only registered when the environment is Development and Auth:DevBypass is true (see ServiceDefaults).
/// </summary>
public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevBypass";
    public const string UserHeader = "X-Dev-User";
    public const string RolesHeader = "X-Dev-Roles";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
            return await Context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        var claims = new List<Claim> { new("oid", user!), new("name", user!) };
        claims.AddRange(Request.Headers[RolesHeader].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new Claim("roles", r)));

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
