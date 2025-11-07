using CFACalculateWebAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace CFACalculateWebAPI.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        { }

        // Keyless table
        public DbSet<CfaDataExcel> CfaDataExcel { get; set; }

        // Example: Audit table if you need it
        public DbSet<Audit> Audit { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Mark CfaDataExcel as keyless
            modelBuilder.Entity<CfaDataExcel>().HasNoKey();

            // Configure Audit table normally (if it has primary key)
            modelBuilder.Entity<Audit>().HasKey(a => a.AuditID);
        }
    }
}
