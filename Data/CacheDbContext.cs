using Microsoft.EntityFrameworkCore;

namespace Diyokee.Data {
    public class CacheDbContext(IConfiguration configuration) : DbContext {
        protected readonly IConfiguration Configuration = configuration;

        public DbSet<DFile> Files { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            string connectionString = Configuration.GetConnectionString("CacheDB") ?? "Data Source=.\\Data\\cache.db";
            string[] tokens = connectionString.Split('=');
            tokens[1] = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, connectionString);
            optionsBuilder.UseSqlite(string.Join("=", tokens));
        }
    }
}
