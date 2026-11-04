using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class AccountProvisioner(AppDbContext db, UserManager<ApplicationUser> users, MicrosoftAccountOptions account)
{
    public async Task ProvisionAsync(string zone, bool linkExisting = false)
    {
        if (!account.HasOwner) throw new RuleException("Configure Microsoft:TenantId and Microsoft:OwnerObjectId first.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (zone.Length > 100) throw new RuleException("Time zone ID is too long.");
        // Linking preserves the local owner ID and all existing activities; never link by email.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var existing = await db.Users.ToListAsync();
        if (existing.Count > 1) throw new RuleException("Expected at most one local account. Resolve this administratively before linking.");
        if (existing.Count == 1 && !linkExisting)
            throw new RuleException("An account already exists. Explicitly use --link-microsoft to preserve and link its data.");
        if (existing.Count == 0 && linkExisting) throw new RuleException("No existing account to link. Use --provision.");
        var user = existing.SingleOrDefault() ?? new ApplicationUser
        {
            UserName = $"microsoft-{Guid.Parse(account.OwnerObjectId):N}", TimeZoneId = zone
        };
        if (existing.Count == 0) Check(await users.CreateAsync(user));
        var logins = await users.GetLoginsAsync(user);
        if (logins.Count > 0) throw new RuleException("This account is already linked. Changing its Microsoft identity requires a separately reviewed recovery procedure.");
        Check(await users.AddLoginAsync(user, new UserLoginInfo(MicrosoftAccountOptions.Scheme, account.ProviderKey, "Microsoft")));
        if (await users.HasPasswordAsync(user)) Check(await users.RemovePasswordAsync(user));
        Check(await users.UpdateSecurityStampAsync(user));
        await tx.CommitAsync();
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new RuleException(string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
