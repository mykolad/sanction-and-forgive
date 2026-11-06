using Microsoft.EntityFrameworkCore;

namespace SanctionAndForgive.Data;

/// <summary>The lists, and nothing a user enters. Rows are kept narrow: Basic allows 2 GB, and every list keeps two
/// imports. Normalised and transliterated forms are computed when the index is built, never stored.</summary>
public sealed class ListsDbContext(DbContextOptions<ListsDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();

    public DbSet<Import> Imports => Set<Import>();

    public DbSet<Subject> Subjects => Set<Subject>();

    public DbSet<SubjectName> SubjectNames => Set<SubjectName>();

    public DbSet<SubjectAddress> SubjectAddresses => Set<SubjectAddress>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Source>(source =>
        {
            source.Property(s => s.Code).HasMaxLength(32).IsUnicode(false);
            source.HasIndex(s => s.Code).IsUnique();
            source.Property(s => s.Name).HasMaxLength(200);
            source.Property(s => s.Url).HasMaxLength(1000);
            source.HasMany(s => s.Imports).WithOne(i => i.Source).HasForeignKey(i => i.SourceId).OnDelete(DeleteBehavior.Cascade);
            source.HasOne(s => s.ActiveImport).WithMany().HasForeignKey(s => s.ActiveImportId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Import>(import =>
        {
            import.Property(i => i.ContentHash).HasMaxLength(64).IsUnicode(false);
            import.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            import.Property(i => i.Message).HasMaxLength(1000);
            import.Property(i => i.ReviewedBy).HasMaxLength(256);
            import.HasIndex(i => new { i.SourceId, i.Status });
            import.HasMany<Subject>().WithOne().HasForeignKey(s => s.ImportId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subject>(subject =>
        {
            subject.Property(s => s.ExternalId).HasMaxLength(200);
            subject.Property(s => s.PrimaryName).HasMaxLength(500);
            subject.Property(s => s.Programmes).HasMaxLength(1000);
            subject.Property(s => s.SourceUrl).HasMaxLength(1000);
            subject.HasIndex(s => new { s.ImportId, s.ExternalId });
            subject.HasMany(s => s.Names).WithOne().HasForeignKey(n => n.SubjectId).OnDelete(DeleteBehavior.Cascade);
            subject.HasMany(s => s.Addresses).WithOne().HasForeignKey(a => a.SubjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SubjectName>(name =>
        {
            name.Property(n => n.FullName).HasMaxLength(500);
            name.HasIndex(n => n.SubjectId);
        });

        modelBuilder.Entity<SubjectAddress>(address =>
        {
            address.Property(a => a.Raw).HasMaxLength(1000);
            address.Property(a => a.CountryCode).HasMaxLength(2).IsUnicode(false).IsFixedLength();
            address.HasIndex(a => a.SubjectId);
        });
    }
}
