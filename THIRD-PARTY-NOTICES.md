# Third-Party Notices

This project (Dignite Vault Extract) is licensed under the
[GNU Lesser General Public License v3.0 (LGPL-3.0-only)](LICENSE).

It depends on the following third-party packages. Each remains under its
own license, listed below. This notice does not change or override any of
those licenses.

> Compiled from `Directory.Packages.props` (backend/.NET) and
> `angular/package.json` (frontend). Build-time-only tooling (Fody,
> ConfigureAwait.Fody, SourceLink) and test-only packages (xunit, Shouldly,
> NSubstitute, TestHost, etc.) are not distributed with the product and are
> omitted. License data below was compiled from each package's public
> NuGet/npm metadata at the time of writing — re-verify with a scanner
> (e.g. `nuget-license`, `license-checker`) before relying on this for a
> formal audit, since transitive dependencies and license terms can change
> between package updates.

## .NET (NuGet) dependencies

### LGPL-3.0

These are part of the ABP Framework itself — same license family as this
repository, so no additional obligation beyond normal LGPL compliance
(unmodified, consumed as separate assemblies).

| Package | Notes |
|---|---|
| Volo.Abp.* (Core, Ddd.*, EntityFrameworkCore.*, AspNetCore.*, Identity.*, Account.*, PermissionManagement.*, FeatureManagement.*, SettingManagement.*, TenantManagement.*, BackgroundJobs.*, BlobStoring.*, Auditing, AuditLogging.*, Authorization, Autofac, Features, Guids, Validation, VirtualFileSystem, EventBus.Abstractions, Gdpr.Abstractions, Http.Client, Mapperly, Swashbuckle) | ABP Framework core packages |
| Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite | Free ABP theme, same repo/license as ABP Framework |
| Volo.Abp.Studio.Client.AspNetCore | Confirmed `LGPL-3.0-only` on NuGet |

### LGPL-2.1-or-later

| Package | Notes |
|---|---|
| UTF.Unknown | Tri-licensed MPL-1.1 / GPL-2.0-or-later / LGPL-2.1-or-later. Consumed here under the LGPL arm, which is compatible with this repository's LGPL-3.0-only license (see the `Directory.Packages.props` comment next to this entry). |

### Apache-2.0

| Package |
|---|
| OpenTelemetry.Extensions.Hosting / Exporter.Console / Exporter.OpenTelemetryProtocol / Instrumentation.AspNetCore / Instrumentation.Http |
| PdfPig (UglyToad.PdfPig) |
| IdentityModel |
| KubernetesClient |
| Serilog.AspNetCore, Serilog.Sinks.Async |
| AspNetCore.HealthChecks.UI / UI.Client / UI.InMemory.Storage |

### MIT

| Package |
|---|
| Microsoft.Extensions.AI, Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.AI.OpenAI |
| Microsoft.Agents.AI |
| ModelContextProtocol, ModelContextProtocol.AspNetCore |
| Microsoft.EntityFrameworkCore.InMemory / Proxies / Tools |
| Microsoft.Extensions.FileProviders.Embedded, Microsoft.Extensions.Options.ConfigurationExtensions, Microsoft.Extensions.Http |
| Azure.AI.DocumentIntelligence |
| ElBruno.MarkItDotNet, ElBruno.MarkItDotNet.Excel |
| DocumentFormat.OpenXml |
| PDFtoImage, SkiaSharp, SkiaSharp.NativeAssets.Linux |
| ClosedXML |

### BSD-2-Clause

| Package |
|---|
| Markdig |

## Angular / npm dependencies

The frontend (`angular/`) consumes the official `@abp/ng.*` packages
(same **LGPL-3.0** family as the backend) plus the standard Angular
ecosystem, which is predominantly **MIT**. Given the size of the npm
dependency tree, this file does not enumerate every transitive package —
run `npx license-checker --summary` in `angular/` for a full, current
breakdown before a formal license audit.

## Compliance notes

- This repository's own source code is licensed under **LGPL-3.0-only**
  (see [LICENSE](LICENSE)); the dependencies above do not change that.
- Apache-2.0 and MIT dependencies are permissive and impose no copyleft
  obligation on this repository.
- LGPL/tri-licensed dependencies (ABP Framework packages, UTF.Unknown) are
  consumed unmodified via standard package references (dynamic linking
  equivalent); no source-disclosure obligation is triggered unless their
  own source is modified and redistributed.
