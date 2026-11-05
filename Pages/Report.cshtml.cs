using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Services;

namespace MyStatus.Pages;

public sealed class ReportModel(ReportService reports, AppDbContext db, TimeProvider clock, ILogger<ReportModel> logger) : OwnedPage
{
    [BindProperty(SupportsGet = true), Required] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true), Required] public DateOnly? To { get; set; }
    public StatusReport? Report { get; set; }
    public async Task OnGetAsync()
    {
        if (!Request.Query.ContainsKey(nameof(From)) && !Request.Query.ContainsKey(nameof(To)))
        {
            var zone = await db.Users.Where(x => x.Id == Owner).Select(x => x.TimeZoneId).SingleAsync();
            To = UserDates.Today(zone, clock);
            From = To.Value.AddDays(-6);
            ModelState.Clear();
        }
        if (!ModelState.IsValid) return;
        try { Report = await reports.GenerateAsync(Owner, From!.Value, To!.Value); }
        catch (RuleException ex) { ModelState.AddModelError("", ex.Message); }
        catch (Exception ex) when (ex is System.Data.Common.DbException or TimeoutException)
        {
            logger.LogError("Status report database query failed ({FailureType}).", ex.GetType().Name);
            ModelState.AddModelError("", "The report could not be loaded. Please try again; your activity flags have not been changed.");
        }
    }
}
