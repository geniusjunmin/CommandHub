using CommandHub.Domain;
using CommandHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CommandHub.Infrastructure.Persistence;

public sealed class CommandHubDbContext(DbContextOptions<CommandHubDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<ServerCredential> ServerCredentials => Set<ServerCredential>();
    public DbSet<UserServerPermission> UserServerPermissions => Set<UserServerPermission>();
    public DbSet<CommandExecution> CommandExecutions => Set<CommandExecution>();
    public DbSet<CommandOutputChunk> CommandOutputChunks => Set<CommandOutputChunk>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CommandExecutionTag> CommandExecutionTags => Set<CommandExecutionTag>();
    public DbSet<CommandFavorite> CommandFavorites => Set<CommandFavorite>();
    public DbSet<CommandTemplate> CommandTemplates => Set<CommandTemplate>();
    public DbSet<CommandTemplateParameter> CommandTemplateParameters => Set<CommandTemplateParameter>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(x => x.DisplayName).HasMaxLength(120);
            entity.Property(x => x.PreferredLanguage).HasMaxLength(16);
            entity.Property(x => x.TimeZone).HasMaxLength(80);
            entity.HasIndex(x => x.IsEnabled);
        });

        builder.Entity<Server>(entity =>
        {
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => new { x.Environment, x.IsEnabled });
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Host).HasMaxLength(255);
            entity.Property(x => x.DefaultUsername).HasMaxLength(128);
            entity.Property(x => x.DefaultWorkingDirectory).HasMaxLength(1024);
            entity.Property(x => x.HostKeyAlgorithm).HasMaxLength(100);
            entity.Property(x => x.HostKeyFingerprint).HasMaxLength(256);
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
            entity.HasOne(x => x.Credential).WithOne(x => x.Server).HasForeignKey<ServerCredential>(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ServerCredential>(entity =>
        {
            entity.HasIndex(x => x.ServerId).IsUnique();
            entity.Property(x => x.Username).HasMaxLength(128);
        });

        builder.Entity<UserServerPermission>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.ServerId }).IsUnique();
            entity.HasOne(x => x.Server).WithMany(x => x.Permissions).HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CommandExecution>(entity =>
        {
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => new { x.UserId, x.CreatedAt });
            entity.HasIndex(x => new { x.ServerId, x.Status, x.CreatedAt });
            entity.HasIndex(x => x.NormalizedCommandHash);
            entity.Property(x => x.CommandText).HasColumnType("text");
            entity.Property(x => x.NormalizedCommand).HasColumnType("text");
            entity.Property(x => x.MaskedCommandText).HasColumnType("text");
            entity.Property(x => x.NormalizedCommandHash).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.NormalizedCommandPrefix).HasMaxLength(DomainRules.NormalizedCommandPrefixCharacters);
            entity.Property(x => x.CancellationRequestedByUserId).HasMaxLength(450);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.WorkingDirectory).HasMaxLength(1024);
            entity.Property(x => x.Shell).HasMaxLength(32);
            entity.Property(x => x.RiskReasons).HasColumnType("jsonb");
            entity.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<CommandOutputChunk>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.HasIndex(x => new { x.ExecutionId, x.Sequence }).IsUnique();
            entity.Property(x => x.Content).HasMaxLength(65536);
            entity.HasOne(x => x.Execution).WithMany(x => x.OutputChunks).HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Tag>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.NormalizedName).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.NormalizedName).HasMaxLength(80);
        });

        builder.Entity<CommandExecutionTag>(entity =>
        {
            entity.HasKey(x => new { x.ExecutionId, x.TagId });
            entity.HasOne(x => x.Execution).WithMany(x => x.ExecutionTags).HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Tag).WithMany(x => x.ExecutionTags).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CommandFavorite>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.ExecutionId }).IsUnique();
            entity.HasOne(x => x.Execution).WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CommandTemplate>(entity =>
        {
            entity.HasIndex(x => new { x.CreatedByUserId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.CommandText).HasColumnType("text");
            entity.Property(x => x.RowVersion).IsConcurrencyToken();
            entity.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CommandTemplateParameter>(entity =>
        {
            entity.HasIndex(x => new { x.TemplateId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(80);
            entity.Property(x => x.DisplayName).HasMaxLength(120);
            entity.HasOne(x => x.Template).WithMany(x => x.Parameters).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.ResourceType).HasMaxLength(120);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });
    }
}
