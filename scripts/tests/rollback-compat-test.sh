#!/usr/bin/env bash
# Tests for scripts/rollback-compat.sh, with `docker` and `curl` as stubs on PATH: the script is the real one,
# reading the real realm file, compose.yaml and staging seed, so a rename in any of them goes red here. The stubs play
# the three things it talks to - Docker, Keycloak's token endpoint and an image's API - and each case sets what the
# image does: a release with sign-in (401 to an anonymous request, the bearer token required), a release that predates
# it (no sign-in, the header ignored), and the ways a run can fail. Needs jq and openssl, as the script does; needs no
# Docker daemon. Every case also checks that the access token appears in nothing the script printed.
# Run: bash scripts/tests/rollback-compat-test.sh
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
script="$repo/scripts/rollback-compat.sh"
bash -n "$script" || { echo "FAIL rollback-compat.sh does not parse"; exit 1; }
for tool in jq openssl base64; do command -v "$tool" >/dev/null 2>&1 || { echo "FAIL rollback-compat-test.sh needs $tool"; exit 1; }; done

expected_cases=41
cases=0
failed=0
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
sub=3f2a9c1e-5b7d-4e8a-9c0d-1a2b3c4d5e6f
b64url() { base64 | tr '+/' '-_' | tr -d '=\r\n'; }
token="eyJhbGciOiJub25lIn0.$(printf '{"sub":"%s"}' "$sub" | b64url).c3ludGhldGljLXNpZw"
user="$(sed -n "/^INSERT INTO \"Users\"/,/;/ s/^VALUES ('\([0-9a-f-]\{36\}\)'.*/\1/p" "$repo/scripts/sql/staging-seed.sql")"
account="$(sed -n "/^INSERT INTO \"Accounts\"/,/;/ s/^VALUES ('\([0-9a-f-]\{36\}\)'.*/\1/p" "$repo/scripts/sql/staging-seed.sql")"
kc_image="$(sed -n 's/^[[:space:]]*image:[[:space:]]*\(quay\.io\/keycloak\/keycloak:[^[:space:]]*\).*/\1/p' "$repo/compose.yaml" | head -n 1)"
[[ -n "$user" && -n "$account" && -n "$kc_image" ]] || { echo "FAIL the seed or compose.yaml no longer holds what the tests read"; exit 1; }

mkdir -p "$work/bin"
cat >"$work/bin/docker" <<'EOF'
#!/usr/bin/env bash
printf 'docker %s\n' "$*" >>"$WORK/calls"
case "$1" in
  create) [[ -z "${STUB_KC_FAIL:-}" ]] || exit 1 ;;
  run)
    for ((i = 1; i <= $#; i++)); do
      if [[ "${!i}" == --env-file ]]; then j=$((i + 1)); cp "${!j}" "$WORK/app.env"; fi
    done ;;
  port) echo "127.0.0.1:49152" ;;
  logs) echo "stub log line" ;;
  exec)
    stdin=""
    for a in "$@"; do [[ "$a" == -i ]] && stdin="$(cat)"; done
    if [[ "$stdin" == *UPDATE* ]]; then
      printf '%s\n' "$stdin" >"$WORK/update.sql"
      printf '%s\n' "${STUB_UPDATE_OUT-$USER_ID}"
      exit "${STUB_UPDATE_EXIT:-0}"
    elif [[ -n "$stdin" ]]; then
      exit "${STUB_MIGRATE_EXIT:-0}"
    fi ;;
esac
exit 0
EOF
cat >"$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
out="" hdr="" fmt="" method=GET url="" fail=0 authfile="" auth=no
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[i]}" in
    -o) out="${args[++i]}" ;;
    -D) hdr="${args[++i]}" ;;
    -w) fmt="${args[++i]}" ;;
    -X) method="${args[++i]}" ;;
    -H) h="${args[++i]}"; [[ "$h" == @* ]] && authfile="${h#@}" ;;
    --data|--data-urlencode) ((i++)) ;;
    -*f*) [[ "${args[i]}" != --* ]] && fail=1 ;;
    http*) url="${args[i]}" ;;
  esac
