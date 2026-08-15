using MicroVMStudio.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MicroVMStudio.Core.Data;

public class StudioDbContext(DbContextOptions<StudioDbContext> options) : DbContext(options)
{
    public DbSet<Vm> Vms => Set<Vm>();
    public DbSet<VmDisk> Disks => Set<VmDisk>();
    public DbSet<VmImage> Images => Set<VmImage>();
    public DbSet<VmSecret> Secrets => Set<VmSecret>();
    public DbSet<VmEgressRule> EgressRules => Set<VmEgressRule>();
    public DbSet<VmEvent> Events => Set<VmEvent>();
    public DbSet<IpAllocation> IpAllocations => Set<IpAllocation>();
    public DbSet<HostSetting> HostSettings => Set<HostSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Vm>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.DesiredStatus).HasConversion<string>();
            e.Property(x => x.RestartPolicy).HasConversion<string>();

            // Disk is 1:1 but deliberately not a FK constraint — a disk outlives
            // its VM when the user deletes with "keep disk".
            e.HasOne(x => x.Disk)
             .WithMany()
             .HasForeignKey(x => x.DiskId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Events).WithOne(x => x.Vm).HasForeignKey(x => x.VmId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Secrets).WithOne(x => x.Vm).HasForeignKey(x => x.VmId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.EgressRules).WithOne(x => x.Vm).HasForeignKey(x => x.VmId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VmDisk>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.State);
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Format).HasConversion<string>();
        });

        b.Entity<VmImage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Digest).IsUnique();
            e.HasIndex(x => new { x.Kind, x.Name });
            e.Property(x => x.Kind).HasConversion<string>();
        });

        b.Entity<VmSecret>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.VmId, x.Key }).IsUnique();
        });

        b.Entity<VmEgressRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.VmId);
        });

        b.Entity<VmEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.VmId, x.OccurredAt });
            e.Property(x => x.PrevStatus).HasConversion<string>();
            e.Property(x => x.NewStatus).HasConversion<string>();
        });

        b.Entity<IpAllocation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PrivateIp).IsUnique();
            e.HasIndex(x => x.VmId).IsUnique();
            e.HasOne(x => x.Vm).WithOne(x => x.IpAllocation)
             .HasForeignKey<IpAllocation>(x => x.VmId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<HostSetting>(e => e.HasKey(x => x.Key));
    }
}
