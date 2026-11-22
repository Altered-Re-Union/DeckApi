using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DeckApi.Auth;

/// <summary>
/// Machine-to-machine key for maintenance endpoints (deck revalidation), sent as <c>X-Api-Key</c>.
/// Compared in constant time; fails closed when <c>ApiKeys:Maintenance</c> is not configured.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Matches(presented, config["ApiKeys:Maintenance"]))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "maintenance")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    public static bool Matches(string? presented, string? configured)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(configured)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured));
    }
}
