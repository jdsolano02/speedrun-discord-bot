using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

// Database context for Entity Framework Core using SQLite.
public class SpeedrunContext : DbContext
{
    public DbSet<RunRecord> Runs { get; set; }
    public DbSet<TrackedGame> TrackedGames { get; set; }
    public DbSet<GuildConfig> GuildConfigs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Path to the SQLite database file inside the persistent volume.
        optionsBuilder.UseSqlite("Data Source=data/speedrundb.sqlite");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Primary Key definitions.
        modelBuilder.Entity<RunRecord>().HasKey(r => r.Id);
        modelBuilder.Entity<TrackedGame>().HasKey(g => g.GameId);
        modelBuilder.Entity<GuildConfig>().HasKey(gc => gc.GuildId);

        // Unique index for RunLink to ensure fast duplicate detection.
        modelBuilder.Entity<RunRecord>()
            .HasIndex(r => r.RunLink)
            .IsUnique();

        //Composite index for performance optimization on ranking and activity queries.
        modelBuilder.Entity<RunRecord>()
            .HasIndex(r => new { r.GameFullName, r.CategoryName, r.TimeInSeconds, r.DateSubmitted });
    }
}

