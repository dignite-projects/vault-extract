<!--
  Thanks for contributing to Dignite Vault Extract!
  Keep PRs scoped to one concern. See CONTRIBUTING.md and CLAUDE.md before opening.
-->

## Summary

<!-- What does this change do, and why? -->

## Related issue

<!--
  Link the issue this PR addresses (e.g. "Closes #123").
  Required for any change that touches a channel boundary — the OCR / text-extraction
  pipeline, exit contracts (REST / MCP / EventBus / Webhook, event payloads), the field
  architecture, the document-type tier system, the Markdown-first contract, or the
  security covenant. Those must reach consensus in an issue first (CONTRIBUTING.md → Issue-first principle).
-->

## Checklist

- [ ] Backend tests pass locally (`dotnet test Dignite.Vault.Extract.slnx`)
- [ ] Frontend checks pass if the Angular workspace was touched (`cd angular && npm test && npm run lint`)
- [ ] Follows the conventions in `.claude/rules/` and CLAUDE.md (dependency direction, ABP patterns, Markdown-first, LLM-call security covenant)
- [ ] Documentation updated if behavior, configuration, or exit contracts changed
- [ ] No middleware configured outside the host application
- [ ] For boundary-touching changes: a GitHub Issue exists and consensus was reached before coding
