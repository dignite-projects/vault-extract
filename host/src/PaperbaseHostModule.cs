using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Host.Data;
using Dignite.Paperbase.EntityFrameworkCore;
using Dignite.Paperbase.Host.HealthChecks;
using Dignite.Paperbase.Host.Localization;
using Dignite.Paperbase.Localization;
using Dignite.Paperbase.Ocr.VisionLlm;
using Dignite.Paperbase.TextExtraction;
using Volo.Abp.Localization;
using Dignite.Paperbase.TextExtraction.ElBrunoMarkItDown;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
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
using Volo.Abp.Timing;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;
using ModelContextProtocol.AspNetCore;

namespace Dignite.Paperbase.Host;

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

    // Entity Framework Core packages for the used modules
    typeof(AbpAuditLoggingEntityFrameworkCoreModule),
    typeof(AbpIdentityEntityFrameworkCoreModule),
    typeof(AbpOpenIddictEntityFrameworkCoreModule),
    typeof(AbpFeatureManagementEntityFrameworkCoreModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule),
    typeof(AbpSettingManagementEntityFrameworkCoreModule),
    typeof(AbpBackgroundJobsEntityFrameworkCoreModule),
    typeof(AbpBlobStoringFileSystemModule),
    typeof(AbpEntityFrameworkCoreSqlServerModule),

    // Paperbase core modules
    typeof(PaperbaseHttpApiModule),
    typeof(PaperbaseMcpModule),          // MCP 出口适配器（与 HttpApi REST 出口平行）
    typeof(PaperbaseApplicationModule),
    typeof(PaperbaseEntityFrameworkCoreModule),

    // Paperbase infrastructure modules
    typeof(PaperbaseTextExtractionModule),
    typeof(PaperbaseTextExtractionElBrunoMarkItDownModule),
    typeof(PaperbaseVisionLlmOcrModule)                  // vision-LLM OCR（照片/票据/图片型 PDF），当前默认 OCR provider（#259）。IOcrProvider 互斥：切换 provider 时同步 .csproj ProjectReference + ConfigureAI keyed vision IChatClient。详见 docs/ocr-vision-llm.md
    // typeof(PaperbasePaddleOcrModule),                 // 本地 PaddleOCR sidecar（免费 CPU，PP-StructureV3）；切回时取消注释、注释掉 VisionLlm，并恢复其 .csproj ProjectReference
    // typeof(PaperbaseAzureDocumentIntelligenceModule), // 云方案（高精度），切换时同步在 .csproj 注释 / 启用 ProjectReference
)]
public class PaperbaseHostModule : AbpModule
{
    /* Single point to enable/disable multi-tenancy */
    public const bool IsMultiTenant = false;

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(PaperbaseHostResource)
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                options.AddAudiences("Paperbase");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
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

