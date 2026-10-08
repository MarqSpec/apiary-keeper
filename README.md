# Apiary-Keeper

Apiary-Keeper is a web app for an owner-operated Texas apiary. It keeps one record of the business's yards,
colonies and inspections, and is planned to grow into treatments, harvests, sales, a family group chat, a customer
website and, later, AI assistants that draft records for a person to approve.

`ApiaryCopilot` is its internal code name: the solution, projects, namespaces and database names keep it, while the
app, its API description, the container images (`apiary-keeper`, `apiary-keeper-proxy`) and the host names use
Apiary-Keeper.

## What it does today

This is an early prototype. Built so far:

- **Yards** — each yard with its location, access and forage notes, and its landowner's contact and terms.
- **Colonies** — register a colony or a captured swarm; split, merge, requeen and move it. Every change is added to
  the colony's history, which is never rewritten, so its lineage and its queens can always be traced.
- **Inspections** — record what an inspection found (queen and eggs seen, brood, food stores, temperament, signs of
  disease, actions taken), with photos and short video taken on the phone. An inspection is recorded once and not
  changed afterwards.
- **Sign-in** — through Keycloak, the identity provider: a browser signs in through the server and holds only a
  cookie; an automated client calls with a Keycloak-issued bearer token. The yard, colony, inspection and media
  requests serve a signed-in `owner` or `helper` of an account, acting as that account's user. Sign-in is a setting:
  where it is off, those requests answer `503` and nothing is read or written.
- **Invitations** — an account's owner invites a helper or a grower by email; Keycloak creates the user in the
  owner's account and emails the invitation. An owner is created by the Keycloak administrator, never by the app.
- **The family app** — an installable web app for phone and desktop, in a honey-coloured light and dark theme, with
  screens for yards, colonies, a colony's history and the inspection capture screen. It has no sign-in screen yet: a
  user signs in by opening `/api/auth/login` first.

Nothing has been deployed. Staging is scripted and runs behind a shared password until sign-in is set up there (see
[`DEPLOYMENT.md`](DEPLOYMENT.md)).

What the system is meant to do in full, and in which phase, is in [`REQUIREMENTS.md`](REQUIREMENTS.md); how it is
put together is in [`ARCHITECTURE.md`](ARCHITECTURE.md); the API's contract and the services it depends on are in
[`INTERFACES.md`](INTERFACES.md); the rules the code follows are in [`CONVENTIONS.md`](CONVENTIONS.md); how to
configure, deploy and roll it back is in [`DEPLOYMENT.md`](DEPLOYMENT.md).

## What is in this repository

| Path | What it holds |
|---|---|
| [`src/ApiaryCopilot.Api/`](src/ApiaryCopilot.Api/) | The server: an ASP.NET Core API, with sign-in and invitations, that also serves the family app |
| [`src/ApiaryCopilot.Domain/`](src/ApiaryCopilot.Domain/) | The business rules and records, with no dependencies |
| [`src/ApiaryCopilot.Infrastructure/`](src/ApiaryCopilot.Infrastructure/) | Storage: PostgreSQL through EF Core, its migrations, and the media bucket |
| [`src/pwa/`](src/pwa/) | The family app: React, TypeScript and Material UI, built with Vite |
| [`src/tests/`](src/tests/) | Unit tests and integration tests |
| [`proxy/`](proxy/) | The nginx reverse proxy that sits in front of the server |
| [`keycloak/import/`](keycloak/import/) | The local and test Keycloak realm, `apiary-keeper-dev`: invented users, roles and clients, never a real realm |
| [`Dockerfile`](Dockerfile), [`compose.yaml`](compose.yaml) | The server's image, and the local stack: proxy, server, PostgreSQL, a local media bucket, Keycloak and a local mail catcher |
| [`scripts/`](scripts/) | Deployment, database migration, smoke-test and rollback-check scripts, with their tests in `scripts/tests/` |
| [`site/prd/`](site/prd/) | A static page describing the proposal in plain language |

Code comments cite requirement IDs (`UC-n`, `FR-n`, `NFR-n`), defined in [`REQUIREMENTS.md`](REQUIREMENTS.md);
design decisions (`D-n`), recorded in [`ARCHITECTURE.md`](ARCHITECTURE.md); the API's sections (`D.1`, `F.3`), in
[`INTERFACES.md`](INTERFACES.md); the code's rules (`§4.3`, `§8.1`), in [`CONVENTIONS.md`](CONVENTIONS.md); and the
operating sections (`§1`, `§3`, `§9`), in [`DEPLOYMENT.md`](DEPLOYMENT.md).

## Running it locally

