# Deployment

How to configure, deploy, migrate and roll back Apiary-Keeper. **Nothing is deployed yet.** The target is Railway
(see [`ARCHITECTURE.md`](ARCHITECTURE.md), D-5): staging is scripted and ready to set up; Keycloak on Railway and
sign-in on the deployed environments come with Phase 1, and production has no deployment target or migration path
yet. The section numbers below are the ones the code's comments cite.

## 1. What runs

| Service | Image | Purpose |
|---|---|---|
| `proxy` | `ghcr.io/<owner>/apiary-keeper-proxy:X.Y.Z`, from `proxy/Dockerfile` | The public front door to `app`: routes the app's host names to it, sets security headers, limits request size; on staging, the shared-password gate. The proposal page and, from Phase 1, Keycloak have public addresses of their own |
| `app` | `ghcr.io/<owner>/apiary-keeper:X.Y.Z`, from `Dockerfile` | The API, sign-in and the family app; no public address of its own |
| `Postgres` | Railway's PostgreSQL template | Records and the sign-in key ring, on the private network only |
| `media` | A Railway storage bucket | Photos and video, uploaded and downloaded by phones on presigned URLs |
| Keycloak (Phase 1) | Keycloak's own image, with its own PostgreSQL | Sign-in, on its own Railway-assigned address, not behind the proxy |

`<owner>` is the lower-case name of the GitHub account or organisation that owns the repository; both image packages
are private.

Locally, `compose.yaml` runs the same shape on `127.0.0.1`: the proxy on port 5080 in front of the `api` (which
publishes no port), PostgreSQL 17 on 5432, MinIO as the media bucket on 9000 with a one-shot `media-bucket` service
creating its bucket, Keycloak on 8180 and Mailpit on 8025. The third-party images are pinned by digest:

- **MinIO** is a community build (`pgsty/minio`, AGPLv3), unsigned and running as root, used only locally and never
  deployed.
- **Keycloak** (`quay.io/keycloak/keycloak:26.8.0`) runs in development mode with its embedded database and no
  volume, importing the realm `apiary-keeper-dev` from
  [`keycloak/import/apiary-keeper-dev-realm.json`](keycloak/import/apiary-keeper-dev-realm.json) each time the
  container is created. No administrator is seeded, so the admin console has no login: change the realm in the file,
  then `docker compose up -d --force-recreate keycloak`. Its issuer is `http://127.0.0.1:8180/realms/apiary-keeper-dev`.
- **Mailpit** (`axllent/mailpit`) receives Keycloak's mail on SMTP port 1025 and keeps it in memory, shown at
  `http://127.0.0.1:8025`; it never delivers anything.

