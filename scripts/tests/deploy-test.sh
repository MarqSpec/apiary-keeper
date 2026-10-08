#!/usr/bin/env bash
# Tests for scripts/deploy.sh, against a fake Railway: `railway` is a stub on PATH that keeps each service's
# deployments as a file. `redeploy` adds one whose statuses come from SEQ_<service> (space-separated, one per status
# read, the last repeating); `deployment list --json` prints the newest, as `[{"id":…,"status":…}]`.
# Run: bash scripts/tests/deploy-test.sh
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
bash -n "$repo/scripts/deploy.sh" || { echo "FAIL deploy.sh does not parse"; exit 1; }

expected_cases=12
cases=0
failed=0

setup() {
  work="$(mktemp -d)"
  mkdir -p "$work/bin" "$work/state"
  cat >"$work/bin/railway" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >>"$WORK/calls"
svc=""; envname=""; args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  case "${args[$i]}" in --service) svc="${args[$((i + 1))]}" ;; --environment) envname="${args[$((i + 1))]}" ;; esac
done
[[ "$envname" == staging ]] || { echo "stub: wrong environment '$envname'" >&2; exit 65; }
state="$STATE/$svc"
case "$1 ${2:-}" in
  "redeploy --service")
    [[ -n "${REDEPLOY_FAIL:-}" ]] && { echo "stub: redeploy refused" >&2; exit 1; }
    [[ -n "${REDEPLOY_NOOP:-}" ]] && exit 0
    n=$(( $(cat "$state.n" 2>/dev/null || echo 0) + 1 )); echo "$n" >"$state.n"
    seqvar="SEQ_${svc}"; echo "${!seqvar:-SUCCESS}" >"$state.seq"; echo "dep-$svc-$n" >"$state.id" ;;
  "deployment list")
    [[ -f "$state.id" ]] || { echo '[]'; exit 0; }
    read -ra seq <"$state.seq"; status="${seq[0]}"
    (( ${#seq[@]} > 1 )) && echo "${seq[*]:1}" >"$state.seq"
    [[ "$status" == NONE ]] && { printf '[{"id":"%s"}]\n' "$(cat "$state.id")"; exit 0; }
    printf '[{"id":"%s","status":"%s"}]\n' "$(cat "$state.id")" "$status" ;;
  *) exit 64 ;;
esac
EOF
  chmod +x "$work/bin/railway"
  : >"$work/calls"
}
# seed <service>: an existing deployment, as a service created in the dashboard has.
seed() { echo 1 >"$work/state/$1.n"; echo "dep-$1-1" >"$work/state/$1.id"; echo SUCCESS >"$work/state/$1.seq"; }
deploy() {
  (cd "$repo" && PATH="$work/bin:$PATH" WORK="$work" STATE="$work/state" RAILWAY_TOKEN="${TOKEN-synthetic-token}" \
    RAILWAY_DEPLOY_POLL=0 RAILWAY_DEPLOY_TIMEOUT="${TIMEOUT:-30}" bash scripts/deploy.sh "$@") >"$work/out" 2>&1
}
check() {
  cases=$((cases + 1))
  if [[ "$2" == "$3" ]]; then echo "ok   $1"; else failed=$((failed + 1)); echo "FAIL $1: got '$2', expected '$3'"; cat "$work/out"; fi
}
redeploys() { grep -c '^redeploy' "$work/calls"; }
said() { grep -cF -- "$1" "$work/out"; }

# 1. Staging: app, then proxy, each pulled again from its tag and waited on until SUCCESS.
setup; seed app; seed proxy
SEQ_app="DEPLOYING DEPLOYING SUCCESS" SEQ_proxy="INITIALIZING SUCCESS" deploy staging 1.0.0; status=$?
check "staging redeploys app then proxy and waits for SUCCESS" \
  "$status $(grep '^redeploy' "$work/calls" | tr '\n' '|')" \
  "0 redeploy --service app --environment staging --from-source --yes|redeploy --service proxy --environment staging --from-source --yes|"
check "it waits for the new deployment, not the one that ran before" "$(said 'app: deployment dep-app-2 is SUCCESS')" "1"
rm -rf "$work"

# 2. A failed or crashed deployment fails the deploy, and the next service is never redeployed.
setup; seed app; seed proxy
SEQ_app="DEPLOYING FAILED" deploy staging 1.0.0; status=$?
check "a FAILED app deployment fails, and proxy is not redeployed" "$((status != 0)) $(redeploys) $(said 'is FAILED')" "1 1 1"
rm -rf "$work"
setup; seed app; seed proxy
SEQ_proxy="CRASHED" deploy staging 1.0.0; status=$?
check "a CRASHED proxy deployment fails" "$((status != 0)) $(said 'proxy: deployment dep-proxy-2 is CRASHED')" "1 1"
rm -rf "$work"

setup; seed app; seed proxy
SEQ_app="DEPLOYING REMOVED" TIMEOUT=20 deploy staging 1.0.0; status=$?
check "a REMOVED deployment fails at once, not at the timeout" "$((status != 0)) $(redeploys) $(said 'app: deployment dep-app-2 is REMOVED')" "1 1 1"
rm -rf "$work"

# 3. A deployment that never finishes fails at the timeout, not never.
setup; seed app; seed proxy
SEQ_app="DEPLOYING" TIMEOUT=2 deploy staging 1.0.0; status=$?
check "a deployment still DEPLOYING at the timeout fails" "$((status != 0)) $(redeploys) $(said 'no new deployment reached SUCCESS')" "1 1 1"
rm -rf "$work"

# 4. The old deployment's SUCCESS is never read as the new one's: a redeploy that created nothing times out.
setup; seed app; seed proxy
REDEPLOY_NOOP=1 TIMEOUT=1 deploy staging 1.0.0; status=$?
check "a redeploy that made no new deployment fails, though the old one is SUCCESS" "$((status != 0)) $(said 'no new deployment reached SUCCESS')" "1 1"
rm -rf "$work"

# 5. A status the CLI does not report fails loudly instead of waiting it out.
setup; seed app; seed proxy
SEQ_app="NONE" deploy staging 1.0.0; status=$?
check "a deployment with no status fails" "$((status != 0)) $(said 'has no status')" "1 1"
rm -rf "$work"

# 6. The redeploy command itself failing fails the deploy.
setup; seed app; seed proxy
REDEPLOY_FAIL=1 deploy staging 1.0.0; status=$?
check "a refused redeploy fails" "$((status != 0)) $(said 'redeploy refused')" "1 1"
rm -rf "$work"

# 7. No token: refused before Railway is called.
setup; seed app; seed proxy
TOKEN='' deploy staging 1.0.0; status=$?
check "no RAILWAY_TOKEN fails, and railway is never called" "$((status != 0)) $(wc -l <"$work/calls" | tr -d ' ') $(said RAILWAY_STAGING_TOKEN)" "1 0 1"
rm -rf "$work"

# 8. Production has no target yet (Phase 1); an unknown environment is refused.
setup
deploy production 1.0.0; status=$?
check "production passes with a notice and calls nothing" "$status $(wc -l <"$work/calls" | tr -d ' ') $(said 'No deployment target configured for production')" "0 0 1"
deploy qa 1.0.0; status=$?
check "an unknown environment is refused" "$status $(wc -l <"$work/calls" | tr -d ' ')" "2 0"
rm -rf "$work"

if [[ $cases -ne $expected_cases ]]; then echo "FAIL ran $cases cases, expected $expected_cases"; exit 1; fi
if [[ $failed -ne 0 ]]; then echo "$failed of $cases deploy.sh tests failed"; exit 1; fi
echo "all $cases deploy.sh tests passed"