        PaperbaseHostGlobalFeatureConfigurator.Configure();
        PaperbaseHostModuleExtensionConfigurator.Configure();
        PaperbaseHostEfCoreEntityExtensionMappings.Configure();
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
    }

    // #221：上传请求体上限作为 Kestrel / Form 层 backstop。真正的 fail-closed 友好错误在
    // DocumentAppService.UploadAsync（按 DocumentConsts.MaxUploadFileBytes 校验后抛 BusinessException）；
    // 此处留一段 multipart 信封余量（+1 MiB），使正常的最大尺寸文件不会在到达应用层前被 413 截断，
    // 同时把 ASP.NET Core 默认的隐式 ~28.6 MB（Kestrel）/ 128 MB（Form）上限收敛为显式、可审计的边界。
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

    // 启用 ABP transactional outbox + inbox（issue #188）：
    // - publish 路径在调用方 UoW 内 → 事件写入 AbpEventOutbox 表与业务变更原子持久化
    // - 后台 worker 扫表真正投递到消息中间件，保证 at-least-once 投递
    // 下游消费方按 (DocumentId, EventType, EventTime) 自行幂等以处理重投。
    private void ConfigureDistributedEventBus()
    {
        Configure<AbpDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                config.UseDbContext<Data.PaperbaseHostDbContext>();
            });

            options.Inboxes.Configure(config =>
            {
                config.UseDbContext<Data.PaperbaseHostDbContext>();
            });
        });
    }

    private void ConfigureHealthChecks(ServiceConfigurationContext context)
    {
        context.Services.AddPaperbaseHealthChecks();
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
        context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
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
                .Add<PaperbaseHostResource>("en")
                .AddBaseTypes(typeof(PaperbaseResource))
                .AddVirtualJson("/Localization/PaperbaseHost");

            options.DefaultResourceType = typeof(PaperbaseHostResource);

            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "Chinese (Simplified)"));
            options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "Chinese (Traditional)"));
            options.Languages.Add(new LanguageInfo("ja", "ja", "日语"));
        });

    }

    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(PaperbaseHostModule).Assembly);
        });
    }

    private void ConfigureSwagger(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAbpSwaggerGenWithOAuth(
            configuration["AuthServer:Authority"]!,
            new Dictionary<string, string>
            {
                {"Paperbase", "Paperbase API"}
            },
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Paperbase API", Version = "v1" });
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
        context.Services.AddDataProtection().SetApplicationName("Paperbase");
    }

    private void ConfigureVirtualFiles(IWebHostEnvironment hostingEnvironment)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<PaperbaseHostModule>();
            if (hostingEnvironment.IsDevelopment())
            {
                /* Using physical files in development, so we don't need to recompile on changes */
                options.FileSets.ReplaceEmbeddedByPhysical<PaperbaseHostModule>(hostingEnvironment.ContentRootPath);
            }
        });
    }

    private void ConfigureEfCore(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<Data.PaperbaseHostDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                // Issue #206：移除 native json 列后不再需要 SQL Server 2025 compatibility level 170。
                // 持久化层只用普通关系型列（typed columns + nvarchar(max)），跨任意 SQL Server 版本 / 关系型
                // 数据库可移植——部署目标不再硬锁 SQL Server 2025。
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
        // target a non-OpenAI wire protocol replace this whole method (see docs/ai-provider.md) and
        // own their own validation.
        var endpoint = configuration["PaperbaseAI:Endpoint"];
        var apiKey = configuration["PaperbaseAI:ApiKey"];
        var chatModelId = configuration["PaperbaseAI:ChatModelId"];
        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(apiKey)
            || apiKey == "YOUR_API_KEY"
            || string.IsNullOrWhiteSpace(chatModelId))
        {
            throw new AbpException(
                "Paperbase requires an LLM provider before it can start: document classification and " +
                "field extraction have no non-LLM fallback. Set PaperbaseAI:Endpoint, PaperbaseAI:ApiKey, " +
                "and PaperbaseAI:ChatModelId in host/src/appsettings.Development.json (git-ignored), " +
                "user-secrets, or environment variables. For a zero-cost local option, point Endpoint at a " +
                "local Ollama /v1 endpoint and use any non-empty token as the key. See docs/ai-provider.md.");
        }

        var openAIClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        // Title-generator chat client: single-shot, tool-free, prompt-unique-per-call so
        // distributed caching is a net negative. Consumed by
        // DocumentTextExtractionBackgroundJob.TryGenerateTitleAsync via
        // [FromKeyedServices(PaperbaseAIConsts.TitleGeneratorChatClientKey)]. Falls back
        // to ChatModelId when TitleGeneratorModelId is unset; hosts that want to cut cost
        // can point this at a small fast model (e.g. Qwen3-8B).
        var titleGeneratorModelId = configuration["PaperbaseAI:TitleGeneratorModelId"]
            ?? chatModelId;
        context.Services.AddKeyedChatClient(
            PaperbaseAIConsts.TitleGeneratorChatClientKey,
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
        var structuredModelId = configuration["PaperbaseAI:StructuredModelId"]
            ?? chatModelId;
        context.Services.AddKeyedChatClient(
            PaperbaseAIConsts.StructuredChatClientKey,
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
        var visionOcrModelId = configuration["PaperbaseAI:VisionOcrModelId"];
        if (string.IsNullOrWhiteSpace(visionOcrModelId))
        {
            throw new AbpException(
                "VisionLlm is the configured OCR provider but PaperbaseAI:VisionOcrModelId is not set. " +
                "Point it at a vision-capable model (e.g. Qwen/Qwen3-VL-8B-Instruct on SiliconFlow) in " +
                "host/src/appsettings.Development.json (git-ignored), user-secrets, or environment variables. " +
                "See docs/ocr-vision-llm.md.");
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

        var serviceVersion = typeof(PaperbaseHostModule).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var otel = context.Services.AddOpenTelemetry();

        otel.ConfigureResource(resource => resource
            .AddService(serviceName: "Dignite.Paperbase", serviceVersion: serviceVersion));

        otel.WithTracing(tracing =>
        {
            tracing
                .AddSource("Experimental.Microsoft.Agents.AI")
                .AddSource("Microsoft.Agents.AI")
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("Dignite.Paperbase.*")
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
                .AddMeter("Dignite.Paperbase.*")
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
        app.UseStaticFiles();
        app.UseAbpStudioLink();
        app.UseAbpSecurityHeaders();
        app.UseCors();
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
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Paperbase API");
            options.OAuthClientId(context.GetConfiguration()["AuthServer:SwaggerClientId"]);
        });

        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        app.UseConfiguredEndpoints(endpoints =>
        {
            // MCP 出口端点（Streamable HTTP）。复用 host 现有 OpenIddict Bearer：
            // RequireAuthorization 在端点强制鉴权；tool / resource 方法体内再做显式权限断言（fail-closed 双保险）。
            endpoints.MapMcp("/mcp").RequireAuthorization();
        });
    }
}
