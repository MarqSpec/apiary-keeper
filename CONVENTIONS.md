# Conventions

The rules the code follows, for whoever maintains it. The section numbers are the ones the code's comments cite
(`CONVENTIONS.md §4.3`), so they are not consecutive: a number missing here has no rule the code depends on. How to
build, run and test is in [`README.md`](README.md); the API contract is in [`INTERFACES.md`](INTERFACES.md).

## 1. Toolchain

- **.NET 10**, the SDK pinned in [`global.json`](global.json). `Nullable` is on and every compiler and analyzer
  warning is an error ([`Directory.Build.props`](Directory.Build.props)); `dotnet format --verify-no-changes`
  must pass ([`.editorconfig`](.editorconfig)).
- **The family app:** TypeScript in strict mode, React and Material UI, built with Vite on Node.js 24; ESLint and the
  type check must pass (`npm run lint`, `npm run typecheck`).

## 2. Dependencies

Every dependency has a stated purpose and a known license, and every version is pinned once for the whole
repository:

- **.NET:** central package management — versions only in
  [`Directory.Packages.props`](Directory.Packages.props), never in a project file. The libraries stay on the .NET
  10 line. Where a newer major version changes the license, the range is capped: FluentAssertions stays below 8.0.
- **The family app:** [`src/pwa/package.json`](src/pwa/package.json) and its committed `package-lock.json`;
  install with `npm ci`, never `npm install`, so the lock decides.

## 3. Code

- **The API layer is thin.** An endpoint validates and maps its arguments, calls one service and maps the result.
  Business rules live in `ApiaryCopilot.Domain`, persistence in `ApiaryCopilot.Infrastructure`; neither references
  ASP.NET Core.
- **Parse, don't validate:** raw arguments become typed domain values at the boundary.
- **Errors:** catch the narrowest exception you can act on, never swallow one, and let it propagate when the caller
  cannot recover. At the API boundary failures become the categorized results of §4.3; a caller never sees a raw
  exception, a stack trace, a secret or personal data.
- **No shared mutable state** without synchronization: requests run concurrently.
- **Fluent APIs are preferred** where a library offers one (EF Core model configuration, queries, assertions), over
  attributes or embedded query strings.
- **Each third-party package is wrapped** behind a small class or module of ours, named for what the code needs, and
  the rest of the code calls the wrapper. The wrapper has its own tests against the real package, pinning the
  behaviour the code relies on, so an upgrade that changes it fails a unit test, not production. Examples: the AWS
  SDK only in `S3MediaStorage` (behind `IMediaStorage`); `@tanstack/react-query` only through
  `src/pwa/src/lib/query.ts`. A package used in exactly one place, or whose API is already what the code needs, is not
  wrapped.
- **C#:** records and `init`-only members for data, `sealed` by default, `required` members rather than nullable
  constructor arguments.

## 4. The API

### 4.2 Design

- **One endpoint, one operation** that means something to the user, named for what it does to the domain.
- **Names are contract.** Once shipped, an operation's name, its parameters and its result shape are public;
  changing any of them is a breaking change.
- **Every endpoint and parameter is described** (units, formats, defaults) in the generated OpenAPI document.
- **Arguments are untrusted.** Validate ranges, lengths and formats at the boundary, and that referenced records
  exist and belong to the caller's account. Never splice an argument into a query, a path or a command.
- **What must be trusted comes from the host, never from an argument:** the current account and the current user
  (`ICurrentAccount`, `ICurrentUser`, resolved from the signed-in principal), services and the `CancellationToken`.
- **Every list is bounded:** a `limit` with a documented default and maximum, and a cursor for the next page
  ([`INTERFACES.md`](INTERFACES.md) D.1 *Paging*).
- **Return what the caller needs, not the row:** entities are mapped to result records; the account, audit columns
  and anything sensitive stay behind the service.

### 4.3 Errors

