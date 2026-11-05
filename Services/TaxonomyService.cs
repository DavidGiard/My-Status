using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MyStatus.Data;
using MyStatus.Domain;

namespace MyStatus.Services;

public sealed class TaxonomyService(AppDbContext db)
{
    public static string Normalize(string name) => name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static string ValidateName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 100 || Normalize(trimmed).Length > 100)
            throw new RuleException("Names must contain 1 to 100 characters.");
        return trimmed;
    }

    public async Task SaveCategoryAsync(string owner, int? id, string name, int? parentId, Guid version)
    {
        name = ValidateName(name);
        var normalized = Normalize(name);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await db.Categories.AnyAsync(x => x.OwnerId == owner && x.Id != id && x.ParentId == parentId && x.NormalizedName == normalized))
            throw new RuleException("A category with this name already exists under this parent.");
        var category = id.HasValue
            ? await db.Categories.SingleOrDefaultAsync(x => x.OwnerId == owner && x.Id == id)
                ?? throw new RuleException("Category not found.")
            : new Category { OwnerId = owner };
        if (parentId.HasValue)
        {
            var parent = await db.Categories.SingleOrDefaultAsync(x => x.OwnerId == owner && x.Id == parentId);
            if (parent is null || parent.ParentId.HasValue || parentId == id)
                throw new RuleException("Choose a top-level category other than this category.");
            if (id.HasValue && await db.Categories.AnyAsync(x => x.OwnerId == owner && x.ParentId == id))
                throw new RuleException("A category with children cannot become a subcategory.");
        }
        if (id.HasValue)
        {
            if (category.Version != version) throw new DbUpdateConcurrencyException();
            db.Entry(category).Property(x => x.Version).OriginalValue = version;
        }
        category.Name = name;
        category.NormalizedName = normalized;
        category.ParentId = parentId;
        category.ParentScope = parentId ?? 0;
        category.Version = Guid.NewGuid();
        if (!id.HasValue) db.Categories.Add(category);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task DeleteCategoryAsync(string owner, int id, Guid version)
    {
        var category = await db.Categories.SingleOrDefaultAsync(x => x.OwnerId == owner && x.Id == id)
            ?? throw new RuleException("Category not found.");
        if (await db.Activities.AnyAsync(x => x.OwnerId == owner && x.CategoryId == id)
            || await db.Categories.AnyAsync(x => x.OwnerId == owner && x.ParentId == id))
            throw new RuleException("Reassign activities and remove or move children before deleting this category.");
        db.Entry(category).Property(x => x.Version).OriginalValue = version;
        db.Categories.Remove(category);
        await db.SaveChangesAsync();
    }

    public async Task SaveTagAsync(string owner, int? id, string name, Guid version)
    {
        name = ValidateName(name);
        var normalized = Normalize(name);
        if (await db.Tags.AnyAsync(x => x.OwnerId == owner && x.Id != id && x.NormalizedName == normalized))
            throw new RuleException("A tag with this name already exists.");
        var tag = id.HasValue
            ? await db.Tags.SingleOrDefaultAsync(x => x.OwnerId == owner && x.Id == id)
                ?? throw new RuleException("Tag not found.")
            : new Tag { OwnerId = owner };
        if (id.HasValue)
        {
            if (tag.Version != version) throw new DbUpdateConcurrencyException();
            db.Entry(tag).Property(x => x.Version).OriginalValue = version;
        }
        tag.Name = name;
        tag.NormalizedName = normalized;
        tag.Version = Guid.NewGuid();
        if (!id.HasValue) db.Tags.Add(tag);
        await db.SaveChangesAsync();
    }

    public async Task DeleteTagAsync(string owner, int id, Guid version)
    {
        var tag = await db.Tags.SingleOrDefaultAsync(x => x.OwnerId == owner && x.Id == id)
            ?? throw new RuleException("Tag not found.");
        db.Entry(tag).Property(x => x.Version).OriginalValue = version;
        db.Tags.Remove(tag);
        await db.SaveChangesAsync();
    }
}
