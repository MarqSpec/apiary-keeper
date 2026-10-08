#!/usr/bin/env bash
# Tests for scripts/migrate.sh and the script it runs, against a throwaway PostgreSQL (postgres:17 in Docker; needs
# a Docker daemon). `railway` is a stub on PATH that records its arguments and plays `railway ssh` as OpenSSH does: it
# JOINS the remote arguments with spaces and hands the result to the container's shell (`docker exec ... sh -c "$*"`),
# stdin attached, with the container environment each case sets. STUB_PRELUDE runs before that command, as a session
# that prints its environment would. Every run is checked for the database password in what migrate.sh printed.
# The migration script is a fixture shaped like scripts/migration-script.sh output, or MIGRATION_SQL when set (CI and
# a local run pass the real generated script).
# Run: bash scripts/tests/migrate-test.sh
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
bash -n "$repo/scripts/migrate.sh" || { echo "FAIL migrate.sh does not parse"; exit 1; }
docker info >/dev/null 2>&1 || { echo "FAIL migrate-test.sh needs a Docker daemon"; exit 1; }

expected_cases=18
cases=0
failed=0
work="$(mktemp -d)"
pg="apiary-migrate-test-$$"
cleanup() { docker rm -f "$pg" >/dev/null 2>&1; rm -rf "$work"; }
trap cleanup EXIT
version=9.9.9

mkdir -p "$work/bin"
cat >"$work/bin/railway" <<'EOF'
#!/usr/bin/env bash
# railway ssh --service S --environment E --identity-file K -- <remote command...>
printf '%s\n' "$*" >>"$WORK/calls"
[[ "$1" == ssh ]] || exit 64
shift
while [[ "$1" != -- ]]; do shift; done
shift
envs=(-e "RAILWAY_ENVIRONMENT_NAME=${STUB_ENV_NAME}")
[[ -n "${STUB_DB_URL:-}" ]] && envs+=(-e "DATABASE_URL=${STUB_DB_URL}")
# OpenSSH sends one string, the arguments joined by spaces, and the remote shell parses it again.
remote="${STUB_PRELUDE:+$STUB_PRELUDE; }$*"
if [[ -n "${STUB_DROP_STDIN:-}" ]]; then
  docker exec -i "${envs[@]}" "$PG" sh -c "$remote" </dev/null
else
  docker exec -i "${envs[@]}" "$PG" sh -c "$remote"
fi
EOF
chmod +x "$work/bin/railway"
printf 'synthetic-key\n' >"$work/key"

sql="${MIGRATION_SQL:-}"
if [[ -z "$sql" ]]; then
  sql="$work/release.sql"
  { cat <<'EOF'
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
START TRANSACTION;
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004140357_InitialCreate') THEN
    CREATE TABLE "Accounts" ("AccountId" uuid NOT NULL, "Name" character varying(200) NOT NULL, CONSTRAINT "PK_Accounts" PRIMARY KEY ("AccountId"));
    CREATE TABLE "Users" ("UserId" uuid NOT NULL, "AccountId" uuid NOT NULL, "Name" character varying(200) NOT NULL, CONSTRAINT "PK_Users" PRIMARY KEY ("UserId"), CONSTRAINT "FK_Users_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("AccountId") ON DELETE RESTRICT);
    END IF;
END $EF$;
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004140357_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20261004140357_InitialCreate', '10.0.12');
    END IF;
END $EF$;
COMMIT;
EOF
    cat "$repo/scripts/sql/staging-seed.sql"
    printf "SELECT concat('apiary-copilot-migration-', 'applied ', '%s') AS migration;\n" "$version"
  } >"$work/release.sql"
else
  version="$(sed -n "s/^SELECT concat('apiary-copilot-migration-', 'applied ', '\([^']*\)').*/\1/p" "$sql")"
  [[ -n "$version" ]] || { echo "FAIL MIGRATION_SQL has no closing marker"; exit 1; }
fi

