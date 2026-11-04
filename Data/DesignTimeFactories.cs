using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyStatus.Data;

public sealed class SqlServerDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=MyStatusDesignOnly;Trusted_Connection=True").Options);
}

public sealed class LocalDesignFactory : IDesignTimeDbContextFactory<LocalDbContext>
{
    public LocalDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<LocalDbContext>()
        .UseSqlite("Data Source=.local/my-status.db").Options);
}
