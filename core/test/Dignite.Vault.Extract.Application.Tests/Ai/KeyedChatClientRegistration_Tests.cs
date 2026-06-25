using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Static contract test guarding the <c>[FromKeyedServices(...)]</c> / <c>AddKeyedChatClient(...)</c>
/// symmetry between production code and host wiring.
///
/// <para>
/// Background: the keyed <see cref="IChatClient"/> consumers (title generator, structured-output)
/// are wired through <see cref="FromKeyedServicesAttribute"/>. A typo in a consumer or a host
/// that drops a registration but leaves consumers behind would silently break at runtime with
/// no test signal. This test plugs that gap with a reflection-based audit:
/// </para>
///
/// <list type="number">
///   <item>For every public/internal type in the production assemblies, find every constructor
///         parameter annotated with <see cref="FromKeyedServicesAttribute"/>.</item>
///   <item>Assert each consumed key is present in the host's expected registered-keys snapshot.</item>
///   <item>Assert each registered key has at least one consumer (catches dead registrations).</item>
/// </list>
///
/// <para>
/// The <see cref="HostRegisteredKeys"/> set below is a deliberate snapshot — when
/// <c>ExtractHostModule.ConfigureAI</c> adds or removes a <c>AddKeyedChatClient(...)</c> call,
/// this constant must be updated in the same change.
/// </para>
/// </summary>
public class KeyedChatClientRegistration_Tests
{
    /// <summary>
    /// Snapshot of <c>ExtractHostModule.ConfigureAI</c>'s <c>AddKeyedChatClient(...)</c>
    /// calls. KEEP IN SYNC with that method.
    /// </summary>
    private static readonly HashSet<string> HostRegisteredKeys = new()
    {
        ExtractConsts.TitleGeneratorChatClientKey,
        ExtractConsts.StructuredChatClientKey,
    };

    private static readonly Assembly[] ProductionAssemblies =
    {
        typeof(ExtractConsts).Assembly,                    // Dignite.Vault.Extract.Abstractions
        typeof(IDocumentRepository).Assembly,                  // Dignite.Vault.Extract.Domain
        typeof(DocumentDto).Assembly,                          // Dignite.Vault.Extract.Application.Contracts
        typeof(DocumentClassificationWorkflow).Assembly,       // Dignite.Vault.Extract.Application (where keyed consumers live)
    };

    [Fact]
    public void Every_Consumed_Key_Is_Registered_By_The_Host()
    {
        var consumers = FindKeyedConsumers().ToList();

        var orphans = consumers
            .Where(c => !HostRegisteredKeys.Contains(c.Key))
            .ToList();

        orphans.ShouldBeEmpty(
            "These [FromKeyedServices(...)] consumers reference keys NOT registered by " +
            "ExtractHostModule.ConfigureAI. Either add the AddKeyedChatClient call there, " +
            "or remove the [FromKeyedServices] consumer / fix the typo: " +
            string.Join("; ", orphans.Select(o => $"{o.TypeName}.{o.ParamName} = \"{o.Key}\"")));
    }

    [Fact]
    public void Every_Host_Registered_Key_Has_At_Least_One_Consumer()
    {
        var consumedKeys = FindKeyedConsumers().Select(c => c.Key).ToHashSet();

        var unused = HostRegisteredKeys
            .Where(key => !consumedKeys.Contains(key))
            .ToList();

        unused.ShouldBeEmpty(
            "These host-registered keyed IChatClient registrations have NO consumer anywhere " +
            "in production code. Either delete the AddKeyedChatClient call (dead registration), " +
            "or restore the missing consumer that was accidentally deleted: " +
            string.Join(", ", unused));
    }

    private static IEnumerable<KeyedConsumer> FindKeyedConsumers()
    {
        foreach (var asm in ProductionAssemblies)
        {
            foreach (var type in asm.GetTypes())
            {
                foreach (var ctor in type.GetConstructors())
                {
                    foreach (var param in ctor.GetParameters())
                    {
                        var attr = param.GetCustomAttribute<FromKeyedServicesAttribute>();
                        if (attr?.Key is string key && param.ParameterType == typeof(IChatClient))
                        {
                            yield return new KeyedConsumer(
                                TypeName: type.Name,
                                ParamName: param.Name ?? "<unnamed>",
                                Key: key);
                        }
                    }
                }
            }
        }
    }

    private sealed record KeyedConsumer(string TypeName, string ParamName, string Key);
}
