using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Gdpr;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Vault.Extract.Gdpr;

public class GdprUserDataDeletionEventHandler_Tests : VaultExtractApplicationTestBase<GdprUserDataDeletionTestModule>
{
    private static readonly Guid User = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly IDocumentRepository _documents;
    private readonly ITenantStore _tenants;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly List<Call> _calls = new();

    /// <summary>What the repository was asked, and in which tenant and unit of work it was asked.</summary>
    private sealed record Call(Guid? Tenant, Guid User, Guid? UnitOfWorkId);

    public GdprUserDataDeletionEventHandler_Tests()
    {
        _documents = GetRequiredService<IDocumentRepository>();
        _tenants = GetRequiredService<ITenantStore>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();

        _documents.AnonymizeUploaderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _calls.Add(new Call(_currentTenant.Id, call.ArgAt<Guid>(0), _unitOfWorkManager.Current?.Id));
                return Task.FromResult(1);
            });
        HaveTenants();
    }

    private void HaveTenants(params TenantConfiguration[] tenants) =>
        _tenants.GetListAsync(Arg.Any<bool>()).Returns(Task.FromResult<IReadOnlyList<TenantConfiguration>>(tenants));

    /// <summary>Makes the repository throw <paramref name="exception"/> in <paramref name="tenantId"/>, and record the other layers.</summary>
    private void FailIn(Guid tenantId, Exception exception) =>
        _documents.AnonymizeUploaderAsync(User, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var tenant = _currentTenant.Id;
                if (tenant == tenantId)
                {
                    throw exception;
                }

                _calls.Add(new Call(tenant, User, _unitOfWorkManager.Current?.Id));
                return Task.FromResult(1);
            });

    private static TenantConfiguration Tenant(Guid id, bool active = true) =>
        new(id, $"tenant-{id.ToString()[..4]}") { IsActive = active };

    private static GdprUserDataDeletionRequestedEto EventFor(Guid userId) => new() { UserId = userId };

    /// <summary>
    /// Delivers the event the way the bus does: the handler is resolved from a scope of its own.
    /// </summary>
    private async Task HandleAsync(GdprUserDataDeletionRequestedEto evt)
    {
        using var scope = ServiceProvider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GdprUserDataDeletionEventHandler>().HandleEventAsync(evt);
    }

    [Fact]
    public async Task Visits_the_host_and_every_tenant_each_under_its_own_tenant()
    {
        // An inactive tenant's documents still carry the name, so it is not skipped.
        HaveTenants(Tenant(TenantA), Tenant(TenantB, active: false));

        await HandleAsync(EventFor(User));

        _calls.Select(call => call.Tenant).ShouldBe(new Guid?[] { null, TenantA, TenantB });
        _calls.ShouldAllBe(call => call.User == User);
    }

    [Fact]
    public async Task Gives_each_layer_a_unit_of_work_of_its_own()
    {
        // With a database per tenant the connection is picked when the unit of work starts, so a layer that shared
        // the caller's would read and write the wrong database.
        HaveTenants(Tenant(TenantA), Tenant(TenantB));

        Guid outerId;
        using (var outer = _unitOfWorkManager.Begin())
        {
            outerId = outer.Id;
            await HandleAsync(EventFor(User));
            await outer.CompleteAsync();
        }

        var ids = _calls.Select(call => call.UnitOfWorkId).ToList();
        ids.ShouldAllBe(id => id.HasValue);
        ids.Distinct().Count().ShouldBe(3, "each of the host and the two tenants gets its own unit of work");
        ids.ShouldNotContain(outerId);
    }

    [Fact]
    public async Task A_host_without_tenants_is_handled_in_the_host_alone()
    {
        await HandleAsync(EventFor(User));

        _calls.ShouldHaveSingleItem().Tenant.ShouldBeNull();
    }

    [Fact]
    public async Task Ignores_an_event_that_carries_no_user_id()
    {
        // Guid.Empty names no one: it must not reach the repository, let alone sweep a layer.
        HaveTenants(Tenant(TenantA));

        await HandleAsync(EventFor(Guid.Empty));

        _calls.ShouldBeEmpty();
        await _tenants.DidNotReceiveWithAnyArgs().GetListAsync();
    }

    [Fact]
    public async Task A_failing_layer_is_logged_and_does_not_stop_the_others_or_throw()
    {
        // The retry unit is the whole event, Identity's own erasure handler included, so this handler must not throw
        // for one bad tenant. It also must not give up on the tenants after it.
        HaveTenants(Tenant(TenantA), Tenant(TenantB));
        FailIn(TenantA, new InvalidOperationException("tenant database is unreachable"));
        var logger = new RecordingLogger<GdprUserDataDeletionEventHandler>();
        var handler = new GdprUserDataDeletionEventHandler(_documents, _tenants, _currentTenant, _unitOfWorkManager, logger);

        await handler.HandleEventAsync(EventFor(User));

        _calls.Select(call => call.Tenant).ShouldBe(new Guid?[] { null, TenantB });
        var error = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList().ShouldHaveSingleItem();
        error.Message.ShouldContain(TenantA.ToString());
        error.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        // Cancelling is not a per-tenant fault: every layer after it would fail the same way, and the event must not
        // come out looking handled. It propagates, and the layers after it are not tried.
        HaveTenants(Tenant(TenantA), Tenant(TenantB));
        FailIn(TenantA, new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => HandleAsync(EventFor(User)));

        _calls.Select(call => call.Tenant).ShouldBe(new Guid?[] { null });
    }

    [Fact]
    public async Task A_tenant_store_that_cannot_list_tenants_fails_the_event_before_anything_is_changed()
    {
        // Listing happens first and changes nothing, so failing here is safe - and the right call: the store being down
        // fails the other handlers of the same event too.
        _tenants.GetListAsync(Arg.Any<bool>())
            .Returns(Task.FromException<IReadOnlyList<TenantConfiguration>>(new InvalidOperationException("store is down")));

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(EventFor(User)));

        _calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_host_is_handled_in_the_host_even_when_the_event_arrives_under_a_tenant()
    {
        // The handler runs under whatever tenant is ambient when the event is delivered. The Host layer must still
        // switch to the Host explicitly, or it would erase the ambient tenant's rows a second time and never the Host's.
        HaveTenants(Tenant(TenantA));

        using (_currentTenant.Change(TenantB))
        {
            await HandleAsync(EventFor(User));
        }

        _calls.Select(call => call.Tenant).ShouldBe(new Guid?[] { null, TenantA });
    }

    [Fact]
    public async Task Is_subscribed_to_the_distributed_event_bus()
    {
        // Wiring, not logic: publishing the framework's event reaches the handler, so the subscription (and the
        // package reference behind it) actually works in the module graph.
        await GetRequiredService<IDistributedEventBus>().PublishAsync(EventFor(User));

        _calls.ShouldContain(call => call.Tenant == null && call.User == User);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
