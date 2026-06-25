using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.Modeling;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.SettingManagement.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Host.Data;

public class ExtractHostDbContext
    : AbpDbContext<ExtractHostDbContext>, IHasEventInbox, IHasEventOutbox
{

    public const string DbTablePrefix = "App";
    public const string DbSchema = null;

    /// <summary>
    /// ABP transactional outbox enqueue table (<see cref="IHasEventOutbox"/>).
    /// Extract outbound events are enqueued by callers through
    /// <c>IDistributedEventBus.PublishAsync</c> inside a UoW, then the ABP background worker delivers
    /// them asynchronously to the message middleware, guaranteeing at-least-once delivery.
    /// </summary>
    public DbSet<OutgoingEventRecord> OutgoingEvents { get; set; } = default!;

    /// <summary>
    /// ABP transactional inbox delivery table (<see cref="IHasEventInbox"/>).
    /// Used for exactly-once consumption tracking when Extract itself subscribes to external
    /// distributed events.
    /// </summary>
    public DbSet<IncomingEventRecord> IncomingEvents { get; set; } = default!;

    public ExtractHostDbContext(DbContextOptions<ExtractHostDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        /* Include modules to your migration db context */

        builder.ConfigureSettingManagement();
        builder.ConfigureBackgroundJobs();
        builder.ConfigureAuditLogging();
        builder.ConfigureFeatureManagement();
        builder.ConfigurePermissionManagement();
        builder.ConfigureIdentity();
        builder.ConfigureOpenIddict();

        // ABP transactional outbox / inbox tables, replacing the custom OutboxEvent removed by issue
        // #188. When callers publish inside a UoW, events are automatically written to AbpEventOutbox;
        // the background worker scans the table and performs delivery.
        builder.ConfigureEventInbox();
        builder.ConfigureEventOutbox();

        // Extract core module
        builder.ConfigureExtract();
    }
}
