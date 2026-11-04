using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class AccountProvisioner(AppDbContext db, UserManager<ApplicationUser> users)
{
    public async Task ProvisionAsync(string email, string password, string zone)
    {
        if (!new EmailAddressAttribute().IsValid(email) || email.Length > 256)
            throw new RuleException("Supply a valid email address (maximum 256 characters).");
        _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
        if (zone.Length > 100) throw new RuleException("Time zone ID is too long.");
        // Serializable isolation ensures concurrent administrative setup cannot provision a second account.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.Users.AnyAsync()) throw new RuleException("An account already exists. Provisioning is disabled.");
        var result = await users.CreateAsync(new ApplicationUser
        {
            UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true, TimeZoneId = zone
        }, password);
        if (!result.Succeeded) throw new RuleException(string.Join(" ", result.Errors.Select(x => x.Description)));
        await tx.CommitAsync();
    }
}