You need the .NET SDK (the version is pinned in [`global.json`](global.json)), Node.js 24 for the family app, and
Docker for the local stack and the integration tests.

**First, a `.env` file.** It is never committed; [`.env.example`](.env.example) lists the names. The local stack
refuses to start without `POSTGRES_PASSWORD`, `MINIO_ROOT_USER` and `MINIO_ROOT_PASSWORD` (8 or more characters), so
set them to any local values:

```bash
cp .env.example .env   # then set POSTGRES_PASSWORD, MINIO_ROOT_USER and MINIO_ROOT_PASSWORD
```

**Everything in Docker** — the same images that would be deployed:

```bash
docker compose up --build
```

Open http://localhost:5080: the proxy, in front of the server and the family app. Everything is published on
`127.0.0.1` only:

- PostgreSQL on `127.0.0.1:5432`;
- the local media bucket (MinIO) on `127.0.0.1:9000` — phones and browsers upload photos and video straight to the
  bucket on the server's presigned URLs, never through the server;
- Keycloak on `http://127.0.0.1:8180`, with the invented realm `apiary-keeper-dev` imported from `keycloak/import/`
  (one invented user per role; its passwords and secrets are test values for that realm alone);
- Mailpit on `http://127.0.0.1:8025`, the inbox of every mail Keycloak sends — invitations included — none of it
  delivered.

The server in compose runs with sign-in **off**, so its yard, colony, inspection and media requests answer `503` and
every screen says the server is not ready; to use them, run the server from source with sign-in on, as
[`DEPLOYMENT.md`](DEPLOYMENT.md) §1 *Signing in locally* shows. The proxy answers `502` for the few seconds before the
server is listening. `docker compose down -v` stops the stack and deletes its local data.

**The server on its own**, with the .NET SDK. Run as `Development` it applies the database migrations when it
starts, so it needs PostgreSQL:

```bash
dotnet build src/ApiaryCopilot.slnx
docker compose up -d postgres
export ConnectionStrings__ApiaryCopilot="Host=localhost;Port=5432;Database=apiary_copilot;Username=apiary;Password=<your POSTGRES_PASSWORD>"
dotnet run --project src/ApiaryCopilot.Api --urls http://localhost:5080
```

**The family app** with live reload. The dev server sends `/api` to the server at `http://localhost:5080`:

```bash
cd src/pwa
npm ci
npm run dev        # http://localhost:5173
```

**Who the server acts for.** There is no configured account: each yard, colony, inspection and media request acts
for the signed-in caller's account (the `account_id` in its Keycloak token), as the user of that account whose
`ExternalSubject` is the token's subject (`sub`). A caller who is not signed in gets `401`; a `grower` or `customer`,
or a caller whose account or user has no row in the database, gets `403`; with sign-in off, `503`. An invitation
writes the invited user's row with its subject; locally you can also add the rows yourself, with invented values
([`DEPLOYMENT.md`](DEPLOYMENT.md) §1). Every variable the server reads is listed in [`DEPLOYMENT.md`](DEPLOYMENT.md) §3.

## Testing it

```bash
dotnet test src/tests/ApiaryCopilot.UnitTests
dotnet test src/tests/ApiaryCopilot.IntegrationTests   # needs Docker: starts its own throwaway PostgreSQL, MinIO, Keycloak and Mailpit
dotnet format src/ApiaryCopilot.slnx --verify-no-changes

cd src/pwa
npm ci
npm run lint && npm run typecheck && npm test && npm run build
cd ../..

bash scripts/tests/check-migration-sql-test.sh
bash scripts/tests/deploy-test.sh
bash scripts/tests/rollback-compat-test.sh   # needs jq and openssl
bash scripts/tests/migrate-test.sh           # needs Docker
```

The shell tests cover the migration check, the deploy script (against a stand-in for the Railway CLI), the rollback
check (against stand-ins for Docker, Keycloak and the server) and the migration script (against a throwaway
PostgreSQL). `dotnet test src/tests/ApiaryCopilot.IntegrationTests --filter "Deployment=None"` runs the integration
tests that need nothing beyond the machine.

The unit tests also run inside the image build: `docker compose --profile test build unit-tests`. The proxy's
configuration tests run with `docker build --target test proxy`. Tests use invented data only.

The build treats every compiler and analyzer warning as an error, and every project carries the one version set in
[`Directory.Build.props`](Directory.Build.props) (`<Version>`), which the family app's `package.json` and the image
tags repeat. The conventions the code follows — errors, logging, tests, schema changes — are in
[`CONVENTIONS.md`](CONVENTIONS.md).

## License

This copy carries no licence file. Its use is governed by the written agreement between the parties.
