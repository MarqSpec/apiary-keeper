#!/usr/bin/env bash
# The rollback compatibility test (DEPLOYMENT.md §9): the image that ran before a release must still work against the
# schema that release's migration script leaves behind - it keeps serving while the release migrates (migrate first),
# and runs again after a rollback, which is by image, never by a down migration.
# Against a throwaway PostgreSQL (postgres:17 in Docker) it applies the script twice (it must be idempotent), starts
# <image> as staging runs it - Staging environment, no startup migration - and drives the prototype's writes and reads
# through it: a yard, a colony and an inspection, created and read back. Synthetic data only.
#
# Signing in. Since a separate change every apiary endpoint needs a signed-in owner or helper, so the script signs in the
# way an agent does: a bearer token for the dev realm's synthetic owner (keycloak/import/, the realm the integration
# tier uses; its passwords and secrets are synthetic test values for that realm alone), from a throwaway Keycloak
# started on the run's network from the image compose.yaml pins, imported with that file. The image gets the SignIn__
# and DataProtection__ settings (a certificate made for this run) and the seeded user's ExternalSubject is set to the
# token's sub, as an invitation will. Nothing here reaches a deployed system: the token is minted by the
# throwaway Keycloak and is never printed.
# Against a release that PREDATES sign-in the same run works: it has no sign-in, so it answers an anonymous request
# (the probe below) and ignores the bearer header, and it acts for CurrentAccount__Id and CurrentUser__Id - the only
# live use of those two settings left, passed so such an image can read them (a release with sign-in reads neither).
# The probe decides the mode, and the mode is printed: 401 is a release with sign-in, which must then accept the token;
# 200 is a release that predates it; any other answer fails the run. Once no image this runs against predates sign-in,
# delete those two settings and the predates mode.
# An image built before the account-rooted schema (the organization schema, replaced by one account-rooted initial
# migration before any staging database existed) fails here by design. The first release has no earlier image, so it
# skips this test.
#
# Usage: scripts/rollback-compat.sh <previous-image> <migration-script.sql>
set -euo pipefail
image="${1:?usage: rollback-compat.sh <previous-image> <migration-script.sql>}"
sql="${2:?usage: rollback-compat.sh <previous-image> <migration-script.sql>}"
[[ -s "$sql" ]] || { echo "::error::rollback-compat: '$sql' is missing or empty." >&2; exit 2; }
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/.." && pwd)"
for tool in jq openssl base64; do
  command -v "$tool" >/dev/null 2>&1 || { echo "::error::rollback-compat: needs $tool on PATH." >&2; exit 2; }
done
account="$(sed -n "/^INSERT INTO \"Accounts\"/,/;/ s/^VALUES ('\([0-9a-f-]\{36\}\)'.*/\1/p" "$here/sql/staging-seed.sql")"
[[ "$account" =~ ^[0-9a-f-]{36}$ ]] || { echo "::error::rollback-compat: no account id in scripts/sql/staging-seed.sql" >&2; exit 2; }
user="$(sed -n "/^INSERT INTO \"Users\"/,/;/ s/^VALUES ('\([0-9a-f-]\{36\}\)'.*/\1/p" "$here/sql/staging-seed.sql")"
[[ "$user" =~ ^[0-9a-f-]{36}$ ]] || { echo "::error::rollback-compat: no user id in scripts/sql/staging-seed.sql" >&2; exit 2; }

# What the sign-in needs, all of it read from the committed files: the realm's synthetic owner and clients, and the
# Keycloak image compose.yaml runs (pinned by digest, DEPLOYMENT.md §1).
realm="$repo/keycloak/import/apiary-keeper-dev-realm.json"
[[ -s "$realm" ]] || { echo "::error::rollback-compat: no realm at keycloak/import/apiary-keeper-dev-realm.json" >&2; exit 2; }
rj() { jq -r "$1" "$realm" | tr -d '\r'; }
realm_name="$(rj '.realm')"
owner_name=synthetic-owner
owner_password="$(rj ".users[] | select(.username == \"$owner_name\") | .credentials[0].value")"
owner_account="$(rj ".users[] | select(.username == \"$owner_name\") | .attributes.account_id[0]")"
test_client=apiary-keeper-integration-tests
test_secret="$(rj ".clients[] | select(.clientId == \"$test_client\") | .secret")"
audience="$(rj ".clients[] | select(.clientId == \"$test_client\") | .protocolMappers[] | select(.protocolMapper == \"oidc-audience-mapper\") | .config[\"included.custom.audience\"]")"
bff_client=apiary-keeper-bff
bff_secret="$(rj ".clients[] | select(.clientId == \"$bff_client\") | .secret")"
keycloak_image="$(sed -n 's/^[[:space:]]*image:[[:space:]]*\(quay\.io\/keycloak\/keycloak:[^[:space:]]*\).*/\1/p' "$repo/compose.yaml" | head -n 1)"
for value in "$realm_name" "$owner_password" "$owner_account" "$test_secret" "$audience" "$bff_secret" "$keycloak_image"; do
  [[ -n "$value" && "$value" != null ]] || { echo "::error::rollback-compat: the realm file or compose.yaml lacks a setting the sign-in needs." >&2; exit 2; }