done
code=200 body=""
if [[ -n "$authfile" && "$(cat "$authfile")" == "Authorization: Bearer $TOKEN" ]]; then auth=yes; fi
case "$url" in
  */.well-known/openid-configuration) [[ -z "${STUB_NO_KC:-}" ]] || code=000 ;;
  */protocol/openid-connect/token)
    if [[ -n "${STUB_TOKEN_ERROR:-}" ]]; then body='{"error":"invalid_grant","error_description":"Invalid user credentials"}'
    else body="{\"access_token\":\"$TOKEN\",\"token_type\":\"Bearer\"}"; fi ;;
  */health) body='{"status":"ok","version":"9.9.9"}' ;;
  */api/*)
    path="${url#*://*/}"
    printf '%s /%s auth=%s\n' "$method" "$path" "$auth" >>"$WORK/api.log"
    case "$STUB_SERVER" in
      signin)
        if [[ "$auth" != yes || -n "${STUB_REJECT_TOKEN:-}" ]]; then code=401; fi ;;
      predates) ;;
      down) code=503 ;;
    esac
    if [[ "$code" == 200 ]]; then
      body="Synthetic Rollback Yard RB-01 Synthetic rollback check."
      if [[ "$method" == POST ]]; then
        code=201
        [[ -z "$hdr" ]] || printf 'HTTP/1.1 201 Created\r\nLocation: http://x/%s/0a1b2c3d-0000-4000-8000-000000000001\r\n\r\n' "${path%%\?*}" >"$hdr"
      fi
    fi ;;
esac
[[ -z "$out" ]] || printf '%s' "$body" >"$out"
[[ "$out" == /dev/null || -n "$out" ]] || printf '%s' "$body"
[[ "$fmt" != *http_code* ]] || printf '%s' "$code"
if [[ "$code" == 000 ]] || { ((fail)) && ((code >= 400)); }; then exit 22; fi
exit 0
EOF
chmod +x "$work/bin/docker" "$work/bin/curl"
printf -- '-- synthetic migration script\nSELECT 1;\n' >"$work/migration.sql"

# run <env assignments...>: the script against the stubs; sets $status and leaves its output in $work/out.
run() {
  rm -f "$work/calls" "$work/api.log" "$work/app.env" "$work/update.sql"
  env PATH="$work/bin:$PATH" WORK="$work" TOKEN="$token" USER_ID="$user" "$@" \
    bash "$script" example.test/apiary-keeper:previous "$work/migration.sql" >"$work/out" 2>&1
  status=$?
}
check() {
  cases=$((cases + 1))
  if [[ "$2" == "$3" ]]; then echo "ok   $1"; else failed=$((failed + 1)); echo "FAIL $1: got '$2', expected '$3'"; sed 's/^/     | /' "$work/out"; fi
}
# has <label> <file> <fixed text>: the text is in the file (counted, never piped into an early-exit grep).
has() { check "$1" "$(grep -cF -- "$3" "$2" 2>/dev/null)" "${4:-1}"; }
# clean: the token (the secret of the run) is in nothing the script printed.
clean() { check "$1: the access token is never printed" "$(grep -cF -- "$token" "$work/out")" 0; }
did_not_start_app() { check "$1: the image was never started" "$(grep -c -- '--env-file' "$work/calls" 2>/dev/null)" 0; }