- **Expected failures are categorized results**, each message starting with its category and saying what to do:
  `Validation:` (400), `NotAllowed:` (403), `NotFound:` (404), `Conflict:` (409), `Unavailable:` (503). Not being
  signed in is the authentication handler's own `401`, never a redirect. The message goes to the caller,
  so it carries no secret, personal data or internal detail. The problem results are built in one place,
  `ProblemResults`, never per endpoint.
- **The HTTP form is RFC 9457 problem details**, built with `TypedResults.Problem` (or `ValidationProblem` for field
  errors). A malformed body, an unknown route or method are the framework's own 400, 404 and 405.
- **Anything else is a bug:** it reaches `UseExceptionHandler` and `AddProblemDetails` as a bare 500 with no
  exception detail, and is logged with its type and correlation id.
- **A warning is not an error.** A valid change that may be a mistake only the caller can judge is recorded, and its
  result carries `warnings`, keyed like a validation problem's `errors`, each message starting `Warning:`. What the
  server can judge for itself is a `Validation` refusal.
- **Another account's record is `NotFound`, never `NotAllowed`**: answering "not allowed" would confirm it exists.

The family app explains each category to the user its own way (`ApiFailure` in `src/pwa/src/api/client.ts`), and
shows a warning as a notice that never blocks.

## 5. Outbound calls

Every outbound call has a timeout per attempt, retries only transient faults with exponential backoff and jitter, and
on exhaustion degrades to a categorized `Unavailable` — never a fabricated result and never a silent failure. HTTPS
only; certificate validation is never switched off outside a flagged local setup. The media bucket's policy is in
[`INTERFACES.md`](INTERFACES.md) F.5.

## 6. Configuration

Settings come from environment variables, are typed with the Options pattern and validated at startup
(`ValidateOnStart`), so a misconfiguration stops the server before it serves a request, naming the setting but never
its value. Credentials and endpoints never live in source or in the shipped `appsettings*.json`. Everything the family
app is built with is public, because Vite inlines it into the bundle: never a secret there. The settings are listed
in [`DEPLOYMENT.md`](DEPLOYMENT.md) §3.

## 7. Logging and telemetry

- **Structured logging only**, through `ILogger`: named properties, never string interpolation.
- **No personal data and no credential in a log, a metric or a trace** — never argument values or result content,
  never a client address, never a query string (it can carry a token), never a presigned URL, a bucket key, a token,
  a client secret or an `Authorization` header; never an email address or a name. Users are identified internally by
  an opaque id, never by email address. The proxy's access log follows the same rule. A unit test boots the real host and scans
  its diagnostic output for any URL, key or credential.
- **One correlation scope per request**, opened once at the request boundary.
- **Metric labels are bounded values** (operation name, outcome category), never a user, a record id or free text.
- **OpenTelemetry** exports traces and metrics over OTLP only when an endpoint is configured; Prometheus metrics are
  served at `/metrics` for scraping on the private network.

## 8. Tests

Every test is Arrange-Act-Assert, checks one behavior, uses invented data only, and guards a named failure mode: a
boundary, an invariant or a regression. A suite of happy paths only is not enough.

**Names.** .NET: a class `{ClassUnderTest}Tests` and methods `{Function}_Should{Result}_When{Condition}`
(`GetYard_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchYard`). The family app: `describe('<unit>')` with
`it('should <result> when <condition>')`. A name that needs "and" is two tests. The test projects allow the
underscores (`.editorconfig`).

**Libraries.** .NET: xUnit, FluentAssertions (`.Should()`, never `Assert.*`) and, in the unit tests only, FakeItEasy.
The family app: Vitest with Testing Library — query by role, label or text, interact with `user-event` — and the
network stubbed with msw, where a request no test stubbed fails the test.

### 8.1 Unit tests — `ApiaryCopilot.UnitTests`

