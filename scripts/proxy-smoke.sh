#!/usr/bin/env bash
# The proxy in front of the app, end to end, from the two images (ARCHITECTURE.md D-16). A release runs it against
# its :X.Y.Z images before they are deployed; locally, against images you built.
#
# Usage: scripts/proxy-smoke.sh <app-image> <proxy-image>
# Starts both on a private Docker network, the proxy alone published on 127.0.0.1, and checks: the app's /health and
# the PWA through the proxy; the security headers; HSTS only over HTTPS; an unknown host refused; the body-size
# limit; and the proxy's own metrics on their port. Then a second proxy with the staging gate on
# (PROXY_BASIC_AUTH): anonymous requests refused but /health; synthetic credentials, made for this run, let the PWA and the API
# through; a bearer token is refused by the gate; the password in no response header and no log. Last, the app's /metrics under the listed spellings: the
# proxy's own 404 through both proxies, with and without the credentials, while Prometheus's scrape of the app on the
# private network still answers, and the ways out of /health past the gate are refused.
set -euo pipefail
app_image="${1:?app image}"; proxy_image="${2:?proxy image}"
host="apiary-keeper.example.test"
run_id="proxy-smoke-$$"
expected_checks=70
checks=0

cleanup() {
  docker rm -f "${run_id}-proxy" "${run_id}-gated" "${run_id}-app" >/dev/null 2>&1 || true
  docker network rm "${run_id}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL $1" >&2
  echo "--- proxy logs" >&2; docker logs "${run_id}-proxy" >&2 2>&1 || true
  echo "--- gated proxy logs" >&2; docker logs "${run_id}-gated" >&2 2>&1 || true
  echo "--- app logs" >&2; docker logs "${run_id}-app" 2>&1 | tail -n 40 >&2 || true
  exit 1
}
ok() { checks=$((checks + 1)); echo "ok   $1"; }

docker network create "${run_id}" >/dev/null
# The app runs without a database, which /health and the PWA do not need, and without sign-in (no SignIn__ settings,
# DEPLOYMENT.md §3), so its yard endpoints answer 503: with no sign-in there is no account to act for. Those are proven
# against a database and a Keycloak by the integration tier, whose fixture starts both (APP_SERVER_COMMAND).
docker run -d --name "${run_id}-app" --network "${run_id}" --network-alias app \
  -e ASPNETCORE_ENVIRONMENT=Staging "${app_image}" >/dev/null
# A 1 KB body limit, so the limit is provable with a small request.
docker run -d --name "${run_id}-proxy" --network "${run_id}" \
  -p 127.0.0.1::8080 -p 127.0.0.1::9113 \
  -e PROXY_APP_HOSTS="${host}" -e PROXY_APP_UPSTREAM=app:8080 -e PROXY_MAX_BODY_SIZE=1k \
  "${proxy_image}" >/dev/null
proxy_port="$(docker port "${run_id}-proxy" 8080/tcp | head -n1 | sed 's/.*://')"
metrics_port="$(docker port "${run_id}-proxy" 9113/tcp | head -n1 | sed 's/.*://')"
base="http://127.0.0.1:${proxy_port}"

# The app takes a few seconds to start; the proxy answers 502 until it does.
for attempt in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' -H "Host: ${host}" "${base}/health" || true)"
  [[ "$code" == 200 ]] && break
  [[ $attempt == 30 ]] && fail "/health through the proxy never answered 200 (last: ${code})"
  sleep 1
done

body="$(curl -sf -H "Host: ${host}" "${base}/health")" || fail "/health through the proxy"
grep -q '"version"' <<<"$body" || fail "/health body has no version: ${body}"
ok "/health answers through the proxy: ${body}"

headers="$(curl -s -D - -o /dev/null -H "Host: ${host}" "${base}/")"
grep -qi '^HTTP/[0-9.]* 200' <<<"$headers" || fail "/ (the PWA) is not 200: ${headers}"
grep -qi '^content-type: text/html' <<<"$headers" || fail "/ is not HTML: ${headers}"
ok "the PWA answers through the proxy"

grep -qi "^x-content-type-options: nosniff" <<<"$headers" || fail "no X-Content-Type-Options: ${headers}"
grep -qi "^content-security-policy: frame-ancestors 'none'" <<<"$headers" || fail "no frame-ancestors: ${headers}"
ok "security headers are set"