done
[[ "$owner_account" == "$account" ]] || { echo "::error::rollback-compat: the realm's synthetic owner is not the seeded account's." >&2; exit 2; }

name="apiary-rollback-$$"
password="synthetic-$$-$RANDOM"
work="$(mktemp -d)"
cleanup() {
  docker rm -f "$name-app" "$name-db" "$name-kc" >/dev/null 2>&1 || true
  docker network rm "$name" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
fail() {
  echo "::error::rollback-compat: $*" >&2
  docker logs "$name-app" 2>&1 | tail -40 >&2 || true
  exit 1
}

docker network create "$name" >/dev/null
docker run -d --rm --name "$name-db" --network "$name" -e POSTGRES_DB=apiary -e POSTGRES_PASSWORD="$password" postgres:17 >/dev/null
for _ in $(seq 1 60); do docker exec "$name-db" pg_isready -U postgres -d apiary -h 127.0.0.1 >/dev/null 2>&1 && break; sleep 1; done
for run in 1 2; do
  docker exec -i "$name-db" psql -U postgres -d apiary --no-psqlrc --quiet -v ON_ERROR_STOP=1 <"$sql" >"$work/psql" 2>&1 ||
    { cat "$work/psql" >&2; fail "applying the migration script failed (run ${run})"; }
done
echo "rollback-compat: applied $(basename "$sql") twice"

# A throwaway Keycloak with the dev realm, as the integration tier starts one (TestKeycloak): its issuer pinned to the
# loopback address the token is minted on, its back-channel answering for the address it is asked on, so the app in a
# container on this network reaches it by name. A port taken between choosing and binding is retried.
mkdir -p "$work/import"
cp "$realm" "$work/import/"
kc_port=""
for _ in 1 2 3 4 5; do
  candidate=$((20000 + RANDOM % 20000))
  docker rm -f "$name-kc" >/dev/null 2>&1 || true
  if docker create --name "$name-kc" --network "$name" \
        -e "KC_HOSTNAME=http://127.0.0.1:$candidate" -e KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true \
        -p "127.0.0.1:$candidate:8080" "$keycloak_image" start-dev --import-realm >/dev/null &&
     docker cp "$work/import/." "$name-kc:/opt/keycloak/data/import" >/dev/null &&
     docker start "$name-kc" >/dev/null; then
    kc_port="$candidate"
    break
  fi
done
[[ -n "$kc_port" ]] || fail "could not start Keycloak ($keycloak_image) on a free port"
issuer="http://127.0.0.1:${kc_port}/realms/${realm_name}"
# Keycloak's first start imports the realm and takes the better part of a minute on a small runner.
for _ in $(seq 1 180); do curl -fsS -o /dev/null "$issuer/.well-known/openid-configuration" 2>/dev/null && break; sleep 1; done
curl -fsS -o /dev/null "$issuer/.well-known/openid-configuration" ||
  { docker logs "$name-kc" 2>&1 | tail -20 >&2 || true; fail "Keycloak never served the realm"; }

# Sign in as the realm's synthetic owner: the test client's password grant, the access token kept in a file and never
# printed (a failure prints Keycloak's error code, not its body).
curl -sS -o "$work/token.json" -X POST "$issuer/protocol/openid-connect/token" \
  --data-urlencode grant_type=password --data-urlencode "client_id=$test_client" --data-urlencode "client_secret=$test_secret" \
  --data-urlencode "username=$owner_name" --data-urlencode "password=$owner_password" --data-urlencode scope=openid ||
  fail "the token request for $owner_name did not complete"
token="$(jq -r '.access_token // empty' "$work/token.json" | tr -d '\r')"
[[ -n "$token" ]] || fail "Keycloak gave no access token for $owner_name ($(jq -r '.error // "no error given"' "$work/token.json" | tr -d '\r'))"
sub="$(jq -R -r 'split(".")[1] | gsub("-"; "+") | gsub("_"; "/") | @base64d | fromjson | .sub // empty' <<<"$token" | tr -d '\r')"
[[ "$sub" =~ ^[0-9a-f-]{36}$ ]] || fail "the access token has no usable sub"
printf 'Authorization: Bearer %s\n' "$token" >"$work/auth"
# The seeded user is the one the token acts as: its ExternalSubject is the token's sub, as an invitation will set it.
updated="$(docker exec -i "$name-db" psql -U postgres -d apiary --no-psqlrc --quiet --tuples-only --no-align -v ON_ERROR_STOP=1 \
  <<<"UPDATE \"Users\" SET \"ExternalSubject\" = '$sub' WHERE \"UserId\" = '$user' RETURNING \"UserId\";" 2>"$work/psql")" ||
  { cat "$work/psql" >&2; fail "setting the seeded user's ExternalSubject failed"; }
[[ "$(tr -d '\r\n' <<<"$updated")" == "$user" ]] || fail "the seeded user is not in the database, so the token has nobody to act as"

