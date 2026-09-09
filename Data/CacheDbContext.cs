using Microsoft.EntityFrameworkCore;

namespace Diyokee.Data {
    public class CacheDbContext(IConfiguration configuration) : DbContext {
        protected readonly IConfiguration Configuration = configuration;

        public DbSet<DFile> Files { get; set; }

        // The child tables are named explicitly. Left to convention EF names a table after the
        // entity, which gave the singular "CuePoint"/"BeatGridMarker" against a plural "Files", and
        // names the shadow foreign key after the principal TYPE, which gave "DFileId" - the "D"
        // being an artefact of the class name rather than anything meaningful in the schema.
        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DFile>()
                        .HasMany(f => f.CuePoints)
                        .WithOne()
                        .HasForeignKey("FileId");

            modelBuilder.Entity<DFile>()
                        .HasMany(f => f.BeatGridMarkers)
                        .WithOne()
                        .HasForeignKey("FileId");

            modelBuilder.Entity<DFile.CuePoint>().ToTable("CuePoints");
            modelBuilder.Entity<DFile.BeatGridMarker>().ToTable("BeatGridMarkers");
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dbFilePath = Path.Combine(baseDirectory, "Data", "cache.db");
            string connectionString = $"Data Source={dbFilePath}";
            optionsBuilder.UseSqlite(connectionString);
        }
    }
}
