namespace Dignite.Vault.Extract.Features;

public static class VaultExtractFeatures
{
    public const string GroupName = "VaultExtract";

    /// <summary>
    /// Whether a tenant may use Vault Extract's <b>application services</b> - and with them the REST controllers
    /// and the MCP tools / resources, which are thin callers of those services. A host such as Dignite.Cloud
    /// turns this off for tenants whose edition does not include Vault Extract.
    /// <para>
    /// Enforced once, on <c>VaultExtractAppService</c>, so every application service is gated at the same time;
    /// <c>VaultExtractAppServiceFeature_Tests</c> fails a new service that skips the base class. Unlike a
    /// product with a separate admin and public surface, Extract has one base class: the document data surface
    /// (upload, read, list, export) is gated together with the configuration surface (document types, fields,
    /// cabinets, packs, reprocessing).
    /// </para>
    /// <para>
    /// <b>On by default</b> (see <c>VaultExtractFeatureDefinitionProvider</c>): a host that does not run the
    /// Feature Management module has nowhere to switch it on, and would otherwise lose every application
    /// service. A host that wants Extract to be opt-in overrides the default to <c>false</c> in its own
    /// <c>FeatureDefinitionProvider</c> and grants it per edition or tenant.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> enforced - by <c>[RequiresFeature]</c> or by <c>IFeatureChecker</c> - on anything
    /// that does not run as a signed-in user: the background jobs and event handlers of the document pipeline
    /// (upload, parse, OCR, classification, field extraction, <c>DocumentReadyEto</c>), and anything else that
    /// has no principal behind it. As read from ABP's implementation - not run end to end with an edition-bearing
    /// token here - the Edition level of a feature is resolved from the current principal's <c>editionid</c> claim,
    /// so such a path is expected to see only the tenant-level value or the definition default. If so, a host that
    /// flips the default to <c>false</c> and grants the feature per edition would make that path read "off" and
    /// stop every tenant's pipeline mid-flight, which is the risk this rule exists to rule out. Those paths also
    /// never call an application service, which is what keeps them clear of the gate whatever ABP does;
    /// <c>VaultExtractAppServiceFeature_Tests</c> pins that too.
    /// Whether a tenant without the feature should stop <i>processing</i> documents is a separate product
    /// decision, not something to add here.
    /// </para>
    /// <para>
    /// The same reasoning is expected to apply to the application services themselves: a token without a user
    /// behind it (an OAuth <c>client_credentials</c> service account, for REST or MCP) has no user to carry an
    /// <c>editionid</c>, so under an edition-granted default it would be refused by the gate along with the
    /// signed-in users. This has not been confirmed with an edition-bearing tenant; check it before relying on
    /// it, in either direction.
    /// </para>
    /// </summary>
    public const string Enable = GroupName + ".Enable";
}
