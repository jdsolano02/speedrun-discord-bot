using Microsoft.EntityFrameworkCore;
using SpeedrunBot.Domain.Entities;

namespace SpeedrunBot.Infrastructure.Persistence;

public class SpeedrunContext : DbContext
{
    public DbSet<RunRecord> Runs { get; set; }
    public DbSet<TrackedGame> TrackedGames { get; set; }
    public DbSet<GuildConfig> GuildConfigs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Esto creará un archivo llamado "speedrundb.sqlite" en la raíz del Worker
        optionsBuilder.UseSqlite("Data Source=speedrundb.sqlite");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>().HasKey(r => r.Id);
        modelBuilder.Entity<TrackedGame>().HasKey(g => g.GameId);
        modelBuilder.Entity<GuildConfig>().HasKey(gc => gc.GuildId);

        // Índice para búsquedas rápidas de rankings
        modelBuilder.Entity<RunRecord>()
            .HasIndex(r => new { r.GameFullName, r.CategoryName, r.TimeInSeconds });
    }
}