docker run -d --rm --name "$pg" -e POSTGRES_PASSWORD=synthetic-test-only -e POSTGRES_DB=railway postgres:17 >/dev/null || { echo "FAIL could not start postgres:17"; exit 1; }
for _ in $(seq 1 60); do docker exec "$pg" pg_isready -U postgres -d railway -h 127.0.0.1 >/dev/null 2>&1 && break; sleep 1; done
docker exec "$pg" psql -U postgres -d railway -c 'CREATE DATABASE other' >/dev/null
password=synthetic-test-only
url="postgresql://postgres:${password}@127.0.0.1:5432"
q() { docker exec "$pg" psql -U postgres -d "$1" --tuples-only --no-align -c "$2" 2>/dev/null; }

leaks=0
migrate() {
  : >"$work/calls"
  local status=0
  (cd "$repo" && PATH="$work/bin:$PATH" WORK="$work" PG="$pg" RAILWAY_TOKEN="${TOKEN-synthetic-token}" \
    RAILWAY_SSH_KEY_FILE="${KEYFILE-$work/key}" STUB_ENV_NAME="${ENV_NAME-staging}" STUB_DB_URL="${DB_URL-$url/railway}" \
    GITHUB_ACTIONS="${ACTIONS-}" bash scripts/migrate.sh "$@") >"$work/out" 2>&1 || status=$?
  # The password may appear only in a ::add-mask:: line, which the runner consumes and never shows.
  if grep -v '^::add-mask::' "$work/out" | grep -qF "$password"; then leaks=$((leaks + 1)); echo "LEAK in: migrate $*"; fi
  return "$status"
}
check() {
  cases=$((cases + 1))
  if [[ "$2" == "$3" ]]; then echo "ok   $1"; else failed=$((failed + 1)); echo "FAIL $1: got '$2', expected '$3'"; cat "$work/out"; fi
}
said() { grep -cF -- "$1" "$work/out"; }
called() { [[ -s "$work/calls" ]] && echo called || echo not-called; }

# 1. Refusals before anything runs: another environment, no token, no key.
migrate production "$sql" "$version"; status=$?
check "another environment is refused, and railway is never called" "$status $(said "refusing to run against 'production'") $(called)" "2 1 not-called"
TOKEN='' migrate staging "$sql" "$version"; status=$?
check "no RAILWAY_TOKEN is refused, and railway is never called" "$status $(said "RAILWAY_TOKEN is not set") $(called)" "2 1 not-called"
KEYFILE='' migrate staging "$sql" "$version"; status=$?
check "no SSH key is refused, and railway is never called" "$status $(said "RAILWAY_STAGING_SSH_KEY") $(called)" "2 1 not-called"

# 2. The container's own guard: Railway says it is not staging, so nothing reaches the database.
ENV_NAME=production DB_URL="$url/other" migrate staging "$sql" "$version"; status=$?
check "a container outside staging refuses, and the database is untouched" \
  "$((status != 0)) $(said 'is not in staging') $(q other "SELECT count(*) FROM information_schema.tables WHERE table_name = 'Accounts'")" "1 1 0"

# 3. The real run: the schema and the synthetic account and user, through the expected railway command.
migrate staging "$sql" "$version"; status=$?
check "the script migrates staging and seeds the synthetic account and its user" \
  "$status $(q railway "SELECT count(*) FROM \"Accounts\" WHERE \"AccountId\" = 'a91a7c0e-5747-4e00-8000-000000000019'") $(q railway "SELECT count(*) FROM \"Users\" WHERE \"UserId\" = 'a91a7c0e-5747-4e00-8000-000000000042' AND \"AccountId\" = 'a91a7c0e-5747-4e00-8000-000000000019'")" "0 1 1"
check "it runs psql in the database service, in staging, with the key" \
  "$(grep -cx -- "ssh --service Postgres --environment staging --identity-file $work/key -- sh -s" "$work/calls")" "1"
migrations="$(q railway 'SELECT count(*) FROM "__EFMigrationsHistory"')"