grep -qi '^strict-transport-security' <<<"$headers" && fail "HSTS sent over plain HTTP: ${headers}"
ok "no HSTS over plain HTTP"

https_headers="$(curl -s -D - -o /dev/null -H "Host: ${host}" -H 'X-Forwarded-Proto: https' "${base}/")"
grep -qi '^strict-transport-security: max-age=' <<<"$https_headers" || fail "no HSTS behind TLS: ${https_headers}"
ok "HSTS behind TLS"

code="$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: elsewhere.example.test' "${base}/health")"
[[ "$code" == 421 ]] || fail "an unknown host answered ${code}, expected 421"
ok "an unknown host is refused (421)"

code="$(head -c 2048 /dev/zero | curl -s -o /dev/null -w '%{http_code}' -H "Host: ${host}" --data-binary @- "${base}/health")"
[[ "$code" == 413 ]] || fail "a 2 KB body answered ${code}, expected 413 under PROXY_MAX_BODY_SIZE=1k"
ok "a body over PROXY_MAX_BODY_SIZE is refused (413)"

metrics="$(curl -sf "http://127.0.0.1:${metrics_port}/metrics")" || fail "the proxy's /metrics"
grep -Eq '^nginx_connections_active [0-9]+$' <<<"$metrics" || fail "no nginx_connections_active: ${metrics}"
ok "the proxy's own metrics answer on 9113"

code="$(curl -s -o /dev/null -w '%{http_code}' -H "Host: ${host}" "http://127.0.0.1:${metrics_port}/health")"
[[ "$code" == 404 ]] || fail "the metrics port proxied /health (${code}), expected 404"
ok "the metrics port serves nothing else"

# The staging gate. The password reaches docker run by name, so it is not in docker's arguments; curl -u below does
# carry it in curl's, which is acceptable only because it is synthetic and lives for this run alone.
gate_user="smoke"
PROXY_BASIC_AUTH_PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -d '\n')"
export PROXY_BASIC_AUTH_PASSWORD
gate_password="${PROXY_BASIC_AUTH_PASSWORD}"
gate_encoded="$(printf '%s' "${gate_user}:${gate_password}" | base64 | tr -d '\n')"
docker run -d --name "${run_id}-gated" --network "${run_id}" \
  -p 127.0.0.1::8080 -p 127.0.0.1::9113 \
  -e PROXY_APP_HOSTS="${host}" -e PROXY_APP_UPSTREAM=app:8080 \
  -e PROXY_BASIC_AUTH=on -e PROXY_BASIC_AUTH_USER="${gate_user}" -e PROXY_BASIC_AUTH_PASSWORD \
  "${proxy_image}" >/dev/null
unset PROXY_BASIC_AUTH_PASSWORD
gated_port="$(docker port "${run_id}-gated" 8080/tcp | head -n1 | sed 's/.*://')"
gated_metrics_port="$(docker port "${run_id}-gated" 9113/tcp | head -n1 | sed 's/.*://')"
gated="http://127.0.0.1:${gated_port}"
all_headers=""
# Usage: gated_get PATH [curl options...]. Sets response (headers and body) and status (its first line), and keeps
# the headers for the leak check. Not run in a subshell, so all_headers grows.
gated_get() {
  local path="$1"; shift
  response="$(curl -s -D - -H "Host: ${host}" "$@" "${gated}${path}")"
  status="$(head -n1 <<<"$response")"
  all_headers+="$(sed '/^\r*$/q' <<<"$response")"$'\n'
}

for attempt in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' -H "Host: ${host}" "${gated}/health" || true)"
  [[ "$code" == 200 ]] && break
  [[ $attempt == 30 ]] && fail "gate on: anonymous /health never answered 200 (last: ${code})"
  sleep 1
done
gated_get /health
grep -q '"version"' <<<"$response" || fail "gate on: /health is not the app's: ${response}"
ok "gate on: /health stays open for the smoke check"

gated_get /
[[ "$status" =~ ^HTTP/[0-9.]+\ 401 ]] || fail "gate on: anonymous / answered ${status}, expected 401"
grep -qi '^www-authenticate: Basic realm="Apiary-Keeper"' <<<"$response" || fail "gate on: no WWW-Authenticate: ${response}"
if grep -q 'id="root"' <<<"$response"; then fail "gate on: the 401 carries the PWA: ${response}"; fi
grep -qi "^x-content-type-options: nosniff" <<<"$response" || fail "gate on: the 401 lacks the security headers: ${response}"
ok "gate on: an anonymous request to the PWA is refused (401) with no app content"

