using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DeckApi.Data;

public sealed class DeckDbContext(DbContextOptions<DeckDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<DeckUpvote> DeckUpvotes => Set<DeckUpvote>();
    public DbSet<LegacyPendingDelete> LegacyPendingDeletes => Set<LegacyPendingDelete>();
    public DbSet<CardCatalogSnapshot> CardCatalogSnapshots => Set<CardCatalogSnapshot>();
    public DbSet<DeckTournamentResult> DeckTournamentResults => Set<DeckTournamentResult>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.KeycloakId).IsUnique();
            e.Property(u => u.KeycloakId).HasMaxLength(255);
            e.Property(u => u.Email).HasMaxLength(255);
            e.Property(u => u.Username).HasMaxLength(100);
            e.Property(u => u.Locale).HasMaxLength(10);
        });

        b.Entity<Deck>(e =>
        {
            e.ToTable("decks");
            e.Property(d => d.Name).HasMaxLength(150);
            e.Property(d => d.Format).HasMaxLength(50);
            e.Property(d => d.AlteredId).HasMaxLength(255);
            e.Property(d => d.HeroReference).HasMaxLength(100);
            e.Property(d => d.HeroFaction).HasMaxLength(10);
            e.HasIndex(d => d.AlteredId).IsUnique();
            e.HasIndex(d => d.LegacyId).IsUnique();
            e.HasIndex(d => new { d.UserId, d.UpdatedAt });
            e.HasIndex(d => d.Format);
            e.HasIndex(d => new { d.IsPublic, d.Legal, d.CreatedAt });
            e.HasIndex(d => new { d.IsPublic, d.Legal, d.UpvoteCount });
            e.HasIndex(d => new { d.IsPublic, d.Legal, d.ViewCount });
            e.HasIndex(d => d.CardReferences).HasMethod("gin");
            e.HasIndex(d => d.CardFamilies).HasMethod("gin");
            e.HasIndex(d => d.ValidSeasons).HasMethod("gin");
            e.HasIndex(d => new { d.UserId, d.Source });
            e.HasIndex(d => new { d.Source, d.IsPublic, d.CreatedAt });
            e.HasMany(d => d.TournamentResults).WithOne().HasForeignKey(r => r.DeckId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);

            e.Property(d => d.ByRarity).HasJsonConversion();
            e.Property(d => d.ExtraCards).HasJsonConversion();
            e.Property(d => d.AltArtOverrides).HasJsonConversion();
            e.Property(d => d.ValidationErrors).HasJsonConversion();
            e.Property(d => d.LegalityDetail).HasJsonConversion();
        });

        b.Entity<DeckUpvote>(e =>
        {
            e.ToTable("deck_upvotes");
            e.HasIndex(u => new { u.DeckId, u.UserId }).IsUnique();
            e.HasIndex(u => u.UserId);
            e.HasOne<Deck>().WithMany().HasForeignKey(u => u.DeckId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LegacyPendingDelete>(e =>
        {
            e.ToTable("legacy_pending_deletes");
            e.HasKey(p => p.LegacyId);
            e.HasIndex(p => p.UserId);
        });

        b.Entity<DeckTournamentResult>(e =>
        {
            e.ToTable("deck_tournament_results");
            e.Property(r => r.TournamentName).HasMaxLength(255);
            e.Property(r => r.Mode).HasMaxLength(50);
            e.Property(r => r.BgaUserId).HasMaxLength(100);
            e.HasIndex(r => new { r.TournamentParentId, r.BgaUserId });
            e.HasIndex(r => r.DeckId);
        });

        b.Entity<SyncCursor>(e =>
        {
            e.ToTable("sync_cursors");
            e.HasKey(c => c.Name);
            e.Property(c => c.Name).HasMaxLength(100);
        });

        b.Entity<CardCatalogSnapshot>(e =>
        {
            e.ToTable("card_catalog_snapshots");
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Payload).HasColumnType("jsonb");
        });
    }
}

/// <summary>Used by <c>dotnet ef</c> (migrations, bundle). The bundle takes <c>--connection</c> at runtime.</summary>
public sealed class DeckDbContextDesignFactory : IDesignTimeDbContextFactory<DeckDbContext>
{
    public DeckDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DeckDbContext>()
            .UseNpgsql("Host=localhost;Database=deckapi;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}

internal static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Stores the property as <c>jsonb</c> through System.Text.Json, compared by serialized value.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property)
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<T>(v, Options)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                    v => JsonSerializer.Serialize(v, Options).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!))
            .HasColumnType("jsonb");
        return property;
    }
}