**The local realm holds invented values only.** Every password and client secret in the file is a test value for
that realm alone, prefixed `synthetic-dev-realm-only-`, and used nowhere else. It holds the realm roles `owner`,
`helper`, `grower` and `customer` (their ids pinned, `0d6f3a52-7a1e-4c1f-9b0e-000000000001` to `…004`); the
`owners` group, which gives `owner`; one invented user per role (`synthetic-owner`, `synthetic-helper`,
`synthetic-grower`, `synthetic-customer`) with an `account_id` attribute — the owner, helper and grower in the
invented account `a91a7c0e-5747-4e00-8000-000000000019`; mappers putting `account_id` and the roles into the ID token
and the access token; the server's confidential client `apiary-keeper-bff` (authorization code with PKCE only,
loopback redirect URIs on any port); an audience mapper putting `apiary-keeper-api` in the access token; the server's
service account `apiary-keeper-api-admin` with the grant of §4 *Keycloak on Railway*; a public browser client; a test
client `apiary-keeper-integration-tests`, the only client with the password grant — which exists in this realm only,
never in a real one (Keycloak's built-in `admin-cli` has it on by default, and the file turns it off); Google stubbed
on `.invalid` hosts; a 30-minute password-reset link, refresh-token rotation on, self-registration off; and token
lifetimes of 5 minutes (access), 30 minutes (idle) and 12 hours (maximum session).

**Signing in locally.** Compose's `api` has sign-in off: behind the proxy it would see the host without its port, and
inside compose it would reach Keycloak at an address the browser never sees. Run the server from source against
compose's PostgreSQL and Keycloak instead:

```bash
docker compose up -d postgres keycloak mailpit
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=apiary-keeper-local-data-protection" \
  -keyout dp.key -out dp.crt && openssl pkcs12 -export -inkey dp.key -in dp.crt -passout pass: -out dp.pfx \
  && rm dp.key dp.crt                       # a throwaway local key ring certificate; never commit it
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__ApiaryCopilot="Host=127.0.0.1;Port=5432;Database=apiary_copilot;Username=apiary;Password=$POSTGRES_PASSWORD"
export SignIn__Authority=http://127.0.0.1:8180/realms/apiary-keeper-dev SignIn__ClientId=apiary-keeper-bff \
  SignIn__ClientSecret=<the apiary-keeper-bff secret in keycloak/import/apiary-keeper-dev-realm.json> \
  SignIn__Audience=apiary-keeper-api SignIn__RequireHttps=false DataProtection__Certificate="$(base64 -w0 dp.pfx)"
export KeycloakAdmin__ClientId=apiary-keeper-api-admin \
  KeycloakAdmin__ClientSecret=<the apiary-keeper-api-admin secret in keycloak/import/apiary-keeper-dev-realm.json> \
  KeycloakAdmin__HelperRoleId=0d6f3a52-7a1e-4c1f-9b0e-000000000002 KeycloakAdmin__GrowerRoleId=0d6f3a52-7a1e-4c1f-9b0e-000000000003 \
  KeycloakAdmin__CustomerRoleId=0d6f3a52-7a1e-4c1f-9b0e-000000000004   # invitations; optional
dotnet run --project src/ApiaryCopilot.Api   # then open http://localhost:<its port>/api/auth/login
```

The yard, colony, inspection and media requests then serve a signed-in `owner` or `helper`, acting as the `Users` row
of its `account_id` whose `ExternalSubject` is its `sub`. Locally those rows are yours to make, with invented values:
an `Accounts` row for the realm user's `account_id` (`a91a7c0e-5747-4e00-8000-000000000019` for `synthetic-owner`)
and a `Users` row in it whose `ExternalSubject` is the `sub` that `GET /api/auth/me` returns once signed in (Keycloak
makes a new one each time the realm is imported). An invitation does this for real users: signed in as
`synthetic-owner`, `POST /api/invitations` with an address, a name and `helper` or `grower`; the invitation arrives
in Mailpit, its link verifies the address and sets the password, and the new user's row already holds its `sub`.

**Schema migrations** live in `src/ApiaryCopilot.Infrastructure/Migrations/`. The server applies them on start **in
Development only**; anywhere else it never changes the schema itself. The first migration also installs a trigger
that refuses any update, delete or truncate of `ColonyEvents`, the colony history: a restore that inserts rows is
unaffected, anything that rewrites that table fails. `AddDataProtectionKeys` adds the `DataProtectionKeys` table, the
sign-in key ring, each row encrypted with `DataProtection__Certificate`; it is written by hand, because the table is
outside the account-filtered model, so squashing the migrations must carry it over. A restore needs the same
certificate to read it; without it, users only sign in again.

## 2. Rules that must hold

| Must hold | Otherwise |
|---|---|
| Staging and production never share a database, bucket or chat bot | Test data reaches the family's live records |
| PostgreSQL is reachable only on the private network | The database is exposed to the internet |
| Migrations only add, and rollback is by image, never by a down migration (§9) | The previous image cannot read the schema, or a release destroys data |
| `app` has no public address: `proxy` is its only public route | Requests reach the app around the proxy, and `/metrics` is public |
| Secrets live only in Railway variables, and locally in the git-ignored `.env` | A credential is committed or shipped in an image |
| Production runs only an image that passed staging | Production runs an untested build |
| .NET memory stays capped (§3) | Memory, the main cost, grows past the cost target (NFR-7) |
| Every apiary request acts only for a signed-in caller's `account_id`, as the user whose `ExternalSubject` is its `sub`; nothing configures an account. `CurrentAccount__Id` and `CurrentUser__Id` are read by nothing and may be deleted wherever they are set | A request acts for an account it did not sign in to, or stamps rows with someone else's user |
| Staging's proxy runs with `PROXY_BASIC_AUTH=on` until sign-in is on there | Anyone who finds the staging address reaches the prototype |
| The bucket's credentials are only in `app`'s variables | Anyone holding them reads, writes or deletes every account's media |
| Where sign-in is on, `SignIn__RequireHttps` stays on and the authority is `https`; off only for a local http Keycloak and the tests | The session cookie loses `Secure` and its `__Host-` prefix, and the server trusts keys fetched over plain http |
| `DataProtection__Certificate`, `SignIn__ClientSecret` and `KeycloakAdmin__ClientSecret` are only in `app`'s variables; the `DataProtectionKeys` table is reached only on the private network, never logged or exported | With the certificate and a copy of the table, someone can forge or read the server's cookies; with a client secret, act as the server towards Keycloak; with the service account's secret, take over or delete any helper, grower or customer and invite addresses they control, in any account (the accepted risk of D-22) |
| In a real realm the service account has exactly the grant of §4 *Keycloak on Railway*, and every owner holds `owner` through the `owners` group, never by a direct role mapping | A wider grant reaches owners, roles or the realm; an owner outside the group can be disabled, edited, reset or deleted by the service account |
| While sessions are in memory, `app` runs one replica | A second replica does not know the first one's sessions, so a browser is signed out at random |

## 3. Configuration

Values come from the environment, never from source. The server stops at startup, naming the setting (never its
value), when one is malformed or out of its bounds — including a certificate that does not load or has no RSA private
key of at least 2048 bits, sign-in configured without the connection string, and a `KeycloakAdmin__` setting set
without sign-in.

**The server (`app`):**

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | Yes | `Production` | `Development` locally, `Staging` on staging |
| `ConnectionStrings__ApiaryCopilot` | In Development; wherever the apiary requests are to read and write; wherever `SignIn__Authority` is set | none | PostgreSQL connection string, e.g. `Host=…;Port=5432;Database=apiary_copilot;Username=…;Password=…`. **Secret** |
| `SignIn__Authority` | To turn sign-in on; then every `SignIn__` setting below without a default, `DataProtection__Certificate` and the connection string are required | unset: **sign-in off** — no `/api/auth` route exists, no caller has an account, and every yard, colony, inspection and media request answers `503` | The Keycloak realm's URL, which is also the issuer every token must name, e.g. `https://<Keycloak's Railway address>/realms/<realm>`; `http://127.0.0.1:8180/realms/apiary-keeper-dev` locally. Must be `https` unless `SignIn__RequireHttps` is off |
| `SignIn__MetadataAddress` | No | the authority's own discovery document | Where the server fetches Keycloak's discovery document when it reaches Keycloak by another address than the browser does, e.g. `http://keycloak.railway.internal:8080/realms/<realm>/.well-known/openid-configuration`. Keycloak then needs its front-end URL fixed (`KC_HOSTNAME`) and back-channel URLs per request (`KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`), so the issuer stays the browser's |
| `SignIn__ClientId` | With `SignIn__Authority` | none | The server's confidential client in the realm: `apiary-keeper-bff` locally |
| `SignIn__ClientSecret` | With `SignIn__Authority` | none | That client's secret. **Secret** |
| `SignIn__Audience` | With `SignIn__Authority` | none | The audience a bearer token must carry: `apiary-keeper-api` |
| `SignIn__RequireHttps` | No | `true` | **On everywhere deployed.** Off only for a local http Keycloak and the tests: it allows an `http` authority and drops the cookie's `Secure` flag and `__Host-` prefix |
| `SignIn__SessionIdleTimeout` | No | `00:30:00` | How long a browser session lasts without a request, 1 minute to 1 day |
| `SignIn__SessionMaxLifetime` | No | `12:00:00` | How long a browser session lasts at most from sign-in, from the idle timeout to 7 days. Sessions are also lost when the server restarts |
| `SignIn__ClockSkew` | No | `00:00:30` | How far a token's lifetime may be off the server's clock, 0 to 5 minutes |
| `DataProtection__Certificate` | With `SignIn__Authority` | none | An X.509 certificate with an RSA private key of at least 2048 bits, as a **base64 PKCS#12**, that encrypts the key ring in `DataProtectionKeys`. **Secret**. Replacing it makes every cookie and sign-in in progress invalid, so users sign in again |
| `DataProtection__CertificatePassword` | When the PKCS#12 has a password | none | Its password. **Secret** |
| `KeycloakAdmin__ClientId` | To turn invitations on; then sign-in and every `KeycloakAdmin__` setting below without a default are required | unset: **invitations off** — `POST /api/invitations` answers `503` | The server's service-account client in the realm: `apiary-keeper-api-admin` locally. The admin and token addresses come from the sign-in realm (`SignIn__MetadataAddress` when set, otherwise `SignIn__Authority`) |
| `KeycloakAdmin__ClientSecret` | With `KeycloakAdmin__ClientId` | none | That client's secret. **Secret**: rotate it with the other secrets and after any suspected exposure |
| `KeycloakAdmin__HelperRoleId`, `KeycloakAdmin__GrowerRoleId`, `KeycloakAdmin__CustomerRoleId` | With `KeycloakAdmin__ClientId` | none | The Keycloak ids (GUIDs) of the realm roles `helper`, `grower` and `customer`, which the service account may not read: in the admin console, *Realm roles* → the role, the id in its address. Locally `0d6f3a52-7a1e-4c1f-9b0e-000000000002`, `…003` and `…004` |
| `KeycloakAdmin__InvitationLifetime` | No | `3.00:00:00` | How long an invitation's emailed link stays valid, 1 hour to 30 days |
| `Media__ServiceUrl`, `Media__Bucket`, `Media__AccessKeyId`, `Media__SecretAccessKey` | All four, or none | none | The bucket's S3 endpoint (`https`), name and key pair; `Media__SecretAccessKey` is **secret**. None set: the media requests answer `503`. Some set: the server stops at startup |
| `Media__PublicUrl` | No | `Media__ServiceUrl` | The endpoint put in the URLs phones receive, when it differs (locally `http://127.0.0.1:9000`) |
| `Media__Region` | No | `auto` | The region URLs are signed for |
| `Media__ForcePathStyle` | No | `false` | `true` for path-style bucket URLs (MinIO, and a Railway bucket whose credentials say so) |
| `Media__AllowHttp` | No | `false` | `true` allows `http` endpoints: local MinIO only, never staging or production |
| `Media__UploadUrlLifetime`, `Media__DownloadUrlLifetime` | No | `00:10:00`, `00:15:00` | How long a presigned URL stays valid, 1 second to 1 hour. Keep uploads short: until it expires, an upload URL can be replayed |
| `Media__ImageContentTypes` | No | `image/jpeg image/png image/webp image/heic image/heif` | Allowed image types. SVG, XML, HTML and any `+suffix` type are refused at startup |
| `Media__VideoContentTypes` | No | `video/mp4 video/quicktime video/webm` | Allowed video types, with the same refusals |
| `Media__MaxImageBytes`, `Media__MaxVideoBytes` | No | 20 MiB, 200 MiB | The largest upload of each kind, 1 byte to 5 GiB; the length is signed into the URL |
| `Features__BeeRemovals` | No | `false` | Turns the bee-removal module on, once it exists |
| `DOTNET_gcServer`, `DOTNET_GCHeapHardLimit` | No — set in the image | `0`, `0x10000000` (256 MiB) | Keep memory, Railway's main cost, low. A heap that reaches the limit fails rather than growing the bill |

`CurrentAccount__Id` and `CurrentUser__Id`, which earlier versions read, are read by nothing; delete them wherever
they are set.

**The proxy (`proxy`):**

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `PROXY_APP_HOSTS` | Yes | none | Host names routed to the app, space-separated, lower case: `staging-apiary-keeper.marqspec.com` on staging, `apiary-keeper.marqspec.com` on production (interim names, D-17), `localhost 127.0.0.1` locally. Any other host gets `421` |
| `PROXY_APP_UPSTREAM` | Yes | none | The app's private address: `app.railway.internal:8080` on Railway, `api:8080` locally |
| `PROXY_MAX_BODY_SIZE` | No | `30m` | Largest request body passed on |
| `PROXY_BASIC_AUTH` | No | `off` | `on` asks for a shared password on every path but `/health`: on staging until sign-in is on there, off everywhere else. While it is on, a bearer token cannot pass the proxy (both use the `Authorization` header), so automated clients cannot call staging through it |
| `PROXY_BASIC_AUTH_USER`, `PROXY_BASIC_AUTH_PASSWORD` | When the gate is `on` | none | The gate's user name, and its password (16 to 128 characters, e.g. `openssl rand -base64 24`). **Secret**. The proxy refuses to start if they are set while the gate is off |

**Where sign-in is deployed, rate-limit `/api/auth/login` at the proxy.** Each anonymous call to it makes the server
push an authorization request to Keycloak, so anyone can make the server call Keycloak as often as they call it. Add
an nginx `limit_req` zone keyed by the client address on that route (not on the callback) when sign-in is turned on;
nothing limits it today.

**Local only** (in `.env`, names in [`.env.example`](.env.example)): `POSTGRES_PASSWORD`, `MINIO_ROOT_USER`,
`MINIO_ROOT_PASSWORD`, which [`compose.yaml`](compose.yaml) passes to the local PostgreSQL and MinIO.

## 4. Setting up staging

1. Create a Railway project, on Railway's Pro plan, with a `staging` environment. The two images are private, so
   Railway pulls them with registry credentials, which need the Pro plan; nothing is ever pushed public.
2. Create these services, with these names (the deploy script redeploys them by name, `app` first):
   - **`app`** — image `ghcr.io/<owner>/apiary-keeper:staging`, no public address. Variables: `PORT=8080`,
     `ASPNETCORE_ENVIRONMENT=Staging`, `ConnectionStrings__ApiaryCopilot` built from the Postgres service's variables
     (`Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}`).
     Health check path `/health`. Until sign-in is set up on staging (Keycloak on Railway, below), every yard, colony,
     inspection and media call answers `503` and the prototype shows no data.
   - **`proxy`** — image `ghcr.io/<owner>/apiary-keeper-proxy:staging`. Variables: `PORT=8080`,
     `PROXY_APP_HOSTS=staging-apiary-keeper.marqspec.com`, `PROXY_APP_UPSTREAM=app.railway.internal:8080`,
     `PROXY_MAX_BODY_SIZE=30m`, `PROXY_BASIC_AUTH=on`, `PROXY_BASIC_AUTH_USER` and a generated
     `PROXY_BASIC_AUTH_PASSWORD`, shared with the family separately. **No health check path**: Railway's check sends a
     host name the proxy answers with `421`.
   - **`Postgres`** — Railway's PostgreSQL template with its volume; no TCP proxy.
   - **`media`** — a storage bucket. On `app`, set `Media__ServiceUrl`, `Media__Bucket`, `Media__AccessKeyId`,
     `Media__SecretAccessKey` and `Media__Region` from the bucket's variables. Then give the bucket a CORS rule that
     allows only the staging address, `PUT` and `GET`, and the `content-type` header — without it the browser blocks
     every upload:

     ```bash
     aws s3api put-bucket-cors --endpoint-url https://t3.storageapi.dev --bucket "$BUCKET" --cors-configuration \
       '{"CORSRules":[{"AllowedOrigins":["https://staging-apiary-keeper.marqspec.com"],"AllowedMethods":["PUT","GET"],"AllowedHeaders":["content-type"]}]}'
     ```

   The services fail to pull an image until the first images are pushed; that is expected.
3. **Registry credentials** for `app` and `proxy` (each service's *Settings*, *Source*, *Registry Credentials*): for
   `ghcr.io` Railway asks only for the token, a **classic** GitHub personal access token with only `read:packages`
   (fine-grained tokens do not cover the container registry), created by an account that can read `<owner>`'s packages and, if the organisation enforces SAML single sign-on, authorised
   for it. The images live in `<owner>`'s namespace: if the owning account or organisation is renamed, the namespace
   moves, so update both services' image addresses and these credentials, or the next deploy pulls from a namespace
   that no longer exists. Renaming the repository changes neither.
4. Register an SSH key with Railway (`ssh-keygen -t ed25519 -N ''`, the public half in Railway's *SSH keys*). The
   migration step uses its private half to reach the private database (§9). Railway's SSH keys belong to an account
   or workspace, so the key reaches more than staging; the migration script refuses any environment but staging.
5. Create a Railway **project token** for the `staging` environment, for the deploy and migration scripts. A project
   token reaches one environment of one project; an account or workspace token reaches far more.
6. Once the registry credentials are set, build and push the first images (§5), migrate (§9) and deploy (§9). After
   the first push, check on `<owner>`'s *Packages* page that both packages are **private** and linked to the
   repository.
7. Check the prototype through the gate: `/health` answers the version, and the family app's shell loads. Until
   sign-in is on there, every apiary request answers `503` *no signed-in account: sign-in is not set up on this
   server*. With sign-in on, a signed-in `owner` or `helper` whose `Users` row holds their `sub` creates a yard and
   reads it back; an account with no row or an unknown `sub` is `403`, picked up without a redeploy once the row
   exists.
8. Attach `staging-apiary-keeper.marqspec.com` to the `proxy` service (target port 8080) and create the DNS record
   Railway shows. Production later takes `apiary-keeper.marqspec.com` the same way. Both are interim names: the
   go-live domain is `apiary-keeper.com`, with per-customer storefronts at `{business name}.apiary-keeper.com`. The
   proposal page is served at `apiary-keeper-intro.marqspec.com`.
9. Set a Railway custom email alert at $30 of compute usage (NFR-7's target; up to $40 is tolerated in a month with
   higher expenses). Pro includes $20 of usage, so past $20 usage and the bill are the same figure. A hard usage limit
   takes every workload offline when it is reached, so if one is set, set it no lower than $40.

**Keycloak on Railway: the server's service account and owners** (Phase 1, with Keycloak stood up there). In the
Keycloak admin console, mirroring the local realm file:

1. **Turn on admin permissions:** *Realm settings* → *General* → *Admin Permissions* on.
2. **The `owners` group:** *Groups* → create `owners`; its *Role mapping* → assign the realm role `owner`. Nothing else
   gives `owner`.
3. **The service account:** *Clients* → create `apiary-keeper-api-admin`, *Client authentication* on, and of the
   authentication flows *Service accounts roles* only. Its *Credentials* secret goes into `KeycloakAdmin__ClientSecret`
   on `app` (§3), and nowhere else.
4. **Its grant**, under *Permissions*: a client policy for `apiary-keeper-api-admin` and a negative one for it; then
   the permissions — *Users*, all users, `manage` and `map-roles`, the positive policy; *Roles*, the roles `helper`,
   `grower` and `customer`, `map-role`, the positive policy; *Groups*, the group `owners`, `view-members`,
   `manage-members` and `manage-membership`, the negative policy. Nothing else: no realm-management role, no other
   permission. Copy the three roles' ids into `KeycloakAdmin__*RoleId` (§3).
5. **Check it** as the integration tests do against the local realm: the account cannot read a user, map `owner`, or
   disable, edit, reset or delete a member of `owners`, or take one out of the group; it can add and remove a member
   role on one, as accepted (D-22).

Also in a real realm: turn off the password (direct-access) grant on Keycloak's built-in `admin-cli` client, or anyone
can trade a user's password for realm-signed tokens through it; set the password-reset link to 30 minutes; use MFA for
the administrator logins; and keep Keycloak's address stable, because it is the token issuer.

**Creating an owner** (the Keycloak administrator, never the server): *Users* → *Add user* with the address as user
name and email, first and last name, and the attribute `account_id` set to the account's id (a lower-case GUID);
**Join groups → `owners`** — never *Role mapping* → `owner`, which the service account's deny does not see; then
*Credentials* → *Send email* with *Update password* (or *Verify email*). The account's `Accounts` row and the owner's
`Users` row, whose `ExternalSubject` is the user's *ID* as the console shows it, are written in the app's database;
production's bootstrap step for this is not written yet. Every helper and grower after that is the owner's
invitation.

The commands [`scripts/deploy.sh`](scripts/deploy.sh) runs:

```bash
railway redeploy --service app --environment staging --from-source --yes        # pull :staging again: a new deployment
railway deployment list --service app --environment staging --limit 1 --json    # the newest deployment's id and status
```

`--from-source` makes Railway pull the image's tag again rather than redeploy the image it already has. Confirm on the
first staging run that the moved `:staging` digest is picked up, that Railway's edge keeps the original `Host`, and
that `railway ssh` runs the migration non-interactively with the project token and `--identity-file`; each failure
stops the step with a message naming it.

## 5. Images

Images are tagged by version only: `:X.Y.Z` (from `<Version>` in [`Directory.Build.props`](Directory.Build.props)),
the moving `:staging` and `:production`, and `:staging-previous` / `:production-previous` for rollback. Build each
version once and promote the same image; never rebuild a released version. Raise `<Version>` (and the matching
`version` in `src/pwa/package.json`) before building a new release.

```bash
V=0.1.0   # the <Version> in Directory.Build.props
# <owner>: the GitHub account or organisation that owns the repository, in lower case (a registry path must be)
APP=ghcr.io/<owner>/apiary-keeper
PROXY=ghcr.io/<owner>/apiary-keeper-proxy
docker build --target test .                    # the unit tests, inside the build
docker build --target test proxy                # the proxy's configuration tests
# The source label links each package to its repository on GHCR (the Dockerfiles carry no URL);
# REVISION records the commit the image was built from.
SRC=https://github.com/<owner>/<repository>
REV=$(git rev-parse HEAD)
docker build --target runtime --build-arg VERSION=$V --build-arg REVISION=$REV \
  --label org.opencontainers.image.source=$SRC -t $APP:$V .
docker build --target runtime --build-arg VERSION=$V --build-arg REVISION=$REV \
  --label org.opencontainers.image.source=$SRC -t $PROXY:$V proxy
bash scripts/proxy-smoke.sh $APP:$V $PROXY:$V
```

`--target runtime` matters for the proxy, whose last stage is its test stage. [`scripts/proxy-smoke.sh`](scripts/proxy-smoke.sh)
runs the two images together on a private Docker network and checks routing, headers, the size limit, the gate (and
that a bearer token cannot pass it) and that `/metrics` stays private. Then run the integration tests against the
image itself rather than a source build — the fixture starts its own PostgreSQL, MinIO, Keycloak and Mailpit, fills in
every `-e` variable and replaces `{port}` ([`CONVENTIONS.md`](CONVENTIONS.md) §8.2):

```bash
APP_SERVER_COMMAND="docker run --rm --name apiary-it-{port} --network apiary-it-{port} -p 127.0.0.1:{port}:8080 -e DOTNET_ENVIRONMENT=Staging -e ConnectionStrings__ApiaryCopilot -e Media__ServiceUrl -e Media__PublicUrl -e Media__Bucket -e Media__AccessKeyId -e Media__SecretAccessKey -e Media__ForcePathStyle -e Media__AllowHttp -e Media__UploadUrlLifetime -e Media__DownloadUrlLifetime -e SignIn__Authority -e SignIn__MetadataAddress -e SignIn__ClientId -e SignIn__ClientSecret -e SignIn__Audience -e SignIn__RequireHttps -e DataProtection__Certificate -e DataProtection__CertificatePassword -e KeycloakAdmin__ClientId -e KeycloakAdmin__ClientSecret -e KeycloakAdmin__HelperRoleId -e KeycloakAdmin__GrowerRoleId -e KeycloakAdmin__CustomerRoleId $APP:$V" \
  dotnet test src/tests/ApiaryCopilot.IntegrationTests -c Release
```

When all of that passes, push both version tags (`docker login ghcr.io` first, with a token that may write packages):

```bash
docker push $APP:$V
docker push $PROXY:$V
```

## 6. Rolling forward and back

Forward is a new version, migrated and deployed (§9). Back is **by image**: point the environment's tag at the
version that ran before — `:staging-previous`, which step 4 of §9 kept, or any known-good `:X.Y.Z` — redeploy, and
smoke-check. The schema stays where it is: the migration only added, so the previous image still works on it.

```bash
PREV=1.3.0   # the version :staging-previous holds
for img in ghcr.io/<owner>/apiary-keeper ghcr.io/<owner>/apiary-keeper-proxy; do
  docker buildx imagetools create --tag "$img:staging" "$img:staging-previous"
done
RAILWAY_TOKEN=<staging project token> bash scripts/deploy.sh staging $PREV
STAGING_URL=https://staging-apiary-keeper.marqspec.com bash scripts/smoke-check.sh staging
```

On the first release to an environment there is no `:staging-previous`, and nothing to roll back to: fix forward
with a new version.

## 7. Known issues

| Symptom | Cause | Fix |
|---|---|---|
| Every apiary request answers `503` *no signed-in account: sign-in is not set up on this server* | `SignIn__Authority` is unset, so no caller has an account | Turn sign-in on (§3), or expect it until Keycloak runs there |
| A request answers `401` | Not signed in, or the session ended (idle, maximum lifetime, a refused renewal, a server restart) | Sign in again at `/api/auth/login` |
| A request answers `403` *your account is not set up on this server* | The token's `account_id` has no `Accounts` row | Add the row; it is picked up without a restart |
| A request answers `403` *you are not a user of this account* | No `Users` row of that account holds the token's `sub` in `ExternalSubject` | Invite the user, or set the row's `ExternalSubject`; it is picked up without a restart |
| `POST /api/invitations` answers `503` *invitations are not set up on this server* | `KeycloakAdmin__ClientId` is unset | Set the `KeycloakAdmin__` settings (§3) |
| A write answers `403` with the session cookie | It lacks the header `X-CSRF: 1` | Send the header on every write |
| Every media request answers `503` *no media bucket is set up* | The `Media__` settings are unset | Set them on `app` (§3) |
| The proxy answers `421` | The host name is not in `PROXY_APP_HOSTS` | Add it |
| The proxy answers `502` just after start | The app is not listening yet | Wait a few seconds |
| The `proxy` deployment fails its Railway health check | Railway's check uses a host name the proxy refuses | Leave the proxy's health check path empty (§4) |
| A Railway deployment fails with an image-pull or `unauthorized` error | The packages are private and the service has no registry credentials, the token is not a classic one with `read:packages`, it cannot read `<owner>`'s packages, or it has expired | Set the registry credentials on `app` and `proxy` (§4); the packages stay private |
| An automated client gets `401` from staging through the proxy | The shared-password gate takes the `Authorization` header, so a bearer token cannot pass it | Expected while the gate is on |
| Uploads fail with a CORS error | The bucket's CORS rule is missing, or lacks `content-type` | Set it as in §4. Locally, open the app at `http://localhost:5080` |
| An upload answers `403 SignatureDoesNotMatch` | The upload's type or length differs from what was signed | Send exactly the headers and size the upload request returned |
| An upload or download answers `403 Request has expired` | The URL outlived its lifetime | Ask for a new URL |
| Staging asks for a user name and password | The gate (§3) | Use the gate's credentials |
| A change to `keycloak/import/` does not show in the local Keycloak | Keycloak skips importing a realm that already exists | `docker compose up -d --force-recreate keycloak` |
| iPhone users lose queued photos | Safari clears an uninstalled site's data after 7 days | Install the app to the home screen |
| The server logs `Cannot load library libgssapi_krb5.so.2` | PostgreSQL's driver probes for Kerberos | Nothing: password sign-in does not use it |
| A fresh database's first start logs a failed command on `__EFMigrationsHistory` | EF Core reads its history table before creating it | Nothing, when `Applying migration` follows |
| `dotnet run` fails: *No database connection string* | Development migrates on start | Start PostgreSQL and set `ConnectionStrings__ApiaryCopilot` |

## 8. Production

Not set up. It differs from staging in: the custom domain `apiary-keeper.marqspec.com` (interim) with Railway-managed
TLS, real integration credentials, **no seeded data**, sign-in on through its own Keycloak and no shared-password gate,
and a migration path still to be decided. Its first owner is created in Keycloak by the administrator (§4 *Creating an
owner*). Backups are planned as Railway volume backups plus a nightly `pg_dump` to the bucket; the restore procedure
must be written and rehearsed before production holds real data.

## 9. Deploying and migrating staging

**Migrate first, then deploy.** The new schema only ever adds, and the previous image keeps working on it, so the
running version keeps serving while the migration runs, and a failed migration stops the release with nothing
deployed.

1. **Generate the migration script** — the idempotent SQL of every committed migration, then the staging seed
   (`scripts/sql/staging-seed.sql`: one invented account and its user, inserted once and never rewritten), then a
   closing line that proves the script ran to its end. It refuses the script unless every statement is an additive
   shape:

   ```bash
   dotnet tool restore
   bash scripts/migration-script.sh $V staging-migration-$V.sql
   ```

   `scripts/check-migration-sql.sh` is the check: it allows creating tables and indexes, adding a nullable column or
   one with a default, and adding keys; it refuses drops, renames, type changes, `SET NOT NULL`, updates, deletes and
   dynamic SQL. A reviewed line in `scripts/sql/migration-overrides.txt`
   (`<MigrationId> <reason>`) exempts one migration — never one that touches `ColonyEvents` or `Inspections`, whose
   history is corrected by a new event, not rewritten. The check guards against accidents, not against a malicious
   author.
2. **Check that the running version survives it:** `bash scripts/rollback-compat.sh $APP:staging
   staging-migration-$V.sql` applies the script twice to a throwaway PostgreSQL and drives the image staging runs now
   against it, creating and reading a yard, a colony and an inspection. It needs Docker, `jq` and `openssl`: it starts
   a throwaway Keycloak with the local realm, signs in as its invented owner for a bearer token, sets the seeded user's
   `ExternalSubject` to the token's `sub`, and gives the image the sign-in settings with a certificate made for the
   run. An image that predates sign-in is detected and driven the old way. Skip this step on the first release, when
   there is no `:staging` yet.
3. **Apply it to staging** — inside the Postgres service's own container, over `railway ssh`, so the database never
   leaves the private network. The script refuses any environment but staging:

   ```bash
   RAILWAY_TOKEN=<staging project token> RAILWAY_SSH_KEY_FILE=<private key> \
     bash scripts/migrate.sh staging staging-migration-$V.sql $V
   ```

4. **Deploy:** keep what runs now as `:staging-previous` (the rollback target; skip on the first release), point
   `:staging` at the new version for both images, redeploy and smoke-check:

   ```bash
   for img in $APP $PROXY; do
     docker buildx imagetools create --tag "$img:staging-previous" "$img:staging"
     docker buildx imagetools create --tag "$img:staging" "$img:$V"
   done
   RAILWAY_TOKEN=<staging project token> bash scripts/deploy.sh staging $V
   STAGING_URL=https://staging-apiary-keeper.marqspec.com bash scripts/smoke-check.sh staging
   curl -s https://staging-apiary-keeper.marqspec.com/health   # {"version":"<V>"}; the gate lets /health through
   ```

   [`scripts/deploy.sh`](scripts/deploy.sh) redeploys `app`, then `proxy`, and waits for each to succeed (default
   600 seconds). [`scripts/smoke-check.sh`](scripts/smoke-check.sh) needs `/health` to answer 2xx within about a
   minute; `/health` reports the running version — compare it with the one you deployed. If either fails, roll back
   (§6) to `:staging-previous`.

**Fixing data** is done forward: a further additive, idempotent change in the next version, never editing rows by
hand and never rolling the schema back.

**New kinds of stored value** (a new colony event type, a new inspection answer) need care that the rollback test
cannot give: either the previous image ignores them, or they ship in two versions — the first teaches the image to
read them, the second starts writing them.
