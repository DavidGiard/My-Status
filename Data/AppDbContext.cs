using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MyStatus.Domain;

namespace MyStatus.Data;

public class AppDbContext(DbContextOptions options)
    : IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ActivityTag> ActivityTags => Set<ActivityTag>();
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        var length = Database.IsSqlServer() ? "LEN" : "length";
        b.Entity<ApplicationUser>().Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        b.Entity<Category>(e =>
        {
            e.HasAlternateKey(x => new { x.Id, x.OwnerId });
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.ParentScope, x.NormalizedName }).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => new { x.ParentId, x.OwnerId })
                .HasPrincipalKey(x => new { x.Id, x.OwnerId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable("Categories", t =>
            {
                t.HasTrigger("TR_Categories_Hierarchy_Insert");
                t.HasTrigger("TR_Categories_Hierarchy_Update");
                t.HasCheckConstraint("CK_Category_Scope", "(ParentId IS NULL AND ParentScope = 0) OR (ParentId IS NOT NULL AND ParentScope = ParentId AND ParentId <> Id)");
                t.HasCheckConstraint("CK_Category_Name", $"{length}(trim(Name)) BETWEEN 1 AND 100 AND {length}(Name) <= 100 AND {length}(NormalizedName) BETWEEN 1 AND 100");
            });
        });
        b.Entity<Tag>(e =>
        {
            e.HasAlternateKey(x => new { x.Id, x.OwnerId });
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.NormalizedName }).IsUnique();
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable("Tags", t => t.HasCheckConstraint("CK_Tag_Name", $"{length}(trim(Name)) BETWEEN 1 AND 100 AND {length}(Name) <= 100 AND {length}(NormalizedName) BETWEEN 1 AND 100"));
        });
        b.Entity<Activity>(e =>
        {
            e.HasAlternateKey(x => new { x.Id, x.OwnerId });
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => new { x.CategoryId, x.OwnerId })
                .HasPrincipalKey(x => new { x.Id, x.OwnerId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.Date });
            e.HasIndex(x => new { x.OwnerId, x.Status, x.IncludeInReport, x.CompletionDate });
            e.ToTable("Activities", t =>
            {
                t.HasCheckConstraint("CK_Activity_Status", "Status BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_Activity_Completion",
                    "(Status = 2 AND CompletionDate IS NOT NULL) OR (Status <> 2 AND CompletionDate IS NULL)");
                t.HasCheckConstraint("CK_Activity_Description", $"{length}(trim(Description)) BETWEEN 1 AND 2000 AND {length}(Description) <= 2000");
            });
        });
        b.Entity<ActivityTag>(e =>
        {
            e.HasKey(x => new { x.ActivityId, x.TagId });
            e.Property(x => x.OwnerId).HasMaxLength(450);
            e.HasOne(x => x.Activity).WithMany(x => x.ActivityTags)
                .HasForeignKey(x => new { x.ActivityId, x.OwnerId })
                .HasPrincipalKey(x => new { x.Id, x.OwnerId }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Tag).WithMany().HasForeignKey(x => new { x.TagId, x.OwnerId })
                .HasPrincipalKey(x => new { x.Id, x.OwnerId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class LocalDbContext(DbContextOptions<LocalDbContext> options) : AppDbContext(options);
