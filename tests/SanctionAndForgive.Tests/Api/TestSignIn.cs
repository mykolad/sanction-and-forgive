using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SanctionAndForgive.Tests.Api;

/// <summary>A stand-in for Microsoft Entra ID: its metadata (issuer, sign-in address, signing key) and ID tokens
/// signed with a key made for this test run.</summary>
internal static class TestSignIn
{
    public const string ClientId = "test-client";

    public const string Authority = "https://login.test/tenant/v2.0";

    public const string AuthorizationEndpoint = "https://login.test/tenant/oauth2/v2.0/authorize";

    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test-key" };

    /// <summary>A key the issuer never published, under the same key id.</summary>
    public static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static IConfigurationManager<OpenIdConnectConfiguration> Metadata()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Authority, AuthorizationEndpoint = AuthorizationEndpoint };
        configuration.SigningKeys.Add(Key);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }

    /// <summary>An administrator's (or, without roles, a visitor's) ID token, valid for an hour from the test clock.</summary>
    public static string Token(string name, params string[] roles) =>
        Token(name, roles, nonce: null, ApiFactory.Now.AddHours(1), ClientId, Authority, Key);

    public static string Token(string name, string[] roles, string? nonce, DateTimeOffset expires, string audience, string issuer, RsaSecurityKey key)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal) { ["preferred_username"] = name, ["name"] = "Owner", ["roles"] = roles };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expires.AddHours(-1).UtcDateTime,
            NotBefore = expires.AddHours(-1).UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }
}