gated_get /api/yards
[[ "$status" =~ ^HTTP/[0-9.]+\ 401 ]] || fail "gate on: anonymous /api/yards answered ${status}, expected 401"
ok "gate on: an anonymous request to /api/* is refused (401)"

gated_get / -u "${gate_user}:${gate_password}x"
[[ "$status" =~ ^HTTP/[0-9.]+\ 401 ]] || fail "gate on: a wrong password answered ${status}, expected 401"
ok "gate on: a wrong password is refused (401)"

gated_get / -u "${gate_user}:${gate_password}"
[[ "$status" =~ ^HTTP/[0-9.]+\ 200 ]] || fail "gate on: / with the credentials answered ${status}, expected 200"
grep -q 'id="root"' <<<"$response" || fail "gate on: / with the credentials is not the PWA: ${response}"
ok "gate on: the credentials let the PWA through"

grep -Eq '<link rel="manifest"[^>]* crossorigin="use-credentials"' <<<"$response" \
  || fail "gate on: the PWA fetches its manifest without credentials, so it gets a 401 behind the gate: ${response}"
ok "gate on: the PWA fetches its manifest with the credentials"

gated_get /api/yards -u "${gate_user}:${gate_password}"
[[ "$status" =~ ^HTTP/[0-9.]+\ 503 ]] || fail "gate on: /api/yards with the credentials answered ${status}, expected the app's 503 (sign-in is off)"
ok "gate on: the credentials let the API through (the app's own 503, sign-in being off)"

# The gate and a bearer token both need the Authorization header. The gate takes it and strips it before the
# app sees the request, so an agent's bearer token cannot pass the gate: the gate refuses it as bad credentials. A
# browser is unaffected - the gate's Basic credentials ride the Authorization header and its session the cookie. So no
# staging smoke check signs in with a bearer token (the release's own /health check needs neither); the checks that do
# (scripts/rollback-compat.sh, the integration tier) call the container directly, never through the gate.
gated_get /api/yards -H 'Authorization: Bearer synthetic-not-a-token'
[[ "$status" =~ ^HTTP/[0-9.]+\ 401 ]] || fail "gate on: a bearer token at /api/yards answered ${status}, expected the gate's 401"
grep -qi '^www-authenticate: Basic realm="Apiary-Keeper"' <<<"$response" || fail "gate on: the refused bearer token got no Basic challenge: ${response}"
ok "gate on: a bearer token does not pass the gate - it needs the Authorization header the gate takes"

metrics="$(curl -sf "http://127.0.0.1:${gated_metrics_port}/metrics")" || fail "gate on: the proxy's /metrics"
grep -Eq '^nginx_connections_active [0-9]+$' <<<"$metrics" || fail "gate on: no nginx_connections_active: ${metrics}"
ok "gate on: the proxy's own metrics port is as before"

# The app's metrics under the listed spellings ().
# The app's routing ignores case and a trailing slash and resolves dot segments and percent-encoding, so each spelling
# marked 1 is answered with the metrics by the app itself on the private network (asserted, so the case is a live one);
# those marked 0 the issue names, and the app does not resolve. Sent raw, an encoded slash or a '#' made nginx and the
# app resolve a target differently (/metrics/x%2F../.. and /health#/../metrics were / and /health to nginx, /metrics/
# to the app); the proxy now sends the app the path it resolved and refuses both.
# Through either proxy each must be the proxy's own 404 page (the app's 404 has no body), with the gate off, and on with
# and without the credentials.
scrape="$(docker exec "${run_id}-proxy" curl -sf http://app:8080/metrics)" || fail "Prometheus's scrape of the app on the private network"
grep -q '^# TYPE ' <<<"$scrape" || fail "the app's /metrics on the private network is not Prometheus output: ${scrape:0:300}"
ok "Prometheus's scrape of the app's /metrics on the private network answers"

