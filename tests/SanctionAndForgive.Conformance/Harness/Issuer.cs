using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SanctionAndForgive.Conformance.Harness;

/// <summary>A stand-in for Microsoft Entra ID, for the admin sign-in (docs/api.md, "Admin"): an OpenID Connect issuer
/// that publishes its metadata and one signing key, made for this run (no key in the repository), and signs ID
/// tokens. The implementation is started with <c>Admin__ClientId</c> = <see cref="ClientId"/> and
/// <c>Admin__Authority</c> = <see cref="Authority"/> (tests/conformance/run.ps1); this process serves it on
/// <c>CONFORMANCE_ISSUER_BIND</c> (127.0.0.1 by default) at the authority's port.</summary>
public static class Issuer
{
    public const string ClientId = "conformance-admin";

    public const string KeyId = "conformance-1";

    /// <summary>The issuer's signing key, made for this run.</summary>
    public static RSA SigningKey { get; } = RSA.Create(2048);

    private static readonly Lazy<TcpListener> Listener = new(Start);

    /// <summary>The issuer as the implementation reaches it, e.g. <c>http://host.docker.internal:18090/conformance/v2.0</c>.</summary>
    public static string Authority => Environment.GetEnvironmentVariable("CONFORMANCE_ISSUER") is { Length: > 0 } issuer
        ? issuer.TrimEnd('/')
        : "http://localhost:18090/conformance/v2.0";

    public static string AuthorizationEndpoint => Authority + "/authorize";

    /// <summary>Starts serving the metadata (once); every test that signs in calls it first.</summary>
    public static void EnsureStarted() => _ = Listener.Value;

    /// <summary>An ID token as the issuer would sign it: for <paramref name="name"/>, with the administrator role or
    /// without, valid for an hour.</summary>
    public static string Token(string name, bool admin) =>
        Token(name, admin, nonce: null, DateTimeOffset.UtcNow.AddHours(1), ClientId, Authority, SigningKey);

    public static string Token(string name, bool admin, string? nonce, DateTimeOffset expires, string audience, string issuer, RSA key)
    {
        EnsureStarted();
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["iat"] = expires.AddHours(-1).ToUnixTimeSeconds(),
            ["nbf"] = expires.AddHours(-1).ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["preferred_username"] = name,
            ["name"] = "Conformance",
            ["roles"] = admin ? new[] { "Lists.Admin" } : Array.Empty<string>(),
        };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = KeyId, typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TcpListener Start()
    {
        var bind = Environment.GetEnvironmentVariable("CONFORMANCE_ISSUER_BIND") is { Length: > 0 } address ? IPAddress.Parse(address) : IPAddress.Loopback;
        var listener = new TcpListener(bind, new Uri(Authority).Port);
        listener.Start();
        _ = Task.Run(() => ServeAsync(listener));
        return listener;
    }

    private static async Task ServeAsync(TcpListener listener)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    await AnswerAsync(client);
                }
            });
        }
    }

    /// <summary>One GET, one answer, then the connection closes: the metadata and the key set.</summary>
    private static async Task AnswerAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? string.Empty;
        while (await reader.ReadLineAsync() is { Length: > 0 })
        {
            // The headers don't matter.
        }

        var path = requestLine.Split(' ') is [_, var target, ..] ? target.Split('?')[0] : string.Empty;
        var authorityPath = new Uri(Authority).AbsolutePath.TrimEnd('/');
        var parameters = SigningKey.ExportParameters(includePrivateParameters: false);
        object? body = path switch
        {
            _ when path == authorityPath + "/.well-known/openid-configuration" => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issuer"] = Authority,
                ["authorization_endpoint"] = AuthorizationEndpoint,
                ["jwks_uri"] = Authority + "/keys",
                ["response_types_supported"] = new[] { "id_token" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            },
            _ when path == authorityPath + "/keys" => new
            {
                keys = new[] { new { kty = "RSA", use = "sig", kid = KeyId, alg = "RS256", n = Base64Url(parameters.Modulus!), e = Base64Url(parameters.Exponent!) } },
            },
            _ => null,
        };
        var content = body is null ? "{}"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(body is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }
}