# 4. Idempotence: the same script again changes nothing and still passes.
migrate staging "$sql" "$version"; status=$?
check "running it twice passes and changes nothing" \
  "$status $(q railway 'SELECT count(*) FROM "Accounts"') $(q railway 'SELECT count(*) FROM "Users"') $(q railway 'SELECT count(*) FROM "__EFMigrationsHistory"')" "0 1 1 $migrations"

# 5. A psql that read nothing exits 0; the missing closing line makes that a failure.
STUB_DROP_STDIN=1 migrate staging "$sql" "$version"; status=$?
check "a run where the script never arrived fails" "$status $(said 'never printed')" "1 1"

# 6. A failing statement stops the script and fails the step; nothing after it runs.
{ printf 'CREATE TABLE "Synthetic" ("Id" integer);\nSELECT 1/0;\n'; printf "SELECT concat('apiary-copilot-migration-', 'applied ', '%s') AS migration;\n" "$version"; } >"$work/bad.sql"
DB_URL="$url/other" migrate staging "$work/bad.sql" "$version"; status=$?
check "a failing statement fails the step before the closing line" "$status $(said 'the release has failed') $(said "apiary-copilot-migration-applied $version")" "1 1 0"

# 7. No connection string in the container.
DB_URL='' migrate staging "$sql" "$version"; status=$?
check "no DATABASE_URL in the container fails" "$((status != 0)) $(said 'DATABASE_URL is not set')" "1 1"

# 8. The marker must be the whole line: a script for another version that only starts the same is not this run's.
sed "s/'applied ', '${version}')/'applied ', '${version}0')/" "$sql" >"$work/other.sql"
migrate staging "$work/other.sql" "$version"; status=$?
check "another version's marker does not pass" "$status $(said 'never printed')" "1 1"
: >"$work/empty.sql"; migrate staging "$work/empty.sql" "$version"; status=$?
check "an empty script is refused, and railway is never called" "$status $(called)" "2 not-called"

# 9. A session that prints its environment before the command - what a bare `set` does when a script is passed as an
#    argument and re-parsed - leaks nothing: the remote output is filtered.
STUB_PRELUDE='env' migrate staging "$sql" "$version"; status=$?
check "a session that prints its environment still migrates, and its output is withheld" "$status $(said 'other line(s) of remote output withheld')" "0 1"
STUB_PRELUDE='env' ENV_NAME=production DB_URL="$url/other" migrate staging "$sql" "$version"; status=$?
check "the refusal path, after a session printed its environment, prints no credential" "$((status != 0)) $(said 'is not in staging')" "1 1"
STUB_PRELUDE='env' ACTIONS=true migrate staging "$sql" "$version"; status=$?
check "under GitHub Actions, a credential in the remote output is masked" "$status $(grep -cxF "::add-mask::${password}" "$work/out")" "0 1"
STUB_PRELUDE="echo 'psql: error: connection to postgresql://postgres:${password}@db failed'" migrate staging "$sql" "$version"; status=$?
check "an allowed diagnostic line that carries a connection URL is withheld" "$status $(said 'may carry a credential was withheld')" "0 1"
# A malformed DATABASE_URL: psql itself quotes the part it could not parse - here the password - on a psql: line.
DB_URL="postgresql://postgres:${password}%ZZ@127.0.0.1:5432/railway" ACTIONS=true migrate staging "$sql" "$version"; status=$?
check "psql's 'invalid ... token' line about a malformed URL is withheld and its token masked" \
  "$((status != 0)) $(said 'may carry a credential was withheld') $(grep -cxF "::add-mask::${password}%ZZ" "$work/out")" "1 1 1"
check "no run printed the database password" "$leaks" "0"

if [[ $cases -ne $expected_cases ]]; then echo "FAIL ran $cases cases, expected $expected_cases"; exit 1; fi
if [[ $failed -ne 0 ]]; then echo "$failed of $cases migrate.sh tests failed"; exit 1; fi
echo "all $cases migrate.sh tests passed"
