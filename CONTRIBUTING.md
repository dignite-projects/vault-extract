# Contributing to Dignite Vault Extract

Thank you for considering a contribution. This page covers the practical workflow; the architectural contract lives in [CLAUDE.md](./CLAUDE.md) and is the truth source for what belongs in this repository (and what is explicitly out of scope).

## Questions, ideas, and bugs

Not every contribution starts with code. Pick the right channel so the issue tracker stays scoped to actionable work — this mirrors the [issue chooser](https://github.com/dignite-projects/vault-extract/issues/new/choose) and the [Discussions welcome post](https://github.com/dignite-projects/vault-extract/discussions/417):

- **Usage / configuration / "is this in scope?" questions** → [Discussions → Q&A](https://github.com/dignite-projects/vault-extract/discussions/categories/q-a), **not** an Issue.
- **Early ideas, before a formal proposal** → [Discussions → Ideas](https://github.com/dignite-projects/vault-extract/discussions/categories/ideas). Boundary-touching ideas should be floated here first, then promoted to an Issue once there is direction (see [Issue-first principle](#issue-first-principle)).
- **Reproducible bugs / concrete change proposals** → [open an Issue](https://github.com/dignite-projects/vault-extract/issues/new/choose) using a template.
- **Security vulnerabilities** → never a public issue; use [private vulnerability reporting](https://github.com/dignite-projects/vault-extract/security/advisories/new) (see [SECURITY.md](./SECURITY.md)).

## Development environment

Follow [README → Getting started](./README.md#getting-started-local-development). In short: .NET SDK 10, Node.js 20+, SQL Server (LocalDB works), optionally Docker Desktop for the PaddleOCR sidecar and the local OpenTelemetry dashboard. An OpenAI-compatible LLM API key is mandatory — see [AI provider](./docs/en/configuration/ai-provider.md).

After cloning, activate the shared git hooks once:

```bash
git config core.hooksPath .githooks
```

## Running tests

### Backend (xUnit)

All backend test projects are part of the root solution:

```bash
dotnet test Dignite.Vault.Extract.slnx
```

Or run individual projects:

```bash
# Core
dotnet test core/test/Dignite.Vault.Extract.Domain.Tests
dotnet test core/test/Dignite.Vault.Extract.Application.Tests
dotnet test core/test/Dignite.Vault.Extract.EntityFrameworkCore.Tests
dotnet test core/test/Dignite.Vault.Extract.Mcp.Tests
dotnet test core/test/Dignite.Vault.Extract.Ocr.VisionLlm.Tests
```

### Frontend (Vitest)

```bash
cd angular
npm install
npm test          # vitest run
npm run lint
```

## Code conventions

- **ABP conventions** — `.claude/rules/abp-core.md` and the other files under [`.claude/rules/`](./.claude/rules/) are normative (dependency direction, base classes, `IClock`, repositories, anti-patterns). They are written for AI coding assistants but apply equally to human contributors.
- **Architecture rules** — [CLAUDE.md](./CLAUDE.md) defines the channel boundary: Markdown-first data flow, the two-layer document-type model, the exit contracts, and the security covenant for LLM call paths (`.claude/rules/llm-call-anti-patterns.md`).
- Middleware is configured **only** in the host application, never in core modules.

## Issue-first principle

Any change that touches a **channel boundary** — the OCR / text-extraction pipeline, exit contracts (REST / MCP / EventBus / Webhook, event payloads), the field architecture, the document-type tier system, the Markdown-first contract, or the security covenant — must start with a GitHub Issue and reach consensus there **before** any code is written. This is rule 3 of CLAUDE.md's processing rules.

Pure implementation details (bug fixes, wording corrections) don't need an Issue — a descriptive commit message is enough.

Also note CLAUDE.md's "OUT of scope" list: business modules (contract / invoice / HR management), RAG features (vectorization, retrieval, chat), and end-user LLM configuration are not accepted into this repository — downstream consumers build those in their own repositories against the exit contracts.

## Branch naming

Branches follow `<type>/<issue-number>-<short-description>` (issue number is required for boundary-touching changes; may be omitted for minor work with no associated Issue):

| Prefix | When to use |
|---|---|
| `feat/` | New feature |
| `fix/` | Bug fix |
| `chore/` | Build, release, dependency update |
| `docs/` | Documentation only |
| `refactor/` | Refactor with no functional change |
| `ci/` | CI/CD configuration |

Rules: all lowercase, words separated by `-`, no underscores or spaces.

```
feat/312-document-type-filter   ✓
fix/298-ocr-timeout-retry       ✓
chore/release-0.2.0             ✓  (no issue number needed)
Feature_DocumentType            ✗
```

A `post-checkout` hook warns immediately when a branch name is non-conforming, and a `pre-push` hook blocks the push until the branch is renamed (`git branch -m <new-name>`). See the setup step in [Development environment](#development-environment) to activate these hooks.

## Commit style

The repository uses [Conventional Commits](https://www.conventionalcommits.org/): `feat(scope): …`, `fix: …`, `docs: …`, `chore: …`, `refactor(scope): …`. Existing commit descriptions are mostly written in Chinese; both Chinese and English descriptions are accepted — pick whichever expresses the change most clearly.

## AI-assisted contributions

AI coding assistants are welcome. Whatever tools you use, the human who opens the PR is the author and is fully responsible for the contribution: it must meet the same review, testing, and architecture-rule bar (`.claude/rules/`, [CLAUDE.md](./CLAUDE.md)) as any other change — read and understand what you submit.

Listing an AI tool as a commit `Co-Authored-By` trailer is **not required**; the project's git history attributes authorship and accountability to people.

## Pull requests

- Target the `main` branch.
- CI (`.github/workflows/ci.yml`) must pass: it builds the core solution and the host, runs the backend test suites (Domain, Application, EntityFrameworkCore, MCP, OCR VisionLlm, and host), and builds / lints / tests the Angular workspace.
- Keep PRs scoped to one concern, and reference the related Issue (required for boundary-touching changes, see above).
- Use the PR template checklist (`.github/pull_request_template.md`).

## Versioning and releases

The project follows three-part [Semantic Versioning](https://semver.org/) (`MAJOR.MINOR.PATCH`), as declared in [CHANGELOG.md](./CHANGELOG.md). SemVer is not cosmetic here: the version **is** the stability signal to downstream consumers. A breaking change to an exit contract (REST / MCP / EventBus / Webhook, event payloads, `TextExtractionResult` shape) requires a `MAJOR` bump; a backward-compatible addition (e.g. a new nullable named field) is a `MINOR`; a fix that changes no contract is a `PATCH`.

- **Pre-1.0 (`0.y.z`)** — the exit contracts are still being shaped (Webhook is not yet shipped; the field architecture is evolving). Under `0.y.z`, consumers should expect contracts to change. Graduating to `1.0.0` is an earned milestone — all four exits present, the contracts validated against at least one real downstream consumer — not a default for the first release.
- **Pre-release suffixes** — use SemVer pre-release tags for previews on the way to a stable version: `0.1.0-preview.1` → `0.1.0-rc.1` → `0.1.0`. Both NuGet and npm understand their precedence (a suffixed version always ranks below the matching final version) and treat them as non-stable by default.
- **Do not use CalVer** (e.g. `2026.6.0`) — it communicates *when* a release was cut, not whether it is *safe to upgrade*, which is the opposite of what this project's positioning needs.

### Where the version lives

| Property | Segments | Purpose |
|----------|----------|---------|
| `<Version>` in [`common.props`](./common.props) | 3-segment SemVer | The NuGet package version and the value a `v*` tag must match. **This is the release version.** |
| `<AssemblyVersion>` | 4-segment | Keep coarse and stable (e.g. `0.1.0.0`); do not move it for every `MINOR`/`PATCH`, to avoid assembly-binding churn. The 4-segment `1.0.0.0` form belongs here — never as a package version or tag. |
| `<FileVersion>` | 4-segment | Diagnostic only; CI may stamp it from a build number or commit count. |
| `version` in [`angular/packages/vault-extract/package.json`](./angular/packages/vault-extract/package.json) | 3-segment SemVer | The npm package version. **Keep it in lockstep with `<Version>`** as a single product version, until the backend and frontend release cadences genuinely diverge. |

### Cutting a release

1. Move the CHANGELOG `[Unreleased]` section to `## [x.y.z] - YYYY-MM-DD`.
2. Confirm `<Version>` in `common.props` and the npm `version` match the intended release (tags do not drive the version — the release workflow reads it from `common.props`).
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The release workflow (`.github/workflows/release.yml`) triggers on `v*` tags; `workflow_dispatch` only builds artifacts and does not create a GitHub Release.
4. The npm package (`@dignite/vault-extract`) is **published manually** for now — see the header comment in `release.yml`.
