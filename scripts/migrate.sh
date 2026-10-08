#!/usr/bin/env bash
# Runs a release's migration script (scripts/migration-script.sh) against the staging database, before the
# new image is deployed (DEPLOYMENT.md §9). Staging only: any other environment is refused before anything runs.
#
# The database is on Railway's private network, so the script cannot connect from the runner. `railway ssh` runs psql
# inside the database service's own container instead; `railway run` would not do, because it only copies the
# variables to the runner, where a *.railway.internal host does not resolve. psql connects with the service's own
# DATABASE_URL - the connection never leaves Railway and is never a GitHub secret.
#
# Nothing but `sh -s` crosses as the remote command: OpenSSH joins the remote arguments with spaces and the remote
# shell parses them again, so a script passed as an argument could run as something else (a bare `set` prints every
# variable, credentials included). Everything travels on standard input instead: first the guard - Railway's own
# RAILWAY_ENVIRONMENT_NAME must be "staging" - then `exec psql` with the SQL as a quoted here-document. The remote
# output is never printed raw: only psql's diagnostics, this script's own lines and the closing line are shown, and a
# line that looks like it carries a credential is withheld.
#
# Environment:
#   RAILWAY_TOKEN             the staging environment's project token (GitHub secret RAILWAY_STAGING_TOKEN)
#   RAILWAY_SSH_KEY_FILE      path to the private key registered with Railway for SSH (from RAILWAY_STAGING_SSH_KEY)
#   RAILWAY_DATABASE_SERVICE  the Postgres service's name in Railway (default: Postgres)
#
# Usage: scripts/migrate.sh <environment> <script.sql> <version>
set -euo pipefail
env="${1:?usage: migrate.sh <environment> <script.sql> <version>}"
sql="${2:?usage: migrate.sh <environment> <script.sql> <version>}"
version="${3:?usage: migrate.sh <environment> <script.sql> <version>}"

if [[ "$env" != staging ]]; then
  echo "::error::migrate: refusing to run against '${env}': the migration script runs against staging only (DEPLOYMENT.md section 9)." >&2
  exit 2
fi
[[ -s "$sql" ]] || { echo "::error::migrate: '$sql' is missing or empty." >&2; exit 2; }
[[ -n "${RAILWAY_TOKEN:-}" ]] || { echo "::error::migrate: RAILWAY_TOKEN is not set - map it from the staging environment's secret RAILWAY_STAGING_TOKEN." >&2; exit 2; }
key="${RAILWAY_SSH_KEY_FILE:-}"
[[ -n "$key" && -s "$key" ]] || { echo "::error::migrate: RAILWAY_SSH_KEY_FILE names no key - add the staging environment's secret RAILWAY_STAGING_SSH_KEY (DEPLOYMENT.md section 4)." >&2; exit 2; }
service="${RAILWAY_DATABASE_SERVICE:-Postgres}"
marker="apiary-copilot-migration-applied ${version}"
delimiter="APIARY_MIGRATION_SQL_END_OF_SCRIPT"
if grep -qxF -- "$delimiter" "$sql"; then
  echo "::error::migrate: '$sql' contains the line ${delimiter}, which would end its here-document early." >&2
  exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# The guard comes first and prints nothing but its own refusal; the variables are the container's, not the runner's.
# shellcheck disable=SC2016
{
  printf '%s\n' \
    'if [ "${RAILWAY_ENVIRONMENT_NAME:-}" != staging ]; then echo "migrate: refusing: this container is not in staging" >&2; exit 3; fi' \
    'if [ -z "${DATABASE_URL:-}" ]; then echo "migrate: DATABASE_URL is not set in the database container" >&2; exit 4; fi' \
    "exec psql \"\$DATABASE_URL\" --no-psqlrc --quiet --tuples-only --no-align -v ON_ERROR_STOP=1 <<'${delimiter}'"
  tr -d '\r' <"$sql"
  printf '\n%s\n' "$delimiter"
} >"$work/stdin"

# Prints the remote output through an allowlist, withholding anything else, and masks any credential it can see.
show() {
  local line withheld=0 secret
  if [[ "${GITHUB_ACTIONS:-}" == true ]]; then
    while IFS= read -r secret; do
      [[ -n "$secret" ]] && echo "::add-mask::${secret}"
    done < <({ grep -oE 'postgres(ql)?://[^[:space:]"'"'"']+' "$1" || true
               grep -oE 'postgres(ql)?://[^:/@[:space:]]+:[^@[:space:]]+@' "$1" | sed -E 's|^[^:]+://[^:]+:||; s|@$||' || true
               grep -oiE 'password[a-z_]*=[^[:space:]"'"'"']+' "$1" | sed -E 's/^[^=]+=//' || true
               # libpq quotes the part of a malformed URL it could not parse, which can be the password itself.
               grep -oiE 'invalid [^"]*: "[^"]+"' "$1" | sed -E 's/^[^"]*"//; s/"$//' || true; } | sort -u)
  fi
  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == "$marker" || "$line" =~ ^(migrate:|psql:|ERROR:|WARNING:|NOTICE:|DETAIL:|HINT:|CONTEXT:|QUERY:|LINE\ [0-9]+:|[[:space:]]*\^[[:space:]]*$) ]]; then
      if [[ "$line" =~ :// || "$line" =~ [Pp][Aa][Ss][Ss][Ww][Oo][Rr][Dd] || "$line" =~ [Ii]nvalid.*\" ]]; then
        echo "migrate: [a line that may carry a credential was withheld]"
      else
        printf '%s\n' "$line"
      fi
    else
      withheld=$((withheld + 1))
    fi
  done <"$1"
  if (( withheld )); then echo "migrate: ${withheld} other line(s) of remote output withheld"; fi
}

echo "migrate: running $(basename "$sql") (${version}) in staging's ${service} service"
status=0
railway ssh --service "$service" --environment staging --identity-file "$key" -- sh -s <"$work/stdin" >"$work/out" 2>&1 || status=$?
show "$work/out"
if (( status != 0 )); then
  echo "::error::migrate: the migration script failed in staging (exit ${status}); the release has failed. The schema is as far as the last committed migration got - fix forward (DEPLOYMENT.md section 9)." >&2
  exit 1
fi
# Exit 0 with no closing line means psql ran nothing - the script never arrived - so it is a failure, not a pass.
if [[ "$(grep -cxF -- "$marker" "$work/out" || true)" -lt 1 ]]; then
  echo "::error::migrate: psql exited 0 but never printed '${marker}': the script did not run in staging." >&2
  exit 1
fi
echo "migrate: staging's database is migrated to ${version}"
