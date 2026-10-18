# Guide for coding agents

Short orientation for automated assistants working in this repository. Read the linked docs for detail; this file only routes you and states repo-wide expectations.

## Where to look

| Document | Use it for |
|---|---|
| [`README.md`](README.md) | What the app does, repo layout, local run, and the full test checklist |
| [`REQUIREMENTS.md`](REQUIREMENTS.md) | Requirement IDs (`UC-n`, `FR-n`, `NFR-n`) the code cites |
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | System shape, phases, and design decision IDs (`D-n`) |
| [`INTERFACES.md`](INTERFACES.md) | HTTP API, external services, and interface section IDs |
| [`CONVENTIONS.md`](CONVENTIONS.md) | Coding rules, errors, logging, tests, and schema changes |
| [`DEPLOYMENT.md`](DEPLOYMENT.md) | Configuration variables, deploy, migrate, and rollback |

Supporting files agents often need: [`.env.example`](.env.example) (local env names; copy to `.env`, never commit `.env`), [`global.json`](global.json) (.NET SDK pin), [`Directory.Build.props`](Directory.Build.props) (solution version).

## Before you finish

Run the checks that match what you changed, then prefer the full sequence in [`README.md`](README.md) *Testing it* when in doubt.

**Server (.NET)**

```bash
dotnet build src/ApiaryCopilot.slnx
dotnet test src/tests/ApiaryCopilot.UnitTests
dotnet test src/tests/ApiaryCopilot.IntegrationTests   # Docker required
dotnet format src/ApiaryCopilot.slnx --verify-no-changes
```

**Family app (`src/pwa/`)**

```bash
cd src/pwa && npm ci
npm run lint && npm run typecheck && npm test && npm run build
```

**Deploy and migration scripts** (when you touch `scripts/` or migrations): see the `bash scripts/tests/…` commands in [`README.md`](README.md).

## Working here

- Keep diffs small and scoped to the task; match existing style and patterns in [`CONVENTIONS.md`](CONVENTIONS.md).
- Treat warnings as errors; do not weaken analyzers or skip formatting to green the build.
- Never commit secrets, real credentials, or customer data. Use invented values from the docs and [`.env.example`](.env.example).
- Prefer citing requirement, decision, and section IDs from the docs when explaining or commenting on behavior the code already references.
