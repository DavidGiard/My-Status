using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class MicrosoftAccountOptions
{
    public const string Scheme = "MicrosoftEntra";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string OwnerObjectId { get; set; } = "";
    public bool HasOwner => Guid.TryParse(TenantId, out var tenant) && tenant != Guid.Empty
        && Guid.TryParse(OwnerObjectId, out var owner) && owner != Guid.Empty;
    public bool IsConfigured => HasOwner && Guid.TryParse(ClientId, out var client) && client != Guid.Empty
        && !string.IsNullOrWhiteSpace(ClientSecret);
    public string ProviderKey => HasOwner
        ? $"{Guid.Parse(TenantId):D}:{Guid.Parse(OwnerObjectId):D}"
        : throw new InvalidOperationException("Configure Microsoft:TenantId and Microsoft:OwnerObjectId as nonempty GUIDs.");
}

public static class MicrosoftAuthentication
{
    public const string TenantClaim = "my-status:tenant";
    public const string ObjectClaim = "my-status:object";

    public static bool IsOwner(ClaimsPrincipal principal, MicrosoftAccountOptions options) =>
        options.HasOwner && Matches(principal, "tid", options.TenantId) && Matches(principal, "oid", options.OwnerObjectId);

    private static bool Matches(ClaimsPrincipal principal, string type, string expected)
    {
        var values = principal.FindAll(type).ToArray();
        return values.Length == 1 && Guid.TryParse(values[0].Value, out var actual) && actual == Guid.Parse(expected);
    }

    public static void Configure(OpenIdConnectOptions oidc, MicrosoftAccountOptions account)
    {
        var tenant = account.HasOwner ? Guid.Parse(account.TenantId) : Guid.Empty;
        oidc.Authority = $"https://login.microsoftonline.com/{tenant:D}/v2.0";
        oidc.ClientId = account.IsConfigured ? account.ClientId : "not-configured";
        oidc.ClientSecret = account.ClientSecret;
        oidc.SignInScheme = IdentityConstants.ApplicationScheme;
        oidc.CallbackPath = "/signin-oidc";
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.Prompt = "select_account";
        oidc.MapInboundClaims = false;
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.TokenValidationParameters.ValidateIssuer = true;
        oidc.TokenValidationParameters.ValidIssuer = oidc.Authority;
        oidc.TokenValidationParameters.ValidateAudience = true;
        oidc.TokenValidationParameters.ValidAudience = account.ClientId;
        oidc.TokenValidationParameters.ValidateLifetime = true;
        oidc.Events.OnMessageReceived = context =>
        {
            if (!account.IsConfigured)
            {
                context.HandleResponse();
                context.Response.Redirect("/Account/Login?signInFailed=true");
            }
            return Task.CompletedTask;
        };
        oidc.Events.OnTokenValidated = async context =>
        {
            if (context.Principal is null || !IsOwner(context.Principal, account))
            {
                context.Fail("The Microsoft identity is not authorized.");
                return;
            }
            var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByLoginAsync(MicrosoftAccountOptions.Scheme, account.ProviderKey);
            if (user is null)
            {
                context.Fail("The Microsoft identity has not been linked by the operator.");
                return;
            }
            var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
            var principal = await signIn.CreateUserPrincipalAsync(user);
            var identity = (ClaimsIdentity)principal.Identity!;
            identity.AddClaim(new Claim(TenantClaim, Guid.Parse(account.TenantId).ToString("D")));
            identity.AddClaim(new Claim(ObjectClaim, Guid.Parse(account.OwnerObjectId).ToString("D")));
            context.Principal = principal;
        };
        oidc.Events.OnRemoteFailure = context =>
        {
            // Provider exception text can contain codes or tokens. Log only the failure category.
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MicrosoftSignIn").LogWarning(new EventId(1101, "MicrosoftSignInFailure"),
                    "Microsoft sign-in failed ({FailureType}).", context.Failure?.GetType().Name ?? "Unknown");
            context.HandleResponse();
            context.Response.Redirect("/Account/Login?signInFailed=true");
            return Task.CompletedTask;
        };
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal is null) return;
        var options = context.HttpContext.RequestServices.GetRequiredService<MicrosoftAccountOptions>();
        var principal = context.Principal;
        var valid = options.IsConfigured && Matches(principal, TenantClaim, options.TenantId)
            && Matches(principal, ObjectClaim, options.OwnerObjectId);
        if (valid)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            valid = await db.UserLogins.AnyAsync(x => x.UserId == id
                && x.LoginProvider == MicrosoftAccountOptions.Scheme && x.ProviderKey == options.ProviderKey);
        }
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }
}
