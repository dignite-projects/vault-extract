using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Shouldly;
using Volo.Abp.Application.Services;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.EventBus;
using Volo.Abp.Features;
using Xunit;

namespace Dignite.Vault.Extract.Features;

/// <summary>
/// The gate lives on <see cref="VaultExtractAppService"/> and reaches each application service by inheritance, so
/// a new service that skips the base class would be silently ungated. This is the check that catches it - and
/// the check that the other half of the design still holds: nothing that runs the document pipeline goes through
/// a gated service. Both are reflection tests on purpose; no behavioural fact would notice a service added next
/// month.
/// <para>
/// They scan every product assembly this test project can see: each <c>Dignite.Vault.Extract.*.dll</c> next to it
/// except the test assemblies, which is the Application, Domain, EntityFrameworkCore, Parse and Ocr projects it
/// references, directly or not. A project it does not reference (<c>Mcp</c>, <c>HttpApi</c>, the OCR provider
/// projects, the host) is not scanned; none of them holds a background job, a worker or an application service
/// today, so a first one added there has to be covered by a test of its own.
/// </para>
/// </summary>
public class VaultExtractAppServiceFeature_Tests
{
    private static readonly Type[] ApplicationTypes = LoadProductAssemblies().SelectMany(GetLoadableTypes).ToArray();

    [Fact]
    public void Every_application_service_should_require_the_VaultExtract_feature()
    {
        var services = ApplicationTypes
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && typeof(IApplicationService).IsAssignableFrom(type))
            .ToList();

        services.ShouldNotBeEmpty("the scan should find the application services");

        var ungated = services
            .Where(type => !RequiresVaultExtractFeature(type))
            .Select(type => type.FullName)
            .ToList();

        ungated.ShouldBeEmpty(
            $"every application service must derive from {nameof(VaultExtractAppService)} " +
            $"(or carry [RequiresFeature(\"{VaultExtractFeatures.Enable}\")] itself)");
    }

    /// <summary>
    /// Background jobs, workers and event handlers run with no signed-in user, so ABP cannot resolve the Edition
    /// level of the feature for them (see <see cref="VaultExtractFeatures.Enable"/>). They stay clear of the gate
    /// only because they never call an application service; a constructor that takes one is the way that
    /// stops being true. Hand the shared logic to a domain service or an Application-layer helper instead, the
    /// way the existing pipeline code already does.
    /// <para>
    /// Constructor parameters only: it does not follow a dependency into a second class, and it cannot see a
    /// service resolved lazily from <c>IServiceProvider</c>. It catches the ordinary mistake, not a determined one.
    /// Which assemblies it scans is on the type.
    /// </para>
    /// </summary>
    [Fact]
    public void No_background_job_worker_or_event_handler_should_depend_on_an_application_service()
    {
        var pipelineTypes = ApplicationTypes
            .Where(type => type is { IsClass: true, IsAbstract: false }
                           && (IsBackgroundJob(type)
                               || typeof(IBackgroundWorker).IsAssignableFrom(type)
                               || typeof(IEventHandler).IsAssignableFrom(type)))
            .ToList();

        pipelineTypes.ShouldNotBeEmpty("the scan should find the pipeline's jobs and event handlers");

        var offenders = pipelineTypes
            .SelectMany(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => typeof(IApplicationService).IsAssignableFrom(parameter.ParameterType))
                .Select(parameter => $"{type.FullName} takes {parameter.ParameterType.Name}"))
            .ToList();

        offenders.ShouldBeEmpty(
            "a job or event handler that calls an application service would be refused by the " +
            $"{VaultExtractFeatures.Enable} gate whenever the host grants the feature per edition");
    }

    private static IEnumerable<Assembly> LoadProductAssemblies()
    {
        return Directory.GetFiles(AppContext.BaseDirectory, "Dignite.Vault.Extract.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Tests") && !name.EndsWith(".TestBase"))
            .Select(name => Assembly.Load(new AssemblyName(name!)));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // An assembly with an optional dependency that is not in the output: scan what did load.
            return exception.Types.OfType<Type>();
        }
    }

    private static bool IsBackgroundJob(Type type)
    {
        return type.GetInterfaces().Any(i =>
            i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IAsyncBackgroundJob<>)
                || i.GetGenericTypeDefinition() == typeof(IBackgroundJob<>)));
    }

    private static bool RequiresVaultExtractFeature(Type type)
    {
        return type
            .GetCustomAttributes<RequiresFeatureAttribute>(inherit: true)
            .Any(attribute => attribute.Features.Contains(VaultExtractFeatures.Enable));
    }
}
