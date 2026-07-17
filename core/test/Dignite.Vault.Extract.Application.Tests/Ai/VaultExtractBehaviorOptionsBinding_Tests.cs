using System.Collections.Generic;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Test module that stacks an in-memory <c>Vault:ExtractBehavior</c> configuration
/// section on top of whatever <see cref="VaultExtractApplicationTestModule"/> already
/// provides. <see cref="VaultExtractApplicationModule.ConfigureServices"/> binds
/// <see cref="VaultExtractBehaviorOptions"/> to that section, so this module is the
/// vehicle that lets the test prove the binding is wired end-to-end.
/// </summary>
[DependsOn(typeof(VaultExtractApplicationTestModule))]
public class VaultExtractBehaviorOptionsBindingTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var existing = context.Services.GetConfiguration();
        var stacked = new ConfigurationBuilder()
            .AddConfiguration(existing)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:ExtractBehavior:DefaultLanguage"] = "en",
                ["Vault:ExtractBehavior:MaxDocumentTypesInClassificationPrompt"] = "25",
                ["Vault:ExtractBehavior:MaxTextLengthPerExtraction"] = "16000",
                ["Vault:ExtractBehavior:MaxTitleGenerationMarkdownLength"] = "2048",
                ["Vault:ExtractBehavior:MaxFieldSchemaPromptLength"] = "48000",
            })
            .Build();

        context.Services.ReplaceConfiguration(stacked);
    }
}

/// <summary>
/// Acceptance test: configuration values placed under the <c>Vault:ExtractBehavior</c>
/// JSON section must reach <see cref="VaultExtractBehaviorOptions"/> consumers via
/// <see cref="IOptions{T}"/>.
/// </summary>
public class VaultExtractBehaviorOptionsBinding_Tests
    : VaultExtractApplicationTestBase<VaultExtractBehaviorOptionsBindingTestModule>
{
    private readonly VaultExtractBehaviorOptions _options;

    public VaultExtractBehaviorOptionsBinding_Tests()
    {
        _options = GetRequiredService<IOptions<VaultExtractBehaviorOptions>>().Value;
    }

    [Fact]
    public void Configuration_Values_Flow_Through_To_Options()
    {
        // Each assertion would fail with the class default if the
        // ExtractBehavior → VaultExtractBehaviorOptions binding ever regresses.
        _options.DefaultLanguage.ShouldBe("en");                                      // default "ja"
        _options.MaxDocumentTypesInClassificationPrompt.ShouldBe(25);                 // default 50
        _options.MaxTextLengthPerExtraction.ShouldBe(16000);                          // default 8000
        _options.MaxTitleGenerationMarkdownLength.ShouldBe(2048);                     // default 4000
        _options.MaxFieldSchemaPromptLength.ShouldBe(48000);                          // default 32000
    }
}
