using Dignite.Vault.Extract.Localization;
using Volo.Abp.Features;
using Volo.Abp.Localization;
using Volo.Abp.Validation.StringValues;

namespace Dignite.Vault.Extract.Features;

public class VaultExtractFeatureDefinitionProvider : FeatureDefinitionProvider
{
    public override void Define(IFeatureDefinitionContext context)
    {
        var group = context.AddGroup(VaultExtractFeatures.GroupName, L("Feature:VaultExtract"));

        // No allowedProviders: an empty list allows every provider, so a tenant value, an edition value and a
        // test override all take effect.
        group.AddFeature(
            VaultExtractFeatures.Enable,
            defaultValue: "true",
            displayName: L("Feature:VaultExtract.Enable"),
            description: L("Feature:VaultExtract.Enable:Description"),
            valueType: new ToggleStringValueType(),
            // Visible to clients so the Angular SPA can hide its menu from application-configuration - a
            // convenience only, the application services are what actually enforce it.
            isVisibleToClients: true);
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<VaultExtractResource>(name);
    }
}
