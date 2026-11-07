using MatDrive.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatDrive.Data;

/// <summary>
/// The database (PostgreSQL). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate, CreateUserId, UpdateDate and UpdateUserId
/// (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/>), and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// The bytes of the files are not in the database: they lie in the data volume (<c>Services/Files/BlobStore</c>).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? current = null) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<AppPassword> AppPasswords => Set<AppPassword>();

    public DbSet<Drive> Drives => Set<Drive>();

    public DbSet<DriveMember> DriveMembers => Set<DriveMember>();

    public DbSet<Node> Nodes => Set<Node>();

    public DbSet<NodeVersion> NodeVersions => Set<NodeVersion>();

    public DbSet<NodeShare> NodeShares => Set<NodeShare>();

    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();

    public DbSet<NodeStar> NodeStars => Set<NodeStar>();

    public DbSet<NodeVisit> NodeVisits => Set<NodeVisit>();

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();

    public DbSet<StorageLocationAccess> StorageLocationAccess => Set<StorageLocationAccess>();

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.Language).HasMaxLength(5);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.Property(x => x.Scheme).HasMaxLength(10);
            e.Property(x => x.View).HasMaxLength(10);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
            e.HasIndex(x => x.IdentitySessionId);
        });

        b.Entity<AppPassword>(e =>
        {
            e.ToTable("AppPassword");
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Prefix).HasMaxLength(16);
        });

        b.Entity<Drive>(e =>
        {
            e.ToTable("Drive");
            e.HasIndex(x => x.OwnerId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Colour).HasMaxLength(7);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DriveMember>(e =>
        {
            e.ToTable("DriveMember");
            e.HasIndex(x => new { x.DriveId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Drive).WithMany(x => x.Members).HasForeignKey(x => x.DriveId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Node>(e =>
        {
            e.ToTable("Node");
            e.HasIndex(x => new { x.DriveId, x.ParentId, x.Name });
            e.HasIndex(x => x.Path);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => new { x.DriveId, x.InTrash });
            e.HasOne(x => x.Drive).WithMany().HasForeignKey(x => x.DriveId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.Path).HasMaxLength(2000);
            e.Property(x => x.MimeType).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Colour).HasMaxLength(7);
            e.Property(x => x.TextContent).HasMaxLength(20000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<NodeVersion>(e =>
        {
            e.ToTable("NodeVersion");
            e.HasIndex(x => new { x.NodeId, x.Number }).IsUnique();
            e.HasIndex(x => x.BlobId).IsUnique();
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.MimeType).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Comment).HasMaxLength(500);
        });

        b.Entity<NodeShare>(e =>
        {
            e.ToTable("NodeShare");
            e.HasIndex(x => new { x.NodeId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Node).WithMany(x => x.Shares).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShareLink>(e =>
        {
            e.ToTable("ShareLink");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.NodeId);
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(200);
        });

        b.Entity<NodeStar>(e =>
        {
            e.ToTable("NodeStar");
            e.HasIndex(x => new { x.UserId, x.NodeId }).IsUnique();
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NodeVisit>(e =>
        {
            e.ToTable("NodeVisit");
            e.HasIndex(x => new { x.UserId, x.NodeId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.VisitDate });
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("Activity");
            e.HasIndex(x => x.OccurredDate);
            e.HasIndex(x => new { x.NodeId, x.OccurredDate });
            e.HasIndex(x => new { x.DriveId, x.OccurredDate });
            e.Property(x => x.Action).HasMaxLength(30);
            e.Property(x => x.Target).HasMaxLength(255);
            e.Property(x => x.Detail).HasMaxLength(1000);
        });

        b.Entity<StorageLocation>(e =>
        {
            e.ToTable("StorageLocation");
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.RootPath).HasMaxLength(500);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.Share).HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(255);
            e.Property(x => x.SecretProtected).HasMaxLength(2000);
            e.Property(x => x.Domain).HasMaxLength(255);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.PrivateKeyProtected).HasMaxLength(20000);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<StorageLocationAccess>(e =>
        {
            e.ToTable("StorageLocationAccess");
            e.HasIndex(x => new { x.LocationId, x.UserId }).IsUnique();
            e.HasOne(x => x.Location).WithMany(x => x.Access).HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UploadSession>(e =>
        {
            e.ToTable("UploadSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.Property(x => x.RelativePath).HasMaxLength(1000);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.Property(x => x.PublicUrl).HasMaxLength(300);
            e.Property(x => x.OfficeUrl).HasMaxLength(300);
            e.Property(x => x.OfficeInternalUrl).HasMaxLength(300);
            e.Property(x => x.DriveUrlForOffice).HasMaxLength(300);
            e.Property(x => x.OfficeKeyProtected).HasMaxLength(2000);
            e.Property(x => x.OfficeFormats).HasColumnType("jsonb");
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var user = current?.Id;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= user;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = user ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>For <c>dotnet ef migrations add</c>: no database is needed to create a migration.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=matdrive;Username=matdrive;Password=matdrive").Options);
}
