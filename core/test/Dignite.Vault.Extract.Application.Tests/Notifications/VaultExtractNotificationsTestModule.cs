using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Notifications;

/// <summary>
/// The exit with its two seams replaced: the document store (this stack has no persistence) and the publisher (what
/// the handler asks of the framework is the thing under test, not the framework's own distribution).
/// <para>
/// The publisher is a <b>scoped</b> fake that refuses to work once its scope is disposed, like the real one does.
/// That is the point of it: a singleton NSubstitute would never notice that a handler used a service from a scope
/// the event bus had already disposed, which is exactly the bug that made every real notification fail.
/// </para>
/// </summary>
[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class DocumentNotificationsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton<PublishedNotifications>();
        context.Services.AddScoped<INotificationPublisher, ScopeBoundNotificationPublisher>();
    }
}

public sealed record PublishedNotification(
    string Name,
    NotificationData? Data,
    NotificationEntityIdentifier? Entity,
    NotificationSeverity Severity,
    Guid[]? UserIds,
    Guid? TenantId);

/// <summary>What reached the publisher, across every scope. Singleton.</summary>
public class PublishedNotifications
{
    private readonly List<PublishedNotification> _items = new();

    public IReadOnlyList<PublishedNotification> All {
        get { lock (_items) { return _items.ToList(); } }
    }

    /// <summary>When set, every publish attempt throws it.</summary>
    public Exception? FailWith { get; set; }

    internal void Add(PublishedNotification item)
    {
        lock (_items) { _items.Add(item); }
    }
}

public sealed class ScopeBoundNotificationPublisher : INotificationPublisher, IDisposable
{
    private readonly PublishedNotifications _recorder;
    private readonly ICurrentTenant _currentTenant;
    private bool _disposed;

    public ScopeBoundNotificationPublisher(PublishedNotifications recorder, ICurrentTenant currentTenant)
    {
        _recorder = recorder;
        _currentTenant = currentTenant;
    }

    public Task PublishAsync(
        string notificationName,
        NotificationData? data = null,
        NotificationEntityIdentifier? entityIdentifier = null,
        NotificationSeverity severity = NotificationSeverity.Info,
        Guid[]? userIds = null,
        Guid[]? excludedUserIds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_recorder.FailWith != null)
        {
            throw _recorder.FailWith;
        }

        _recorder.Add(new PublishedNotification(
            notificationName, data, entityIdentifier, severity, userIds, _currentTenant.Id));
        return Task.CompletedTask;
    }

    public void Dispose() => _disposed = true;
}