# The key ring certificate sign-in demands: made for this run, an RSA 2048 PKCS#12 with a password made up here.
cert_password="synthetic-$$-$RANDOM"
MSYS2_ARG_CONV_EXCL='/CN=' openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/key.pem" -out "$work/cert.pem" -days 1 -subj /CN=synthetic-rollback-keyring >/dev/null 2>&1 ||
  fail "could not make the key ring certificate"
openssl pkcs12 -export -inkey "$work/key.pem" -in "$work/cert.pem" -out "$work/keyring.p12" -passout "pass:$cert_password" >/dev/null 2>&1 ||
  fail "could not package the key ring certificate"
{
  echo "DOTNET_ENVIRONMENT=Staging"
  echo "ASPNETCORE_ENVIRONMENT=Staging"
  echo "ConnectionStrings__ApiaryCopilot=Host=$name-db;Port=5432;Database=apiary;Username=postgres;Password=$password"
  # Read by a release that predates sign-in alone; a release with sign-in reads neither.
  echo "CurrentAccount__Id=$account"
  echo "CurrentUser__Id=$user"
  echo "SignIn__Authority=$issuer"
  echo "SignIn__MetadataAddress=http://$name-kc:8080/realms/${realm_name}/.well-known/openid-configuration"
  echo "SignIn__ClientId=$bff_client"
  echo "SignIn__ClientSecret=$bff_secret"
  echo "SignIn__Audience=$audience"
  echo "SignIn__RequireHttps=false"
  echo "DataProtection__Certificate=$(base64 <"$work/keyring.p12" | tr -d '\r\n')"
  echo "DataProtection__CertificatePassword=$cert_password"
} >"$work/app.env"

docker run -d --rm --name "$name-app" --network "$name" -p 127.0.0.1::8080 --env-file "$work/app.env" "$image" >/dev/null
port="$(docker port "$name-app" 8080/tcp | head -1 | sed 's/.*://')"
base="http://127.0.0.1:${port}"
for _ in $(seq 1 60); do curl -fsS -o /dev/null "$base/health" 2>/dev/null && break; sleep 1; done
curl -fsS "$base/health" >"$work/health" || fail "$image never answered /health"
echo "rollback-compat: $image is up ($(cat "$work/health"))"

# The probe: what does this release say to a caller who has not signed in?
probe="$(curl -sS -o /dev/null -w '%{http_code}' "$base/api/yards")"
case "$probe" in
  401) echo "rollback-compat: $image answers an anonymous request 401: a release with sign-in; calling with a bearer token for $owner_name" ;;
  200) echo "rollback-compat: $image answers an anonymous request 200: a release that predates sign-in; it ignores the bearer token and acts for CurrentAccount__Id" ;;
  *) fail "an anonymous GET /api/yards answered $probe, expected 401 (a release with sign-in) or 200 (one that predates it)" ;;
esac

# post <path> <json> <want-status>: prints the new resource's id, from the Location header.
post() {
  local status
  status="$(curl -sS -o "$work/body" -D "$work/headers" -w '%{http_code}' -H @"$work/auth" -H 'Content-Type: application/json' -X POST --data "$2" "$base$1")"
  [[ "$status" == "$3" ]] || { cat "$work/body" >&2; fail "POST $1 answered $status, expected $3"; }
  tr -d '\r' <"$work/headers" | sed -n 's|^[Ll]ocation: .*/\([0-9a-f-]\{36\}\)$|\1|p'
}
get() {
  local status
  status="$(curl -sS -o "$work/body" -w '%{http_code}' -H @"$work/auth" "$base$1")"
  [[ "$status" == 200 ]] || { cat "$work/body" >&2; fail "GET $1 answered $status"; }
  grep -qF -- "$2" "$work/body" || fail "GET $1 did not return $2"
}

yard="$(post /api/yards '{"name":"Synthetic Rollback Yard","location":{"latitude":30.2672,"longitude":-97.7431},"state":"TX","county":"Travis","landowner":{"contact":"Synthetic Landowner, 555-0100","terms":"honey"}}' 201)"
[[ -n "$yard" ]] || fail "POST /api/yards gave no Location"
get "/api/yards/$yard" "Synthetic Rollback Yard"
colony="$(post /api/colonies "{\"tagId\":\"RB-01\",\"hiveNumber\":\"TX-9001\",\"yardId\":\"$yard\",\"hiveType\":\"Langstroth 10-frame\",\"boxCount\":2,\"occurredOn\":\"2026-04-01\"}" 201)"
[[ -n "$colony" ]] || fail "POST /api/colonies gave no Location"
get "/api/colonies/$colony" "RB-01"
post "/api/colonies/$colony/inspections" '{"inspectedOn":"2026-04-12","findings":{"queenSeen":true,"eggsSeen":true,"broodFrames":6,"broodPattern":"solid","foodStores":"adequate","temperament":"calm","diseaseSigns":[],"actionsTaken":[]},"notes":"Synthetic rollback check."}' 201 >/dev/null
get "/api/colonies/$colony/inspections" "Synthetic rollback check."
echo "rollback-compat: $image wrote and read a yard, a colony and an inspection on the migrated schema"
