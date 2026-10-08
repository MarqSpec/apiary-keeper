#!/usr/bin/env bash
# Deploy the images tagged :<environment> to that environment: called with the version being deployed, and again
# with the previous version to roll back; exit non-zero on failure.
# Credentials come from the job's environment, never from this file.
#
# Staging is Railway (DEPLOYMENT.md §4, §9): each service runs its image's moving tag (:staging), so
# `railway redeploy --from-source` makes it pull that tag again - the new version, or on a rollback the previous one.
# The services go one at a time, app before proxy, and each must reach SUCCESS before the next starts; a deployment
# that fails, crashes or does not finish in time fails the deploy; rolling back - deploying the previous version with
# this script - is the caller's.
#   RAILWAY_TOKEN           the staging environment's project token (GitHub secret RAILWAY_STAGING_TOKEN)
#   RAILWAY_SERVICES        the services to redeploy, in order (default: "app proxy")
#   RAILWAY_DEPLOY_TIMEOUT  seconds each service may take to reach SUCCESS (default: 600)
#   RAILWAY_DEPLOY_POLL     seconds between status reads (default: 10)
# Production is Phase 1 and has no target yet.
#
# Usage: scripts/deploy.sh <staging|production> <version>
set -euo pipefail
env="${1:?environment}"; version="${2:?version}"

# The newest deployment of a service: "<id> <status>", or nothing if there is none.
latest() {
  railway deployment list --service "$1" --environment "$env" --limit 1 --json |
    jq -r 'if length == 0 then empty else (.[0].id // "") + " " + (.[0].status // "") end'
}

railway_deploy() {
  [[ -n "${RAILWAY_TOKEN:-}" ]] || {
    echo "::error::RAILWAY_TOKEN is not set - map it from the ${env} environment's secret RAILWAY_STAGING_TOKEN (DEPLOYMENT.md section 4)." >&2
    exit 1
  }
  local timeout="${RAILWAY_DEPLOY_TIMEOUT:-600}" poll="${RAILWAY_DEPLOY_POLL:-10}" svc before id status deadline
  for svc in ${RAILWAY_SERVICES:-app proxy}; do
    before="$(latest "$svc")"
    before="${before%% *}"
    echo "Redeploying ${svc} in ${env} from :${env} (${version})"
    railway redeploy --service "$svc" --environment "$env" --from-source --yes
    # Wall-clock, so the wait ends however long each status read takes.
    deadline=$((SECONDS + timeout))
    while :; do
      id=""; status=""
      read -r id status <<<"$(latest "$svc")" || true
      if [[ -n "$id" && "$id" != "$before" ]]; then
        case "$status" in
          SUCCESS) echo "${svc}: deployment ${id} is SUCCESS"; break ;;
          FAILED|CRASHED|REMOVED|REMOVING)
            echo "::error::${svc}: deployment ${id} is ${status} - see \`railway logs ${id}\`." >&2; exit 1 ;;
          '') echo "::error::${svc}: deployment ${id} has no status in \`railway deployment list --json\`." >&2; exit 1 ;;
          *) ;;
        esac
      fi
      if (( SECONDS >= deadline )); then
        echo "::error::${svc}: no new deployment reached SUCCESS within ${timeout}s (last: ${id:-none} ${status})." >&2
        exit 1
      fi
      sleep "$poll"
    done
  done
}

case "$env" in
  staging)
    railway_deploy
    ;;
  production)
    # guide: Phase 1 - the same Railway command, with production's own project token (RAILWAY_PROD_TOKEN).
    echo "::notice::No deployment target configured for ${env} yet (scripts/deploy.sh): ${version} is tagged :${env}, not running anywhere."
    ;;
  *)
    echo "unknown environment '${env}'" >&2
    exit 2
    ;;
esac
