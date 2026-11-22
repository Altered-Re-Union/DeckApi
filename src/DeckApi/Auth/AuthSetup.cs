using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DeckApi.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DeckApi.Auth;

public static class AuthPolicies
{
    public const string User = "user";
    public const string ReadDecks = "read-decks";
    public const string WriteDecks = "write-decks";
    public const string Admin = "admin";
    public const string MaintenanceApiKey = "maintenance-api-key";
    public const string AdminClaim = "deckapi_admin";
}

public static class AuthSetup
{
    /// <summary>
    /// Keycloak bearer tokens (realm <c>players</c>), validated against the realm's JWKS (signature,
    /// lifetime, issuer). Like the old API: no audience check, any client of the realm is accepted.
    /// <c>Auth:DevSigningKey</c> (non-production) additionally accepts HS256 tokens with issuer <c>dev</c>.
    /// </summary>
    public static IServiceCollection AddDeckApiAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var keycloakBase = config["Keycloak:BaseUrl"]?.TrimEnd('/');
        var realm = config["Keycloak:Realm"] ?? "players";
        var devKey = env.IsProduction() ? null : config["Auth:DevSigningKey"];

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !env.IsDevelopment();
                var issuers = new List<string>();
                if (!string.IsNullOrEmpty(keycloakBase))
                {
                    options.Authority = $"{keycloakBase}/realms/{realm}";
                    issuers.Add(options.Authority);
                }
                if (!string.IsNullOrEmpty(devKey)) issuers.Add("dev");

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = false,
                    ValidateIssuer = true,
                    ValidIssuers = issuers,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };
                if (!string.IsNullOrEmpty(devKey))
                {
                    // Keep JWKS keys (resolved by the configuration manager) and add the dev key.
                    var symmetric = new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(devKey)));
                    options.TokenValidationParameters.IssuerSigningKeys = [symmetric];
                    if (string.IsNullOrEmpty(keycloakBase))
                        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                }

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async ctx =>
                    {
                        var users = ctx.HttpContext.RequestServices.GetRequiredService<UserProvisioning>();
                        var user = await users.UpsertAsync(ctx.Principal!, ctx.HttpContext.RequestAborted);
                        if (user.IsAdmin)
                            ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(AuthPolicies.AdminClaim, "true"));
                        ctx.HttpContext.Items[typeof(User)] = user;
                    },
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.User, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser())
            .AddPolicy(AuthPolicies.ReadDecks, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser()
                .RequireAssertion(ctx => DeckClaims.CanRead(ctx.User, config)))
            .AddPolicy(AuthPolicies.WriteDecks, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser()
                .RequireAssertion(ctx => DeckClaims.CanWrite(ctx.User, config)))
            .AddPolicy(AuthPolicies.Admin, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireClaim(AuthPolicies.AdminClaim, "true"))
            .AddPolicy(AuthPolicies.MaintenanceApiKey, p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());

        services.AddScoped<UserProvisioning>();
        return services;
    }

    /// <summary>Signs a dev token (tests / local runs with <c>Auth:DevSigningKey</c>).</summary>
    public static string CreateDevToken(string devKey, string sub, string? pseudo = null, string? email = null, TimeSpan? lifetime = null, string? scope = null)
    {
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var claims = new Dictionary<string, object> { ["sub"] = sub };
        if (scope is not null) claims["scope"] = scope;
        if (pseudo is not null) claims["pseudo"] = pseudo;
        if (email is not null) claims["email"] = email;
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "dev",
            Claims = claims,
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(SHA256.HashData(Encoding.UTF8.GetBytes(devKey))), SecurityAlgorithms.HmacSha256),
        });
    }
}

/// <summary>Creates/updates the local user row from the token, like the old KeycloakAuthenticator (but only writes on change).</summary>
public sealed class UserProvisioning(DeckDbContext db, TimeProvider clock, ILogger<UserProvisioning> logger)
{
    public async Task<User> UpsertAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var sub = principal.FindFirstValue("sub") ?? throw new SecurityTokenException("Token has no sub.");
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username");
        // The pseudo is public (owner.pseudo): only the `pseudo` claim, never preferred_username/name (can be an email).
        var username = principal.FindFirstValue("pseudo");
        var locale = principal.FindFirstValue("locale");
        if (username is { Length: > 100 }) username = username[..100];

        var user = await db.Users.FirstOrDefaultAsync(u => u.KeycloakId == sub, ct);
        var now = clock.GetUtcNow();
        if (user is null)
        {
            user = new User { KeycloakId = sub, Email = email, Username = username, Locale = locale, CreatedAt = now };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("auth.new_user {Sub}", sub);
            }
            catch (DbUpdateException)
            {
                // Concurrent first request of the same user.
                db.Entry(user).State = EntityState.Detached;
                user = await db.Users.FirstAsync(u => u.KeycloakId == sub, ct);
            }
            return user;
        }

        if (user.Email != email || user.Username != username || user.Locale != locale)
        {
            user.Email = email;
            user.Username = username;
            user.Locale = locale;
            user.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return user;
    }
}

public static class HttpContextUserExtensions
{
    public static User? CurrentUser(this HttpContext http) => http.Items.TryGetValue(typeof(User), out var u) ? u as User : null;

    public static string? BearerToken(this HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
    }
}

/// <summary>
/// Deck permissions are Keycloak client scopes, read from the access token's space-separated <c>scope</c>
/// claim — same convention as AlteredOwnership (<c>read-collection</c> / <c>write-collection</c>, see its
/// AuthExtensions.HasScope). Names: <c>Auth:ReadDecksScope</c> (default <c>read-decks</c>) and
/// <c>Auth:WriteDecksScope</c> (default <c>write-deck</c>). <c>Auth:RequireDeckScopes=false</c> disables the
/// check (e.g. while clients are being given the scopes).
/// </summary>
public static class DeckClaims
{
    public static bool CanRead(ClaimsPrincipal user, IConfiguration config) =>
        HasScope(user, config, config["Auth:ReadDecksScope"] ?? "read-decks");

    public static bool CanWrite(ClaimsPrincipal user, IConfiguration config) =>
        HasScope(user, config, config["Auth:WriteDecksScope"] ?? "write-deck");

    private static bool HasScope(ClaimsPrincipal user, IConfiguration config, string scope) =>
        !config.GetValue("Auth:RequireDeckScopes", true)
        || user.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope);
}