# Each SPELLING is sent as the request target exactly as written (--request-target): curl would otherwise rewrite it,
# upper-casing percent-encoding and, on Windows, turning a backslash into a slash. MSYS_NO_PATHCONV keeps Git Bash on
# Windows from rewriting a target that starts with '/' into a Windows path; elsewhere it does nothing.
# Usage: expect_app_metrics APP_SERVES SPELLING [curl options...]: the app itself, on the private network, answers
# SPELLING with its metrics when APP_SERVES is 1, and does not when it is 0.
expect_app_metrics() {
  local app_serves="$1" spelling="$2"; shift 2
  local direct
  direct="$(MSYS_NO_PATHCONV=1 docker exec "${run_id}-proxy" curl -s --request-target "$spelling" "$@" http://app:8080/)"
  if [[ "$app_serves" == 1 ]]; then
    grep -q '^# TYPE ' <<<"$direct" || fail "the app itself no longer answers ${spelling} $* with its metrics; mark it 0"
  else
    if grep -q '^# TYPE ' <<<"$direct"; then fail "the app itself answers ${spelling} $* with its metrics; mark it 1"; fi
  fi
}
# Usage: expect_proxy_404 SPELLING [curl options...]: the proxy's own 404 page, through the ungated proxy and the gated
# one with and without the credentials.
expect_proxy_404() {
  local spelling="$1"; shift
  local target label url response code auth
  for target in "proxy ${base}" "gated ${gated}" "gated-with-credentials ${gated}"; do
    label="${target% *}"; url="${target#* }"
    auth=(); [[ "$label" == gated-with-credentials ]] && auth=(-u "${gate_user}:${gate_password}")
    response="$(MSYS_NO_PATHCONV=1 curl -s -w '\n%{http_code}' -H "Host: ${host}" "${auth[@]}" --request-target "$spelling" "$@" "${url}/")"
    code="${response##*$'\n'}"
    [[ "$code" == 404 ]] || fail "${label}: ${spelling} $* answered ${code}, expected the proxy's 404: ${response:0:300}"
    grep -q '<h1>404 Not Found</h1>' <<<"$response" || fail "${label}: ${spelling} $* is not the proxy's 404 page: ${response:0:300}"
    if grep -q '^# TYPE ' <<<"$response"; then fail "${label}: ${spelling} $* carries the app's metrics"; fi
  done
}

for entry in '/metrics 1' '/METRICS 1' '/Metrics 1' '/metrics/ 1' '/METRICS/ 1' '//metrics 0' '/metrics/. 1' \
             '/metrics/./ 1' '/./metrics 1' '/x/../METRICS 1' '/metrics//.. 1' '/metrics/x/.. 1' '/%4detrics 1' \
             '/%6d%65%74%72%69%63%73 1' '/%2e/metrics 1' '/x/%2e%2e/metrics 1' '/metrics/%2e 1' '/%2Fmetrics 0' \
             '/metrics?x=1 1' '/metrics;x=1 0' '/metrics. 0' \
             '/metrics/x%2F../.. 1' '/metrics/x%2f../.. 1' '/METRICS/x%2F%2E%2E/%2E%2E 1' '/metrics/a%2F%2E%2E/.. 1' \
             '/metrics/a/b%2F../../.. 1' '/api/../metrics/x%2F../.. 1' '/metrics/x%2F../..?a=1 1' '/metrics/x%5C../.. 1' \
             '/metrics/x%5c%2e%2e/.. 1' '/metrics/x%252F../.. 1' '/x/%2e%2e/metrics/y%2F%2e%2e/%2e%2e 1' \
             '/x#/../metrics 1' '/#/../metrics 1' '/x#/../METRICS 1' '/api#/../metrics 1' '/x/#/../../metrics 1' \
             '/health#/../metrics 1' '/health?#/../metrics 0' '/metrics# 0'; do
  spelling="${entry% *}"
  expect_app_metrics "${entry##* }" "$spelling"
  expect_proxy_404 "$spelling"
  ok "the app's metrics as ${spelling} are the proxy's 404, gate off and on, with and without the credentials"
done

expect_app_metrics 1 /metrics/x%2F../.. --http1.0
expect_proxy_404 /metrics/x%2F../.. --http1.0
ok "the app's metrics as /metrics/x%2F../.. over HTTP/1.0 are the proxy's 404, gate off and on, with and without the credentials"

expect_app_metrics 1 'http://app:8080/metrics/x%2F../..'
expect_proxy_404 "http://${host}/metrics/x%2F../.."
ok "the app's metrics as /metrics/x%2F../.. in absolute form are the proxy's 404, gate off and on, with and without the credentials"

