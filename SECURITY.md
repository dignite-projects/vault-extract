# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| 0.2.x   | ✅        |
| 0.1.x   | ✅        |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, use GitHub's private vulnerability reporting for this repository:

1. Go to [https://github.com/dignite-projects/vault-extract/security/advisories/new](https://github.com/dignite-projects/vault-extract/security/advisories/new)
2. Fill in the advisory form with as much detail as you can:
   - A description of the issue and its impact
   - Steps to reproduce (a minimal proof of concept helps a lot)
   - Affected component (e.g. REST API, MCP server, an OCR provider, the Angular UI) and version / commit
   - Any suggested remediation, if you have one

Reports about the LLM call paths (prompt injection, tenant-isolation bypass, permission bypass via MCP tools) are explicitly in scope — see the security covenant in [CLAUDE.md](./CLAUDE.md) and `.claude/rules/llm-call-anti-patterns.md` for the guarantees the project intends to uphold.

## What to expect

- We will acknowledge your report through the advisory thread, normally within **7 days**.
- We will assess the report, keep you informed of progress, and work with you on a fix and coordinated disclosure. This is a volunteer-maintained open-source project, so exact timelines depend on severity and maintainer availability — critical issues are prioritized.
- Once a fix is released, the advisory will be published and you will be credited (unless you prefer otherwise).

Please give us a reasonable opportunity to address the issue before any public disclosure.
