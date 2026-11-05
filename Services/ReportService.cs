using System.Text;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed record ReportItem(DateOnly Completed, string Description);
public sealed record ReportGroup(string Category, string? Subcategory, IReadOnlyList<ReportItem> Items);
public sealed record StatusReport(DateOnly From, DateOnly To, IReadOnlyList<ReportGroup> Groups)
{
    public string ToPlainText()
    {
        var text = new StringBuilder($"Status report: {From:yyyy-MM-dd} to {To:yyyy-MM-dd}\n");
        if (Groups.Count == 0) text.AppendLine("\nNo completed activities selected for this period.");
        foreach (var group in Groups)
        {
            text.AppendLine().AppendLine(group.Category + (group.Subcategory is null ? "" : " / " + group.Subcategory));
            foreach (var item in group.Items)
                text.AppendLine($"- {item.Completed:yyyy-MM-dd}: {item.Description.Replace("\r", "").Replace("\n", "\n  ")}");
        }
        return text.ToString();
    }
}

public sealed class ReportService(AppDbContext db)
{
    public async Task<StatusReport> GenerateAsync(string owner, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (from > to) throw new RuleException("The start date must be on or before the end date.");
        var rows = await db.Activities.AsNoTracking()
            .Where(x => x.OwnerId == owner && x.IncludeInReport && x.Status == ActivityStatus.Complete
                && x.CompletionDate >= from && x.CompletionDate <= to)
            .Select(x => new
            {
                x.Id, x.Description, x.CompletionDate,
                Category = x.Category.Parent == null ? x.Category.Name : x.Category.Parent.Name,
                Subcategory = x.Category.Parent == null ? null : x.Category.Name
            }).ToListAsync(ct);
        var groups = rows.GroupBy(x => new { x.Category, x.Subcategory })
            .OrderBy(x => x.Key.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Category, StringComparer.Ordinal)
            .ThenBy(x => x.Key.Subcategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key.Subcategory, StringComparer.Ordinal)
            .Select(g => new ReportGroup(g.Key.Category, g.Key.Subcategory,
                g.OrderBy(x => x.CompletionDate).ThenBy(x => x.Description, StringComparer.Ordinal)
                    .ThenBy(x => x.Id).Select(x => new ReportItem(x.CompletionDate!.Value, x.Description)).ToList()))
            .ToList();
        return new StatusReport(from, to, groups);
    }
}
