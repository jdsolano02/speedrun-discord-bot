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
        // Esto crear� un archivo llamado "speedrundb.sqlite" en la ra�z del Worker
        optionsBuilder.UseSqlite("Data Source=data/speedrundb.sqlite");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunRecord>().HasKey(r => r.Id);
        modelBuilder.Entity<TrackedGame>().HasKey(g => g.GameId);
        modelBuilder.Entity<GuildConfig>().HasKey(gc => gc.GuildId);

        // �ndice para b�squedas r�pidas de rankings
        modelBuilder.Entity<RunRecord>()
            .HasIndex(r => new { r.GameFullName, r.CategoryName, r.TimeInSeconds });
    }
}