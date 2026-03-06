using Microsoft.EntityFrameworkCore;

namespace Diyokee.Data {
    public class CacheDbContext(IConfiguration configuration) : DbContext {
        protected readonly IConfiguration Configuration = configuration;

        public DbSet<DFile> Files { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dbFilePath = Path.Combine(baseDirectory, "Data", "cache.db");
            string connectionString = $"Data Source={dbFilePath}";
            optionsBuilder.UseSqlite(connectionString);
        }
    }
}