- **Fully faked:** no network, no database, no file I/O; deterministic and fast.
- **Endpoints are tested through the interface:** the real host in memory (`WebApplicationFactory<Program>`, the
  tests' `ApiFactory`, with the migrator faked), its stores and current account faked, asserting on what a caller
  sees — routes, status codes, problem categories, the OpenAPI description.
- **Persistence is tested over EF Core's in-memory provider**, under the real `ApiaryCopilotDbContext` model and its
  account filter, with the clock and current account faked. That provider has no SQL, constraints, transactions or
  migrations; anything relying on them belongs to the integration tests.
- **Booting the real host with its shipped configuration** is allowed only for what exists only in the composition:
  where logs go, the registrations, startup validation. Still no network or database.
- **Package wrappers** are tested against the real package (§3).

### 8.2 Integration tests — `ApiaryCopilot.IntegrationTests`

- **The real server, driven over HTTP, against real PostgreSQL, MinIO and Keycloak.** Nothing under test is faked. The
  fixture starts a throwaway `postgres:17`, MinIO and Keycloak (the image and digest `compose.yaml` pins, importing the
  committed invented realm from `keycloak/import/`) on their own Docker network `apiary-it-{port}`, with credentials
  made up per run; applies the committed migrations; seeds invented accounts — one whose acting user's
  `ExternalSubject` is the realm's invented owner's `sub`, and one holding a yard and a colony the server must never
  show; starts the server with sign-in on; and calls it with the owner's bearer token. It removes everything when the
  tests finish. Docker is the only requirement. The server's collection runs with parallelization off.
- **Keycloak-heavy classes run alone**, in the `KeycloakAlone` collection, after the server's collection: they check the
  realm, sign a browser in through Keycloak's own login form, renew and end sessions through Keycloak's admin
  interface, check the service account's grant, and invite a user end to end through a Mailpit (`TestMailbox`). A
  class whose changes must not reach the shared server starts a server fixture of its own.
- **Which build is tested is `APP_SERVER_COMMAND`.** Unset, the server is built and run from source as `Production`.
  To test an image, set the command that runs it; `{port}` is replaced with a free loopback port. A container command
  must join the network `apiary-it-{port}` and pass `-e ConnectionStrings__ApiaryCopilot`, every `-e Media__…`, every
  `-e SignIn__…` and `-e DataProtection__…` setting, which the fixture fills in — it refuses a command without them,
  naming the first missing — and the five `-e KeycloakAdmin__…` settings, without which only the invitation tests
  fail, naming them. [`DEPLOYMENT.md`](DEPLOYMENT.md) §5 has the full command.
- **The test client's per-request timeout is 30 seconds**, sized for a starved 2-core machine: the first request after
  a server starts pays for the first database connection, the EF Core model and cold JIT. It is bounded on purpose, so
  a server that never answers fails the test. **No retry wraps an assertion**: a slow request is waited for, a wrong
  answer fails.
- **Every test class declares what it needs:** `[Trait("Deployment", "None")]` when nothing leaves the machine but a
  child process or a loopback peer. `--filter "Deployment=None"` runs those alone.

### 8.4 A guard is proven in both directions

A guard is anything that turns red to stop something: a test assertion, a lint, a script's refusal. Whoever adds or
changes one checks that it **turns red when what it guards is broken** (and that this assertion is the one that
catches it), **stays green when it is left alone**, and **turns red on a widened case** (another spelling, one more
path). Anchor patterns at both ends; make a check that reports absence prove it ran; confirm the mutation actually
changed the file. Example: `MethodCallScannerTests` are the red controls for the scan that proves nothing calls
`IgnoreQueryFilters`.

## 11. Security

- **Every query is filtered to the current account** by EF Core global query filters (NFR-8, D-8); with no account a
  query returns nothing. The database login can reach every account's rows, so **nothing in production code may call
  `IgnoreQueryFilters`**: a unit test scans the production assemblies and fails on any reference to it.
- **Every apiary row says who wrote it and when** (`IRowStamped`), written only by `ApiaryCopilotDbContext`'s save
  from `ICurrentUser` and `TimeProvider`.
- **The current account is the signed-in caller's `account_id`**, read only from the principal the request
  authenticated with, and the acting user is the user of that account whose `Users.ExternalSubject` is the token's
  `sub` (D-19). Every apiary request group requires a signed-in `owner` or `helper` (the `apiary-field-work` policy);
  a caller whose account has no row, or whose `sub` no user of the account holds, is refused (`403`) before anything
  is read, and with sign-in off there is no account, so every apiary request answers `503`. Nothing configures an
  account.
- **The server runs as a non-root container user**, the proxy too; secrets come only from the environment.
- **No personal data or credential in logs, errors or telemetry** (§7). Presigned URLs are bearer credentials
  ([`INTERFACES.md`](INTERFACES.md) F.3).

## 12. Sign-in and authorization

- **Keycloak is the identity provider** (D-19): OpenID Connect sign-in, email and password or Google, invited users
  only, MFA the owner may require. The server never handles a password.
- **A browser holds only a cookie.** The sign-in backend is in the API: it runs the authorization code flow with PKCE
  as a confidential client and keeps the refresh token server-side beside the session, never in the cookie, a
  response or a log; no ID or access token is kept. The cookie is `HttpOnly`, `SameSite=Lax` and, with
  `SignIn__RequireHttps` on (always beyond a local http Keycloak), `Secure` and `__Host-`-prefixed. **A write that
  carries the session cookie must carry `X-CSRF: 1`** or is refused `403`. Sign-out is a POST.
- **An automated client sends a bearer access token**, validated for its RS256 signature against Keycloak's published
  keys, the issuer (pinned to `SignIn__Authority` by the server's own validator), the audience `SignIn__Audience` and
  its lifetime (a 30-second clock skew). An ID token is never accepted as an access token. Both paths give one
  principal, built in one place (`ApiaryClaims`): `sub`, `account_id` and the roles, so each endpoint's authorization
  is written once.
- **The renewal is the server's own code**, not a library: one renewal per session at a time, never retried, since a
  rotated refresh token is good once; a refusal ends the session.
- **Roles and claims** (D-22): realm roles `owner`, `helper`, `grower`, `customer`, and one `account_id` per user, in
  the ID token and the access token. The server authorizes from them and never looks them up per request.
- **Accounts are created by an owner's invitation**, through the server's Keycloak service account, which may give only
  `helper`, `grower` or `customer`. An `owner` is created by the Keycloak administrator. The service account's secret,
  the client secret and the key ring certificate come only from the environment (§6).
- **Sessions are in memory**, so the server runs as one replica; the Data Protection key ring is in PostgreSQL,
  encrypted with a certificate from the environment, and never logged or exported.

## 14. Versions

Every project, the family app's `package.json` and the image tags carry the one SemVer version set in `<Version>` in
[`Directory.Build.props`](Directory.Build.props). A released version is never rebuilt.

### 14.1 Schema changes are non-destructive

A release migrates the database before it deploys, and rolls back by image, never by a down migration (D-21), so the
previous release's image must keep working on the new schema:

- **Add, never take away, in one release:** new tables, indexes, and columns that are nullable or have a default. No
  drop, rename, truncate, type change or `SET NOT NULL` in the release that introduces the replacement; the removal
  comes in a later release, once no running image uses it.
- **A new kind of stored value** (a new colony event type, a new inspection answer) ships so the previous image
  survives it: it ignores it, or the change ships in two releases — read it first, write it later.
- **The append-only history is never rewritten** (D-2): a correction is a new event, never an `UPDATE` or `DELETE` on
  `ColonyEvents` or `Inspections`.
- **Data is fixed forward**, by a further additive, idempotent migration.

[`scripts/check-migration-sql.sh`](scripts/check-migration-sql.sh) refuses any statement in the generated migration
script that is not an additive shape, unless a line in `scripts/sql/migration-overrides.txt` names the migration and
the reason; [`scripts/rollback-compat.sh`](scripts/rollback-compat.sh) runs the previous image against the migrated
schema. Both guard against accidents, not against a malicious author, and neither can see the stored-value half of the
rule, which needs a person's judgement. The procedure is [`DEPLOYMENT.md`](DEPLOYMENT.md) §9.