expect_app_metrics 1 /health#/../metrics --http1.0
expect_proxy_404 /health#/../metrics --http1.0
ok "the app's metrics as /health#/../metrics over HTTP/1.0 are the proxy's 404, gate off and on, with and without the credentials"

# The app reads an absolute-form '#' as a fragment; the proxy forwarded the target to it in origin form.
expect_app_metrics 1 '/health#/../metrics'
expect_proxy_404 "http://${host}/health#/../metrics"
ok "the app's metrics as /health#/../metrics in absolute form are the proxy's 404, gate off and on, with and without the credentials"

# The staging gate's one open path, /health, used to reach the rest of the app: sent raw, /health#/../ was
# /health to nginx (open) and / to the app. The app itself answers each target below with the PWA or the API (asserted,
# so the case is live); through the proxy each is its 404, in every mode, with no app content.
for entry in '/health#/../ id="root"' '/health#/../api/yards HTTP/1.1 503'; do
  target="${entry%% *}"; app_sign="${entry#* }"
  direct="$(MSYS_NO_PATHCONV=1 docker exec "${run_id}-proxy" curl -s -i --request-target "$target" http://app:8080/)"
  grep -qF "$app_sign" <<<"$direct" || fail "the app itself no longer answers ${target} with '${app_sign}': ${direct:0:300}"
  expect_proxy_404 "$target"
  ok "gate bypass: ${target} is the proxy's 404, gate off and on, with and without the credentials, never the app"
done

# Out of /health by dot segments: resolved before the gate, so the gate asks and nothing reaches the app.
for target in '/health/../' '/health/../api/yards' '/health/%2e%2e/api/yards' '/health;/../api/yards' '/health/x/..'; do
  response="$(MSYS_NO_PATHCONV=1 curl -s -i -H "Host: ${host}" --request-target "$target" "${gated}/")"
  [[ "$(head -n1 <<<"$response")" =~ ^HTTP/[0-9.]+\ 401 ]] || fail "gate on: anonymous ${target} answered $(head -n1 <<<"$response"), expected 401"
  if grep -qE 'id="root"|"version"|"type"' <<<"$response"; then fail "gate on: anonymous ${target} carries app content: ${response:0:300}"; fi
done
ok "gate on: out of /health by dot segments is the gate's 401 with no app content"

# Dot segments resolved before the gate open nothing but /health itself: /x/../health is the open /health.
response="$(MSYS_NO_PATHCONV=1 curl -s -H "Host: ${host}" --request-target '/x/../health' "${gated}/")"
grep -q '"version"' <<<"$response" || fail "gate on: anonymous /x/../health is not the app's /health: ${response:0:300}"
ok "gate on: /x/../health is the open /health, answered by the app"

code="$(curl -s -o /dev/null -w '%{http_code}' -H "Host: ${host}" "${base}/?next=%2Fyards")"
[[ "$code" == 200 ]] || fail "an encoded slash in the query string answered ${code}, expected the PWA's 200"
ok "an encoded slash in the query string still reaches the app"

grep -qF '"GET /metrics/x%2F../.. HTTP/1.1" 404' <<<"$(docker logs "${run_id}-proxy" 2>&1)" \
  || fail "the proxy did not log the path as sent: $(docker logs "${run_id}-proxy" 2>&1 | tail -n 5)"
if grep -qF 'next=' <<<"$(docker logs "${run_id}-proxy" 2>&1)"; then fail "the proxy logged a query string"; fi
ok "the proxy logs the path as sent, without its query string"

grep -q 'HTTP/' <<<"$all_headers" || fail "gate on: no response headers were collected"
logs="$(docker logs "${run_id}-gated" 2>&1; docker logs "${run_id}-app" 2>&1)"
grep -q '"GET / HTTP/1.1" 401' <<<"$logs" || fail "gate on: the gated proxy logged no request"
for secret in "${gate_password}" "${gate_encoded}"; do
  if grep -qF -- "$secret" <<<"$all_headers"; then fail "gate on: the password is in a response header"; fi
  if grep -qF -- "$secret" <<<"$logs"; then fail "gate on: the password is in the proxy's or the app's logs"; fi
done
ok "gate on: the password is in no response header and no log"

[[ $checks -eq $expected_checks ]] || fail "ran ${checks} checks, expected ${expected_checks}"
echo "all ${checks} proxy smoke checks passed"