# 1. A release with sign-in: 401 to the probe, the token accepted.
run STUB_SERVER=signin
check "sign-in release: passes" "$status" 0
has "sign-in release: names its mode" "$work/out" "a release with sign-in"
has "sign-in release: ends on the writes and reads" "$work/out" "wrote and read a yard, a colony and an inspection"
check "sign-in release: one anonymous probe, every other call signed in" "$(grep -c 'auth=no' "$work/api.log") $(grep -c 'auth=yes' "$work/api.log")" "1 6"
has "sign-in release: the probe is the anonymous call" "$work/api.log" "GET /api/yards auth=no"
has "sign-in release: three psql sessions - the script twice, then the subject" "$work/calls" "docker exec -i apiary-rollback-" 3
has "sign-in release: Keycloak is the image compose.yaml pins" "$work/calls" "$kc_image start-dev --import-realm"
has "sign-in release: the realm is copied in" "$work/calls" ":/opt/keycloak/data/import"
has "sign-in release: the seeded user is the token's sub" "$work/update.sql" "SET \"ExternalSubject\" = '$sub' WHERE \"UserId\" = '$user'"
has "sign-in release: Staging" "$work/app.env" "ASPNETCORE_ENVIRONMENT=Staging"
has "sign-in release: the issuer is the loopback address the token is minted on" "$work/app.env" "SignIn__Authority=http://127.0.0.1:"
has "sign-in release: the metadata is the container's own route" "$work/app.env" "-kc:8080/realms/apiary-keeper-dev/.well-known/openid-configuration"
has "sign-in release: http allowed for the throwaway Keycloak" "$work/app.env" "SignIn__RequireHttps=false"
has "sign-in release: the audience" "$work/app.env" "SignIn__Audience=apiary-keeper-api"
has "sign-in release: the BFF client" "$work/app.env" "SignIn__ClientId=apiary-keeper-bff"
has "sign-in release: a key ring certificate" "$work/app.env" "DataProtection__Certificate=MI"
clean "sign-in release"

# 2. A release that predates sign-in: it answers anonymously and ignores the header.
run STUB_SERVER=predates
check "predates sign-in: passes" "$status" 0
has "predates sign-in: names its mode" "$work/out" "a release that predates sign-in"
check "predates sign-in: the bearer token is sent all the same" "$(grep -c 'auth=yes' "$work/api.log")" 6
has "predates sign-in: it is given the account it acts for" "$work/app.env" "CurrentAccount__Id=$account"
has "predates sign-in: and the user" "$work/app.env" "CurrentUser__Id=$user"
clean "predates sign-in"

# 3. A release with sign-in that refuses the token fails, naming the call.
run STUB_SERVER=signin STUB_REJECT_TOKEN=1
check "token refused: fails" "$status" 1
has "token refused: names the call and the answer" "$work/out" "POST /api/yards answered 401, expected 201"
has "token refused: never claims success" "$work/out" "wrote and read" 0
clean "token refused"

# 4. An anonymous request answered with anything but 401 or 200 is neither mode: fail closed.
run STUB_SERVER=down
check "probe answered 503: fails" "$status" 1
has "probe answered 503: says what it expected" "$work/out" "answered 503, expected 401"
check "probe answered 503: nothing was written" "$(grep -c 'POST' "$work/api.log")" 0

# 5. Keycloak refuses the token request: fails before the image starts, and prints the error code, not the body.
run STUB_SERVER=signin STUB_TOKEN_ERROR=1
check "token request refused: fails" "$status" 1
has "token request refused: prints Keycloak's error code" "$work/out" "(invalid_grant)"
did_not_start_app "token request refused"

# 6. Keycloak cannot start / never serves the realm.
run STUB_SERVER=signin STUB_KC_FAIL=1
check "Keycloak cannot start: fails" "$status" 1
did_not_start_app "Keycloak cannot start"

# 7. The seeded user is not in the database: the token would act as nobody.
run STUB_SERVER=signin STUB_UPDATE_OUT=
check "seeded user missing: fails" "$status" 1
did_not_start_app "seeded user missing"

# 8. A migration script that does not apply.
run STUB_SERVER=signin STUB_MIGRATE_EXIT=1
check "migration script fails: fails" "$status" 1
did_not_start_app "migration script fails"

# 9. Arguments.
env PATH="$work/bin:$PATH" bash "$script" >"$work/out" 2>&1; check "no arguments: refused" "$?" 1
env PATH="$work/bin:$PATH" bash "$script" some/image "$work/missing.sql" >"$work/out" 2>&1; check "missing script: refused with 2" "$?" 2

if ((failed > 0 || cases != expected_cases)); then echo "rollback-compat-test: $failed failed of $cases"; exit 1; fi
echo "rollback-compat-test: $cases checks passed"
