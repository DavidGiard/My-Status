using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using MyStatus.Services;

namespace MyStatus.Tests;

// A test-only token endpoint: real OIDC code/state/nonce/signature processing, no Microsoft network calls.
public sealed class TestMicrosoftProvider : HttpMessageHandler
{
    public const string Tenant = "11111111-1111-1111-1111-111111111111";
    public const string ObjectId = "22222222-2222-2222-2222-222222222222";
    public const string ClientId = "33333333-3333-3333-3333-333333333333";
    public const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private readonly RSA rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, string> nonces = new();
    public string TokenTenant { get; set; } = Tenant;
    public string TokenObject { get; set; } = ObjectId;
    public string TokenIssuer { get; set; } = Issuer;
    public string TokenAudience { get; set; } = ClientId;
    public bool Expired { get; set; }
    public bool InvalidNonce { get; set; }
    public bool InvalidSignature { get; set; }
    public bool IncludeObject { get; set; } = true;
    public static MicrosoftAccountOptions Account() => new()
    {
        TenantId = Tenant, OwnerObjectId = ObjectId, ClientId = ClientId, ClientSecret = "synthetic-test-secret"
    };
    public void Configure(OpenIdConnectOptions options)
    {
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        options.Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
            TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
        };
        options.Configuration.SigningKeys.Add(key);
        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
        options.Backchannel = new HttpClient(this, disposeHandler: false);
    }
    public string Callback(string authorizationUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(authorizationUrl).Query);
        var code = Guid.NewGuid().ToString("N");
        nonces[code] = query["nonce"].ToString();
        return QueryHelpers.AddQueryString(query["redirect_uri"].ToString(), new Dictionary<string, string?>
        { ["code"] = code, ["state"] = query["state"].ToString() });
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!nonces.TryRemove(form["code"].ToString(), out var nonce)) return new(HttpStatusCode.BadRequest);
            var claims = new List<Claim>
            {
                new("sub", "test-subject"), new("tid", TokenTenant), new("name", "Synthetic owner"),
                new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                new("preferred_username", "same-email@example.invalid"),
                new("nonce", InvalidNonce ? "incorrect-nonce" : nonce)
            };
            if (IncludeObject) claims.Add(new("oid", TokenObject));
            var signingKey = InvalidSignature ? new RsaSecurityKey(RSA.Create(2048)) : new RsaSecurityKey(rsa) { KeyId = "test-key" };
            var token = new JwtSecurityToken(TokenIssuer, TokenAudience, claims,
                DateTime.UtcNow.AddHours(-2), Expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id_token = new JwtSecurityTokenHandler().WriteToken(token),
                    access_token = "test-only-unused-access-token", token_type = "Bearer", expires_in = 600
                }), Encoding.UTF8, "application/json")
            };
        }
        throw new InvalidOperationException("Unexpected Microsoft network request during test.");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) rsa.Dispose();
        base.Dispose(disposing);
    }
}
