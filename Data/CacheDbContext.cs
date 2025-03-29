using Microsoft.EntityFrameworkCore;
using Diyokee.Components;

namespace Diyokee.Data {
    public class CacheDbContext : DbContext {
        protected readonly IConfiguration Configuration;

        public DbSet<FilesUI.File> Files { get; set; }

        public CacheDbContext(IConfiguration configuration) => Configuration = configuration;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) {
            optionsBuilder.UseSqlite(Configuration.GetConnectionString("CacheDB"));
        }
    }
}
