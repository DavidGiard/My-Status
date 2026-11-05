using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class AccountProvisioner(AppDbContext db, UserManager<ApplicationUser> users)
{
    public async Task ProvisionAsync(string username, string password, bool replaceExisting = false)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username) || username.Length > 100)
            throw new RuleException("Supply a username between 1 and 100 characters.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var existing = await db.Users.ToListAsync();
        if (existing.Count > 1) throw new RuleException("Expected at most one account. Resolve this administratively.");
        if (existing.Count == 1 && !replaceExisting)
            throw new RuleException("An account already exists. Use --set-password to preserve its data and replace its credentials.");
        if (existing.Count == 0 && replaceExisting) throw new RuleException("No account exists. Use --provision.");
        var user = existing.SingleOrDefault() ?? new ApplicationUser { UserName = username };
        if (existing.Count == 0) Check(await users.CreateAsync(user, password));
        else
        {
            Check(await users.SetUserNameAsync(user, username));
            if (await users.HasPasswordAsync(user)) Check(await users.RemovePasswordAsync(user));
            Check(await users.AddPasswordAsync(user, password));
            foreach (var login in await users.GetLoginsAsync(user))
                Check(await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey));
            Check(await users.SetLockoutEndDateAsync(user, null));
            Check(await users.ResetAccessFailedCountAsync(user));
        }
        Check(await users.UpdateSecurityStampAsync(user));
        await tx.CommitAsync();
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new RuleException(string.Join(" ", result.Errors.Select(x => x.Description)));
    }
}
