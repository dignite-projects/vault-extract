using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Dignite.Vault.Extract.Host.Data;
using Dignite.Vault.Extract.Host.HealthChecks;
using Dignite.Vault.Extract.Host.Localization;
using Dignite.Vault.Extract.Localization;
using Dignite.Vault.Extract.Mcp.Authentication;
using Dignite.Vault.Extract.Ocr.VisionLlm;
using Dignite.Vault.Extract.Parse;
using Dignite.Vault.Extract.Parse.ElBrunoMarkItDown;
using Dignite.Vault.Extract.Parse.OpenXml;
using Dignite.Vault.Extract.Parse.Pdf;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Localization;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Caching;
using Volo.Abp.Emailing;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EntityFrameworkCore.SqlServer;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.FeatureManagement;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.Localization;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.OpenIddict;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.PermissionManagement.Identity;
using Volo.Abp.PermissionManagement.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.SettingManagement;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.Studio;
using Volo.Abp.Studio.Client.AspNetCore;
using Volo.Abp.Swashbuckle;
using Volo.Abp.TenantManagement;
using Volo.Abp.TenantManagement.EntityFrameworkCore;
using Volo.Abp.Timing;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract.Host;

[DependsOn(
    // ABP Framework packages
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpAutofacModule),
    typeof(AbpMapperlyModule),
    typeof(AbpCachingModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(AbpStudioClientAspNetCoreModule),

    // theme
    typeof(AbpAspNetCoreMvcUiLeptonXLiteThemeModule),

    // Account module packages
    typeof(AbpAccountApplicationModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpAccountWebOpenIddictModule),

    // Identity module packages
    typeof(AbpPermissionManagementDomainIdentityModule),
    typeof(AbpPermissionManagementDomainOpenIddictModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpIdentityHttpApiModule),

    // Permission Management module packages
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpPermissionManagementHttpApiModule),

    // Feature Management module packages
    typeof(AbpFeatureManagementHttpApiModule),
    typeof(AbpFeatureManagementApplicationModule),

    // Setting Management module packages
    typeof(AbpSettingManagementHttpApiModule),
    typeof(AbpSettingManagementApplicationModule),

    // Tenant Management module packages
    typeof(AbpTenantManagementHttpApiModule),
    typeof(AbpTenantManagementApplicationModule),

    // Entity Framework Core packages for the used modules
    typeof(AbpAuditLoggingEntityFrameworkCoreModule),
    typeof(AbpIdentityEntityFrameworkCoreModule),
    typeof(AbpOpenIddictEntityFrameworkCoreModule),
    typeof(AbpFeatureManagementEntityFrameworkCoreModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule),
    typeof(AbpSettingManagementEntityFrameworkCoreModule),
    typeof(AbpTenantManagementEntityFrameworkCoreModule),
    typeof(AbpBackgroundJobsEntityFrameworkCoreModule),
    typeof(AbpBlobStoringFileSystemModule),
    typeof(AbpEntityFrameworkCoreSqlServerModule),

    // Extract core modules
    typeof(VaultExtractHttpApiModule),
    typeof(VaultExtractMcpModule),          // MCP exit adapter, parallel to the HttpApi REST exit.
    typeof(VaultExtractApplicationModule),
    typeof(VaultExtractEntityFrameworkCoreModule),

    // Extract infrastructure modules
    typeof(VaultExtractParseModule),
    typeof(VaultExtractParsePdfModule),             // PdfPig: digital-PDF text layer + embedded-image transcription (#301). Claims .pdf; coexists with ElBruno (catch-all). Omitting it makes .pdf fall back to ElBruno.
    typeof(VaultExtractParseOpenXmlModule),         // OpenXML: PPTX (slide text/notes, #307) + DOCX (headings/tables/lists/inline formatting/hyperlinks/text boxes, #308), each with charts/tables + embedded-image transcription. Claims .pptx and .docx. REQUIRED for .pptx (ElBruno has no PresentationML converter -> empty Markdown, no OCR fallback since .pptx != .pdf); .docx degrades gracefully to ElBruno if this module is omitted.
    typeof(VaultExtractParseElBrunoMarkItDownModule),
    typeof(VaultExtractVisionLlmOcrModule)                  // Vision-LLM OCR for photos, receipts, and image PDFs; current default OCR provider (#259). IOcrProvider implementations are mutually exclusive: when switching providers, update the .csproj ProjectReference and ConfigureAI keyed vision IChatClient together. See docs/en/text-extraction/ocr-vision-llm.md.
                                                            // typeof(VaultExtractPaddleOcrModule),                 // Local PaddleOCR sidecar (free CPU, PP-StructureV3); to switch back, uncomment this, comment VisionLlm, and restore its .csproj ProjectReference
                                                            // typeof(VaultExtractAzureDocumentIntelligenceModule), // Cloud option (higher accuracy); when switching, also comment / enable the matching ProjectReference in .csproj
)]
public class VaultExtractHostModule : AbpModule
{
    /* Single point to enable/disable multi-tenancy */
    public const bool IsMultiTenant = true;

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(VaultExtractHostResource)
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                // Single audience for the whole Extract API. REST and MCP are two exit adapters
                // over the same OpenIddict-Bearer resource — every token's aud is "VaultExtract",
                // regardless of how it was obtained (manual static Bearer or MCP Guided OAuth).
                options.AddAudiences("VaultExtract");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        // MCP Guided-OAuth clients (RFC 9728 + RFC 8707) MUST send a `resource` parameter naming
        // the MCP server's canonical URI on every /authorize and /token request (docs/en/egress/mcp-server.md).
        // OpenIddict's default resource handling would reject that parameter two ways:
        //   - ValidateResources         → ID2190 (invalid_target) unless the URI is pre-registered;
        //   - ValidateResourcePermissions → ID2192 unless the client carries an `rsrc:<uri>` grant.
        // Extract is a SINGLE protected resource (audience "VaultExtract"); REST and MCP are just two
        // exit adapters over it. The token aud is derived from the granted scope's resources during
        // sign-in, NOT from this request parameter — verified by decompiling OpenIddict 7.2.0:
        // nothing in OpenIddictServerHandlers narrows the principal's resources by the request
        // `resource`, and ABP's authorize controller resolves resources from scopes alone. So the
        // resource indicator carries no isolation value here and there is no "other resource" to gate
        // against. We therefore turn OFF both resource gates (accept-but-ignore the parameter) rather
        // than minting the MCP URL as a first-class resource, which would scatter URL coupling across
        // scope resources, validation audiences, and per-client permissions. Both gates only fire for
        // requests that actually carry a `resource` param — the Angular / Swagger clients never do, so
        // this is a no-op for them. IgnoreResourcePermissions is flagged "NOT recommended" by
        // OpenIddict for the general multi-resource case; it is the correct, minimal choice for a
        // single-resource server. If Extract ever becomes multi-resource, revisit (re-enable the
        // gates, RegisterResources per resource, and seed rsrc: grants per client).
        PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
        {
            serverBuilder.DisableResourceValidation();
            serverBuilder.IgnoreResourcePermissions();
        });

        if (!hostingEnvironment.IsDevelopment())
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddProductionEncryptionAndSigningCertificate("openiddict.pfx", configuration["AuthServer:CertificatePassPhrase"]!);
            });
        }

        VaultExtractHostGlobalFeatureConfigurator.Configure();
        VaultExtractHostModuleExtensionConfigurator.Configure();
        VaultExtractHostEfCoreEntityExtensionMappings.Configure();
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        if (!hostingEnvironment.IsProduction())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.LogCompleteSecurityArtifact = true;
        }

        if (!configuration.GetValue<bool>("AuthServer:RequireHttpsMetadata"))
        {
            Configure<OpenIddictServerAspNetCoreOptions>(options =>
            {
                options.DisableTransportSecurityRequirement = true;
            });
        }

        // #469: when explicitly enabled, restore the originating client IP before /mcp rate limiting
        // so clients behind the same reverse proxy do not collapse into one partition. The MCP module's
        // registration fails closed unless the deployment declares trusted proxy IPs or CIDR networks.
        // Forwarded headers remain disabled for client IPs by default; UseForwardedHeaders is already the
        // first middleware in OnApplicationInitialization.
        context.Services.AddVaultExtractMcpForwardedClientIp(
            configuration.GetSection("Mcp:ForwardedClientIp"),
            includeForwardedProto: !configuration.GetValue<bool>("AuthServer:RequireHttpsMetadata"));

        if (hostingEnvironment.IsDevelopment())
        {
            context.Services.Replace(ServiceDescriptor.Singleton<IEmailSender, NullEmailSender>());
        }

        Configure<AbpClockOptions>(options =>
        {
            options.Kind = DateTimeKind.Utc;
        });

        ConfigureStudio(hostingEnvironment);
        ConfigureAuthentication(context);
        ConfigureBundles(hostingEnvironment);
        ConfigureMultiTenancy();
        ConfigureUrls(configuration);
        ConfigureHealthChecks(context);
        ConfigureSwagger(context.Services, configuration);
        ConfigureAutoApiControllers();
        ConfigureLocalization();
        ConfigureCors(context, configuration);
        ConfigureDataProtection(context);
        ConfigureVirtualFiles(hostingEnvironment);
        ConfigureEfCore(context);
        ConfigureDistributedEventBus();
        ConfigureAI(context, configuration);
        ConfigureOpenTelemetry(context, configuration);
        ConfigureRequestLimits(context);
        ConfigureMcpRateLimiter(context);
    }

    // #433: rate-limit the /mcp egress endpoint (a DoS / abuse backstop against unauthenticated probing).
    // The mechanism (per-IP fixed-window policy, 429 rejection) lives in the Mcp egress module
    // (AddVaultExtractMcpRateLimiter); the host owns the config and the pipeline wiring. Enabled by default
    // with generous limits (Mcp:RateLimit), applied to /mcp via RequireRateLimiting in
    // OnApplicationInitialization so it also covers the #278 discovery-401 path. The limiter never fires for
    // legitimate, in-limit MCP session traffic.
    private void ConfigureMcpRateLimiter(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.AddVaultExtractMcpRateLimiter(options =>
        {
            configuration.GetSection("Mcp:RateLimit").Bind(options);
        });
    }

    // #221: upload request body limits as Kestrel / Form-layer backstop. The real fail-closed friendly error lives in
    // DocumentAppService.UploadAsync, which validates against DocumentConsts.MaxUploadFileBytes and throws BusinessException.
    // Leave multipart envelope headroom here (+1 MiB), so a normal max-size file is not cut off by 413 before reaching the application layer,
    // while converging ASP.NET Core's implicit defaults (~28.6 MB Kestrel / 128 MB Form) into an explicit, auditable boundary.
    private static void ConfigureRequestLimits(ServiceConfigurationContext context)
    {
        var bodyLimit = DocumentConsts.MaxUploadFileBytes + 1024 * 1024;

        context.Services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = bodyLimit;
        });

        context.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = bodyLimit;
        });
    }

    // Enable ABP transactional outbox + inbox (issue #188):
    // - publish paths run inside the caller UoW, so events are written to AbpEventOutbox atomically with business changes
    // - a background worker scans the table and actually delivers to the message broker, guaranteeing at-least-once delivery
    // Downstream consumers handle redelivery idempotently by (DocumentId, EventType, EventTime).
    private void ConfigureDistributedEventBus()
    {
        Configure<AbpDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                config.UseDbContext<Data.VaultExtractHostDbContext>();
            });

            options.Inboxes.Configure(config =>
            {
                config.UseDbContext<Data.VaultExtractHostDbContext>();
            });
        });
    }

    private void ConfigureHealthChecks(ServiceConfigurationContext context)
    {
        context.Services.AddVaultExtractHealthChecks();
    }

    private void ConfigureStudio(IHostEnvironment hostingEnvironment)
    {
        if (hostingEnvironment.IsProduction())
        {
            Configure<AbpStudioClientOptions>(options =>
            {
                options.IsLinkEnabled = false;
            });
        }
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        // Bearer requests route to OpenIddict validation; everything else falls back to the application
        // cookie (browser / MVC Account). The /mcp endpoint keeps its bare scheme-free RequireAuthorization()
        // (the #278 invariant), so the dynamic-claims-enriched principal is not dropped by re-authentication.
        context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });

        ConfigureMcpAuthentication(context);
    }

    // #278: add OAuth Protected Resource Metadata discovery for the /mcp export endpoint (RFC 9728). This is additive
    // and does not break existing manual token paths. The McpAuth scheme has only two responsibilities in this host, neither involving token validation:
    //   1. Self-serve `/.well-known/oauth-protected-resource`. McpAuthenticationHandler implements
    //      IAuthenticationRequestHandler and directly returns metadata JSON for that path during UseAuthentication(),
    //      without relying on any authorization policy and without separately mapping an endpoint.
    //   2. Provide 401 challenge by injecting the `WWW-Authenticate: Bearer resource_metadata="..."` pointer.
    // Note that McpAuth must not enter the /mcp endpoint authorization policy AuthenticationSchemes. Otherwise PolicyEvaluator would
    // re-authenticate and lose the principal enriched by UseDynamicClaims. McpDiscoveryAuthorizationResultHandler triggers the challenge
    // only for marked /mcp endpoints; see that handler's comments.
    // Token validation, dynamic claims, and tenant resolution all still use the endpoint default policy + existing OpenIddict chain.
    // Manual token paths (mcp-remote static Bearer / Inspector manual token / password-grant) are unaffected; discovery triggers only on 401 without token.
    private void ConfigureMcpAuthentication(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var authority = configuration["AuthServer:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            // Without authority, there is no authorization server to point to and discovery is meaningless.
            // Do not register half-complete metadata or take over challenge. /mcp remains protected by RequireAuthorization,
            // and manual token paths are unaffected.
            return;
        }

        var selfUrl = configuration["App:SelfUrl"]?.TrimEnd('/');

        // #422: the discovery mechanics (McpAuth scheme registration, login-page hiding, challenge-only
        // result-handler replacement) are owned by the Mcp egress module's AddVaultExtractMcpDiscovery. The
        // host keeps only the deployment-specific decisions: whether discovery is enabled (the authority
        // guard above) and the ProtectedResourceMetadata values below.
        context.Services.AddVaultExtractMcpDiscovery(metadata =>
        {
            // RFC 9728 `resource`: the MCP server's canonical URI. The MCP authorization spec
            // says a client SHOULD use the most-specific URI it can, so we advertise the full
            // /mcp endpoint (not the bare host origin). The client echoes this back as the
            // RFC 8707 `resource` parameter on /authorize + /token; OpenIddict accepts-but-
            // ignores it because both resource gates are turned off in PreConfigureServices
            // (see the rationale there), so the issued token's aud stays "VaultExtract". Set
            // explicitly rather than left to handler inference so it remains correct behind a
            // reverse proxy (inference derives it from request host/scheme headers).
            metadata.Resource = string.IsNullOrWhiteSpace(selfUrl) ? null : $"{selfUrl}/mcp";
            metadata.AuthorizationServers = new List<string> { authority };
            metadata.ScopesSupported = new List<string> { "VaultExtract" };
            metadata.BearerMethodsSupported = new List<string> { "header" };
            metadata.ResourceName = "Extract MCP";
        });
    }

    private void ConfigureBundles(IHostEnvironment hostingEnvironment)
    {
        Configure<AbpBundlingOptions>(options =>
        {
            options.StyleBundles.Configure(
                LeptonXLiteThemeBundles.Styles.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-styles.css");
                }
            );

            options.ScriptBundles.Configure(
                LeptonXLiteThemeBundles.Scripts.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-scripts.js");
                    if (hostingEnvironment.IsDevelopment())
                    {
                        bundle.AddFiles("/dev-login-helper.js");
                    }
                }
            );
        });
    }

    private void ConfigureMultiTenancy()
    {
        Configure<AbpMultiTenancyOptions>(options =>
        {
            options.IsEnabled = IsMultiTenant;
        });
    }

    private void ConfigureUrls(IConfiguration configuration)
    {
        Configure<AppUrlOptions>(options =>
        {
            options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',') ?? Array.Empty<string>());

            options.Applications["Angular"].RootUrl = configuration["App:ClientUrl"];
            options.Applications["Angular"].Urls[AccountUrlNames.PasswordReset] = "account/reset-password";
        });
    }

    private void ConfigureLocalization()
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<VaultExtractHostResource>("en")
                .AddBaseTypes(typeof(VaultExtractResource))
                .AddVirtualJson("/Localization/ExtractHost");

            options.DefaultResourceType = typeof(VaultExtractHostResource);

            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "Chinese (Simplified)"));
            options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "Chinese (Traditional)"));
            options.Languages.Add(new LanguageInfo("ja", "ja", "Japanese"));
        });

    }

    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(VaultExtractHostModule).Assembly);
        });
    }

    private void ConfigureSwagger(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAbpSwaggerGenWithOAuth(
            configuration["AuthServer:Authority"]!,
            new Dictionary<string, string>
            {
                {"VaultExtract", "Extract API"}
            },
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Extract API", Version = "v1" });
                options.DocInclusionPredicate((docName, description) => true);
                options.CustomSchemaIds(type => type.FullName);
            });
    }

    private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder
                    .WithOrigins(
                        configuration["App:CorsOrigins"]?
                            .Split(",", StringSplitOptions.RemoveEmptyEntries)
                            .Select(o => o.RemovePostFix("/"))
                            .ToArray() ?? Array.Empty<string>()
                    )
                    .WithAbpExposedHeaders()
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private void ConfigureDataProtection(ServiceConfigurationContext context)
    {
        context.Services.AddDataProtection().SetApplicationName("VaultExtract");
    }

    private void ConfigureVirtualFiles(IWebHostEnvironment hostingEnvironment)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<VaultExtractHostModule>();
            if (hostingEnvironment.IsDevelopment())
            {
                /* Using physical files in development, so we don't need to recompile on changes */
                options.FileSets.ReplaceEmbeddedByPhysical<VaultExtractHostModule>(hostingEnvironment.ContentRootPath);
            }
        });
    }

    private void ConfigureEfCore(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<Data.VaultExtractHostDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                // Issue #206: after removing native JSON columns, SQL Server 2025 compatibility level 170 is no longer required.
                // The persistence layer now uses ordinary relational columns only (typed columns + nvarchar(max)), portable across
                // SQL Server versions and relational databases. Deployment targets are no longer locked to SQL Server 2025.
                configurationContext.UseSqlServer();
            });
        });

        var hostingEnvironment = context.Services.GetHostingEnvironment();
        Configure<AbpBlobStoringOptions>(options =>
        {
            options.Containers.ConfigureDefault(container =>
            {
                container.UseFileSystem(fileSystem =>
                {
                    fileSystem.BasePath = Path.Combine(
                        hostingEnvironment.ContentRootPath, "App_Data", "blobs");
                });
            });
        });
    }

    private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
    {
        // Classification and field extraction have no non-LLM fallback, so a provider is mandatory.
        // Fail fast at startup instead of letting documents silently fail provider auth on the first
        // pipeline call. The committed appsettings.json ships the "YOUR_API_KEY" placeholder; a real
        // provider must come from appsettings.Development.json / user-secrets / env vars. Hosts that
        // target a non-OpenAI wire protocol replace this whole method (see docs/en/configuration/ai-provider.md) and
        // own their own validation.
        var endpoint = configuration["Vault:Extract:Endpoint"];
        var apiKey = configuration["Vault:Extract:ApiKey"];
        var chatModelId = configuration["Vault:Extract:ChatModelId"];
        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(apiKey)
            || apiKey == "YOUR_API_KEY"
            || string.IsNullOrWhiteSpace(chatModelId))
        {
            throw new AbpException(
                "Extract requires an LLM provider before it can start: document classification and " +
                "field extraction have no non-LLM fallback. Set Vault:Extract:Endpoint, Vault:Extract:ApiKey, " +
                "and Vault:Extract:ChatModelId in host/src/appsettings.Development.json (git-ignored), " +
                "user-secrets, or environment variables. For a zero-cost local option, point Endpoint at a " +
                "local Ollama /v1 endpoint and use any non-empty token as the key. See docs/en/configuration/ai-provider.md.");
        }

        var openAIClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        // Title-generator chat client: single-shot, tool-free, prompt-unique-per-call so
        // distributed caching is a net negative. Consumed by
        // DocumentParseBackgroundJob.TryGenerateTitleAsync via
        // [FromKeyedServices(VaultExtractConsts.TitleGeneratorChatClientKey)]. Falls back
        // to ChatModelId when TitleGeneratorModelId is unset; hosts that want to cut cost
        // can point this at a small fast model (e.g. Qwen3-8B).
        var titleGeneratorModelId = configuration["Vault:Extract:TitleGeneratorModelId"]
            ?? chatModelId;
        context.Services.AddKeyedChatClient(
            VaultExtractConsts.TitleGeneratorChatClientKey,
            _ => openAIClient.GetChatClient(titleGeneratorModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();

        // Structured-output chat client: shared by classification (MAF RunAsync<T>) plus
        // direct JSON-schema callers such as field extraction and slug suggestion. All are
        // tool-free and prompts are document/admin-input-derived (unique per call), so
        // FunctionInvocation and DistributedCache are pure overhead. OTel + Logging stay
        // so each structured call shows up as a clean chat <model> span. Falls back to
        // ChatModelId when StructuredModelId is unset; production teams running tight
        // token budgets can point this at a smaller / cheaper model that can still
        // satisfy schema-bound output.
        var structuredModelId = configuration["Vault:Extract:StructuredModelId"]
            ?? chatModelId;
        context.Services.AddKeyedChatClient(
            VaultExtractConsts.StructuredChatClientKey,
            _ => openAIClient.GetChatClient(structuredModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();

        // Vision OCR chat client: consumed by VisionLlmOcrProvider via
        // [FromKeyedServices(VisionLlmOcrConsts.VisionChatClientKey)] to transcribe photos / thermal
        // receipts / image-only PDFs that layout OCR (PP-StructureV3) fails on (#259). Reuses the same
        // OpenAIClient (endpoint/key) but REQUIRES an explicit vision-capable model id — it must NOT fall
        // back to ChatModelId, because the main chat model (e.g. DeepSeek-V3) may have no vision support.
        // Fail fast if unset, mirroring the provider-mandatory check above. Only wired because VisionLlm is
        // the enabled OCR provider; a host that switches back to PaddleOCR/Azure can drop this block.
        var visionOcrModelId = configuration["Vault:Extract:VisionOcrModelId"];
        if (string.IsNullOrWhiteSpace(visionOcrModelId))
        {
            throw new AbpException(
                "VisionLlm is the configured OCR provider but Vault:Extract:VisionOcrModelId is not set. " +
                "Point it at a vision-capable model (e.g. Qwen/Qwen3-VL-8B-Instruct on SiliconFlow) in " +
                "host/src/appsettings.Development.json (git-ignored), user-secrets, or environment variables. " +
                "See docs/en/text-extraction/ocr-vision-llm.md.");
        }
        context.Services.AddKeyedChatClient(
            VisionLlmOcrConsts.VisionChatClientKey,
            _ => openAIClient.GetChatClient(visionOcrModelId).AsIChatClient())
            .UseOpenTelemetry()
            .UseLogging();
    }

    // OTel export pipeline. MAF (Microsoft.Agents.AI) and Microsoft.Extensions.AI (gen_ai.*
    // spans from the chat-client UseOpenTelemetry decorators above) emit signals. Without an
    // exporter wired here they are silently dropped.
    private void ConfigureOpenTelemetry(ServiceConfigurationContext context, IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry");
        if (!section.GetValue("Enabled", defaultValue: false))
        {
            return;
        }

        var endpointValue = section["Otlp:Endpoint"];
        var useOtlp = !string.IsNullOrWhiteSpace(endpointValue);
        var useConsole = section.GetValue("ConsoleExporter", defaultValue: false);

        var serviceVersion = typeof(VaultExtractHostModule).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var otel = context.Services.AddOpenTelemetry();

        otel.ConfigureResource(resource => resource
            .AddService(serviceName: "Dignite.Vault.Extract", serviceVersion: serviceVersion));

        otel.WithTracing(tracing =>
        {
            tracing
                .AddSource("Experimental.Microsoft.Agents.AI")
                .AddSource("Microsoft.Agents.AI")
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("Dignite.Vault.Extract.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                tracing.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                tracing.AddConsoleExporter();
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddMeter("Dignite.Vault.Extract.*")
                .AddMeter("Experimental.Microsoft.Agents.AI")
                .AddMeter("Microsoft.Agents.AI")
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter("Microsoft.Extensions.AI")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                metrics.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                metrics.AddConsoleExporter();
            }
        });
    }

    private static void ConfigureOtlpExporter(
        OpenTelemetry.Exporter.OtlpExporterOptions options,
        IConfigurationSection section)
    {
        var endpoint = section["Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        var protocol = section["Otlp:Protocol"];
        if (string.Equals(protocol, "HttpProtobuf", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }
        else if (string.Equals(protocol, "Grpc", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        }
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();
        app.UseForwardedHeaders();
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        if (!env.IsDevelopment())
        {
            app.UseErrorPage();
        }

        app.UseCorrelationId();
        app.UseRouting();
        // #433: rate limiter, after UseRouting so it can read the endpoint's RequireRateLimiting metadata.
        // Only the /mcp endpoint opts in (below); every other endpoint is unaffected.
        app.UseRateLimiter();
        app.UseStaticFiles();
        app.UseAbpStudioLink();
        app.UseAbpSecurityHeaders();
        app.UseCors();
        // A Bearer-carrying /mcp request is routed to OpenIddict validation by the cookie ForwardDefaultSelector
        // (see ConfigureAuthentication), so its principal flows through UseDynamicClaims below.
        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (IsMultiTenant)
        {
            app.UseMultiTenancy();
        }

        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Extract API");
            options.OAuthClientId(context.GetConfiguration()["AuthServer:SwaggerClientId"]);
        });

        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        app.UseConfiguredEndpoints(endpoints =>
        {
            // MCP export endpoint (Streamable HTTP). Reuse the host's existing OpenIddict Bearer:
            // RequireAuthorization enforces authentication at the endpoint, with authenticate using the default policy and preserving dynamic-claims enrichment.
            // Tool / resource method bodies still perform explicit permission assertions as fail-closed double insurance.
            // #278: McpDiscoveryChallengeMarker lets 401 responses without token be routed by McpDiscoveryAuthorizationResultHandler
            // to McpAuth challenge, injecting the `WWW-Authenticate: Bearer resource_metadata="..."` discovery pointer.
            // #433: RequireRateLimiting scopes the /mcp rate limiter to this endpoint, covering the #278
            // discovery-401 path and all /mcp traffic in one place.
            endpoints.MapMcp("/mcp")
                .RequireAuthorization()
                .RequireRateLimiting(McpRateLimiterDefaults.PolicyName)
                .WithMetadata(McpDiscoveryChallengeMarker.Instance);
        });
    }
}
