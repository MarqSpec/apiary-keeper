#!/bin/sh
# The proxy's configuration tests, run by the image's `test` stage (docker build --target test proxy). Each case starts
# the real entrypoint with `nginx -t`: a good configuration must render and pass nginx's own check; every refusal must
# exit non-zero, print its own message naming its setting (so a different refusal cannot satisfy the case), and render
# nothing (the guarded step did not run). Refusal inputs are fixed, never random, so each case reaches the same guard
# on every run. The last cases start nginx
# itself, in front of a stub app on 127.0.0.1:8081, and send it requests through the staging gate (PROXY_BASIC_AUTH),
# the listed spellings of the app's /metrics and of a way out of /health past the gate, with the gate on and off, and
# the target the app is sent.
set -u

rendered=/etc/nginx/conf.d/default.conf
htpasswd=/etc/nginx/auth/htpasswd
stub=/etc/nginx/conf.d/stub-app.conf
log=/tmp/proxy-test.log
expected_cases=228
cases=0
failed=0

pass() { cases=$((cases + 1)); echo "ok   $1"; }
fail() { cases=$((cases + 1)); failed=$((failed + 1)); echo "FAIL $1: $2"; }

# Usage: run_entrypoint VAR=value ...   (every PROXY_* not given is unset)
run_entrypoint() {
  rm -f "$rendered" "$htpasswd"
  env -u PROXY_APP_HOSTS -u PROXY_APP_UPSTREAM -u PROXY_MAX_BODY_SIZE \
      -u PROXY_BASIC_AUTH -u PROXY_BASIC_AUTH_USER -u PROXY_BASIC_AUTH_PASSWORD \
      "$@" /docker-entrypoint.sh nginx -t 2>&1
}

# Renders and checks a good configuration; fails the case and returns 1 on any fault, passes nothing itself.
render_ok() {
  name="$1"; shift
  out="$(run_entrypoint "$@")"; status=$?
  if [ "$status" -ne 0 ]; then fail "$name" "exit $status: $out"; return 1; fi
  if ! printf '%s\n' "$out" | grep -q 'test is successful'; then fail "$name" "nginx -t did not report success: $out"; return 1; fi
  if [ ! -f "$rendered" ]; then fail "$name" "nothing rendered"; return 1; fi
  if grep -q '\${' "$rendered"; then fail "$name" "unrendered placeholder left: $(grep '\${' "$rendered")"; return 1; fi
}

expect_ok() {
  render_ok "$@" && pass "$1"
}

# Usage: expect_refused NAME SETTING MESSAGE HIDDEN VAR=value ...
# The output must hold 'proxy: ', then SETTING, then MESSAGE (the guard's own words, matched literally and across lines,
# since a refusal may echo a value holding a newline). HIDDEN (when not empty) must not appear in the output.
expect_refused() {
  name="$1"; setting="$2"; message="$3"; hidden="$4"; shift 4
  out="$(run_entrypoint "$@")"; status=$?
  if [ "$status" -eq 0 ]; then fail "$name" "exit 0, expected a refusal"; return; fi
  case "$out" in
    *"proxy: "*"$setting"*"$message"*) ;;
    *) fail "$name" "not the refusal '$setting ... $message': $out"; return ;;
  esac
  if [ -n "$hidden" ]; then
    case "$out" in *"$hidden"*) fail "$name" "the refusal prints the credential"; return ;; esac
  fi
  if [ -f "$rendered" ]; then fail "$name" "the configuration was rendered anyway"; return; fi
  if [ -f "$htpasswd" ]; then fail "$name" "the password file was written anyway"; return; fi
  pass "$name"
}

nl='
'
good_hosts="PROXY_APP_HOSTS=apiary-keeper.example.test localhost"
good_upstream="PROXY_APP_UPSTREAM=app:8080"
good_size="PROXY_MAX_BODY_SIZE=30m"
# Synthetic credentials, made for this run only: nothing is committed.
user="gate-test"
password="$(head -c 24 /dev/urandom | base64 | tr -d '\n')"
gate_on="PROXY_BASIC_AUTH=on"
good_user="PROXY_BASIC_AUTH_USER=$user"
good_password="PROXY_BASIC_AUTH_PASSWORD=$password"

# Each refusal's own words, so a case is satisfied only by the guard it names.
hosts_missing="to the host names the app answers on"
hosts_bad="may hold only lower-case host names separated by spaces"
upstream_missing="to the app's private address"
upstream_bad="must be host:port"
size_missing=", e.g. '30m'"
size_bad="must be a size such as '30m'"

expect_ok      "a complete configuration renders and passes nginx -t" "$good_hosts" "$good_upstream" "$good_size"
expect_refused "no host names"            PROXY_APP_HOSTS "$hosts_missing" "" "$good_upstream" "$good_size"
expect_refused "a host name with a ';'"   PROXY_APP_HOSTS "$hosts_bad" "" "PROXY_APP_HOSTS=evil.test; return 200" "$good_upstream" "$good_size"
expect_refused "a host name with a newline" PROXY_APP_HOSTS "$hosts_bad" "" "PROXY_APP_HOSTS=evil.test$nl" "$good_upstream" "$good_size"
expect_refused "no upstream"              PROXY_APP_UPSTREAM "$upstream_missing" "" "$good_hosts" "$good_size"
expect_refused "an upstream with no port" PROXY_APP_UPSTREAM "$upstream_bad" "" "$good_hosts" "PROXY_APP_UPSTREAM=app" "$good_size"
expect_refused "an upstream with a ';'"   PROXY_APP_UPSTREAM "$upstream_bad" "" "$good_hosts" "PROXY_APP_UPSTREAM=app:8080; return 200" "$good_size"
expect_refused "an upstream with a '\$'"  PROXY_APP_UPSTREAM "$upstream_bad" "" "$good_hosts" 'PROXY_APP_UPSTREAM=app:$host' "$good_size"
expect_refused "an upstream with a newline" PROXY_APP_UPSTREAM "$upstream_bad" "" "$good_hosts" "PROXY_APP_UPSTREAM=app:8080$nl" "$good_size"
expect_refused "no body size"             PROXY_MAX_BODY_SIZE "$size_missing" "" "$good_hosts" "$good_upstream"
expect_refused "a body size that is not a size" PROXY_MAX_BODY_SIZE "$size_bad" "" "$good_hosts" "$good_upstream" "PROXY_MAX_BODY_SIZE=lots"
expect_refused "a body size with a ';'"   PROXY_MAX_BODY_SIZE "$size_bad" "" "$good_hosts" "$good_upstream" "PROXY_MAX_BODY_SIZE=30m; return 200"

# The staging gate ().
name="the gate, off by default, writes no password file and renders auth_basic off"
if render_ok "$name" "$good_hosts" "$good_upstream" "$good_size"; then
  if [ -f "$htpasswd" ]; then fail "$name" "a password file was written"
  elif ! grep -q '^ *auth_basic off;$' "$rendered"; then fail "$name" "no 'auth_basic off;' line: $(grep auth_basic "$rendered")"
  elif grep -q 'auth_basic_user_file' "$rendered"; then fail "$name" "auth_basic_user_file rendered"
  else pass "$name"; fi
fi
name="the gate on renders a hashed password file and passes nginx -t"
if render_ok "$name" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user" "$good_password"; then
  line="$(cat "$htpasswd" 2>/dev/null)"
  mode="$(stat -c '%a' "$htpasswd" 2>/dev/null)"
  case "$line" in
    "$user:\$6\$"*) ok_line=1 ;;
    *) ok_line=0 ;;
  esac
  if [ "$ok_line" -ne 1 ]; then fail "$name" "the password file is not '$user:\$6\$...'"
  elif [ "$(wc -l < "$htpasswd")" -ne 1 ]; then fail "$name" "the password file holds more than one line"
  elif [ "$mode" != 600 ]; then fail "$name" "the password file's mode is $mode, expected 600"
  elif grep -qF "$password" "$htpasswd" "$rendered"; then fail "$name" "the password is written in clear"
  elif ! grep -q "auth_basic_user_file $htpasswd;" "$rendered"; then fail "$name" "auth_basic_user_file not rendered"
  elif ! grep -q 'proxy_set_header Authorization "";' "$rendered"; then fail "$name" "the Authorization header is passed to the app"
  else pass "$name"; fi
fi
gate_bad="must be 'on' or 'off'"
user_missing=", the user name the staging gate asks for"
password_missing=", the staging gate's shared secret"
gate_not_on="is not 'on' but PROXY_BASIC_AUTH_USER or PROXY_BASIC_AUTH_PASSWORD is set"
user_chars="may hold only letters, digits, '.', '_' and '-'"
user_long="may be at most 64 characters"
password_chars="may hold only letters, digits and '.', '_', '~', '+', '/', '='"
password_short="must be at least 16 characters"
password_long="may be at most 128 characters"
# Usage: repeat CHAR COUNT. Fixed lengths made of allowed characters, so only the length guard can refuse them.
repeat() { printf "%${2}s" '' | tr ' ' "$1"; }
user_64="$(repeat u 64)"; user_65="$(repeat u 65)"
password_16="$(repeat p 16)"; password_15="$(repeat p 15)"
password_128="$(repeat p 128)"; password_129="$(repeat p 129)"
# A fixed password of allowed characters, the base of the character cases below.
base_password="$(repeat p 20)"

# Each length at its limit is accepted, so a guard narrowed by one fails too.
expect_ok      "a 64-character user name and a 128-character password render" "$good_hosts" "$good_upstream" "$good_size" \
  "$gate_on" "PROXY_BASIC_AUTH_USER=$user_64" "PROXY_BASIC_AUTH_PASSWORD=$password_128"
expect_ok      "a 16-character password renders" "$good_hosts" "$good_upstream" "$good_size" \
  "$gate_on" "$good_user" "PROXY_BASIC_AUTH_PASSWORD=$password_16"
# No credentials here, so the only refusal left is the value's own.
expect_refused "the gate neither on nor off"          PROXY_BASIC_AUTH "$gate_bad" "" "$good_hosts" "$good_upstream" "$good_size" "PROXY_BASIC_AUTH=yes"
expect_refused "the gate 'on' with a newline"         PROXY_BASIC_AUTH "$gate_bad" "" "$good_hosts" "$good_upstream" "$good_size" "PROXY_BASIC_AUTH=on$nl"
expect_refused "the gate on with no user"             PROXY_BASIC_AUTH_USER "$user_missing" "" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_password"
expect_refused "the gate on with no password"         PROXY_BASIC_AUTH_PASSWORD "$password_missing" "" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user"
expect_refused "credentials set with the gate unset"  PROXY_BASIC_AUTH "$gate_not_on" "$password" "$good_hosts" "$good_upstream" "$good_size" "$good_user" "$good_password"
expect_refused "credentials set with the gate off"    PROXY_BASIC_AUTH "$gate_not_on" "$password" "$good_hosts" "$good_upstream" "$good_size" "PROXY_BASIC_AUTH=off" "$good_user" "$good_password"
expect_refused "a password shorter than 16"           PROXY_BASIC_AUTH_PASSWORD "$password_short" "$password_15" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user" "PROXY_BASIC_AUTH_PASSWORD=$password_15"
expect_refused "a password longer than 128"           PROXY_BASIC_AUTH_PASSWORD "$password_long" "$password_129" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user" "PROXY_BASIC_AUTH_PASSWORD=$password_129"
expect_refused "a user name longer than 64"           PROXY_BASIC_AUTH_USER "$user_long" "$user_65" "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "PROXY_BASIC_AUTH_USER=$user_65" "$good_password"

# Every character that could break out of the password file or the nginx configuration, in each credential.
for pair in "semicolon|;" "open brace|{" "close brace|}" "dollar|\$" "double quote|\"" "single quote|'" \
            "backtick|\`" "hash|#" "space| " "colon|:" "backslash|\\"; do
  label="${pair%%|*}"; c="${pair#*|}"
  bad_user="ga${c}te"
  expect_refused "a user name with a $label" PROXY_BASIC_AUTH_USER "$user_chars" "$bad_user" \
    "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "PROXY_BASIC_AUTH_USER=$bad_user" "$good_password"
  bad_password="${base_password}${c}x"
  expect_refused "a password with a $label" PROXY_BASIC_AUTH_PASSWORD "$password_chars" "$bad_password" \
    "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user" "PROXY_BASIC_AUTH_PASSWORD=$bad_password"
done
expect_refused "a user name with a newline" PROXY_BASIC_AUTH_USER "$user_chars" "" \
  "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "PROXY_BASIC_AUTH_USER=$user${nl}other" "$good_password"
expect_refused "a password with a newline" PROXY_BASIC_AUTH_PASSWORD "$password_chars" "${base_password}${nl}" \
  "$good_hosts" "$good_upstream" "$good_size" "$gate_on" "$good_user" "PROXY_BASIC_AUTH_PASSWORD=${base_password}${nl}other"

# Requests through the gate: nginx in front of a stub app that echoes the Authorization header and the request target
# it receives.
cat > "$stub" <<'EOF'
server {
    listen 127.0.0.1:8081;
    access_log off;
    location / {
        default_type text/plain;
        return 200 "stub-app authorization=[$http_authorization] target=[$request_uri]\n";
    }
}
EOF

start_proxy() {
  rm -f "$rendered" "$htpasswd" "$log"
  env -u PROXY_APP_HOSTS -u PROXY_APP_UPSTREAM -u PROXY_MAX_BODY_SIZE \
      -u PROXY_BASIC_AUTH -u PROXY_BASIC_AUTH_USER -u PROXY_BASIC_AUTH_PASSWORD \
      "PROXY_APP_HOSTS=apiary-keeper.example.test" "PROXY_APP_UPSTREAM=127.0.0.1:8081" "$good_size" "$@" \
      /docker-entrypoint.sh nginx >"$log" 2>&1
}
stop_proxy() {
  nginx -s stop 2>/dev/null
  for _ in 1 2 3 4 5 6 7 8 9 10; do [ -f /tmp/nginx.pid ] || return 0; sleep 0.2; done
}
# Usage: request PATH [curl options...]; sets code and body.
request() {
  path="$1"; shift
  body="$(curl -s -o - -w '\n%{http_code}' -H 'Host: apiary-keeper.example.test' "$@" "http://127.0.0.1:8080$path")"
  code="${body##*"$nl"}"; body="${body%"$nl"*}"
}
# Usage: expect_request NAME CODE BODY PATH [curl options...]; BODY '!text' means the body must not hold text.
expect_request() {
  name="$1"; want_code="$2"; want_body="$3"; path="$4"; shift 4
  request "$path" "$@"
  if [ "$code" != "$want_code" ]; then fail "$name" "answered $code, expected $want_code: $body"; return; fi
  case "$want_body" in
    !*) case "$body" in *"${want_body#!}"*) fail "$name" "the body holds '${want_body#!}': $body"; return ;; esac ;;
    *) case "$body" in *"$want_body"*) ;; *) fail "$name" "the body lacks '$want_body': $body"; return ;; esac ;;
  esac
  pass "$name"
}

# The app's metrics under the spellings found to reach them, or that the issue names: the app's routing ignores case
# and a trailing slash and resolves dot segments and percent-encoding itself, and sent the client's target raw it parsed
# an encoded slash and a '#' differently from nginx. The stub answers 200 to every path, so each must be the proxy's own 404
# page, never the stub. Each is sent as the request target exactly as written (--request-target), which curl would
# otherwise rewrite. Usage: expect_metrics_refused LABEL [curl options...]
expect_metrics_refused() {
  label="$1"; shift
  for spelling in '/metrics' '/METRICS' '/Metrics' '/metrics/' '/METRICS/' '//metrics' '/metrics/.' '/metrics/./' \
                  '/./metrics' '/x/../METRICS' '/metrics//..' '/metrics/x/..' '/%4detrics' '/%6d%65%74%72%69%63%73' \
                  '/%2e/metrics' '/x/%2e%2e/metrics' '/metrics/%2e' '/%2Fmetrics' '/metrics?x=1' '/metrics;x=1' '/metrics.' \
                  '/metrics/x%2F../..' '/metrics/x%2f../..' '/METRICS/x%2F%2E%2E/%2E%2E' '/metrics/a%2F%2E%2E/..' \
                  '/metrics/a/b%2F../../..' '/api/../metrics/x%2F../..' '/metrics/x%2F../..?a=1' '/metrics/x%5C../..' \
                  '/metrics/x%5c%2e%2e/..' '/metrics/x%252F../..' '/x/%2e%2e/metrics/y%2F%2e%2e/%2e%2e' \
                  '/x#/../metrics' '/#/../metrics' '/x#/../METRICS' '/api#/../metrics' '/x/#/../../metrics' \
                  '/health#/../metrics' '/health?#/../metrics' '/metrics#'; do
    expect_request "$label: $spelling is the proxy's 404" 404 '404 Not Found' / --request-target "$spelling" "$@"
  done
  expect_request "$label: /metrics/x%2F../.. over HTTP/1.0 is the proxy's 404" 404 '404 Not Found' \
    / --request-target '/metrics/x%2F../..' --http1.0 "$@"
  expect_request "$label: /metrics/x%2F../.. in absolute form is the proxy's 404" 404 '404 Not Found' \
    / --request-target 'http://apiary-keeper.example.test/metrics/x%2F../..' "$@"
  expect_request "$label: /METRICS/ in absolute form is the proxy's 404" 404 '404 Not Found' \
    / --request-target 'http://apiary-keeper.example.test/METRICS/' "$@"
  expect_request "$label: /health#/../metrics over HTTP/1.0 is the proxy's 404" 404 '404 Not Found' \
    / --request-target '/health#/../metrics' --http1.0 "$@"
  expect_request "$label: /health#/../metrics in absolute form is the proxy's 404" 404 '404 Not Found' \
    / --request-target 'http://apiary-keeper.example.test/health#/../metrics' "$@"
}

# The staging gate's one open path, /health, used to reach the rest of the app: sent raw, /health#/../ was /health to
# nginx (open) and / to the app, a bypass of the gate closed here. A target holding '#' is the proxy's
# 404, in every mode, and never reaches the app. Usage: expect_health_hash_refused LABEL [curl options...]
expect_health_hash_refused() {
  label="$1"; shift
  for target in '/health#/../' '/health#/../api/yards' '/health#/x' '/HEALTH#/../'; do
    expect_request "$label: $target is the proxy's 404, never the app" 404 '404 Not Found' / --request-target "$target" "$@"
  done
  expect_request "$label: /health#/../ over HTTP/1.0 is the proxy's 404" 404 '404 Not Found' \
    / --request-target '/health#/../' --http1.0 "$@"
  expect_request "$label: /health#/../ in absolute form is the proxy's 404" 404 '404 Not Found' \
    / --request-target 'http://apiary-keeper.example.test/health#/../' "$@"
}

if start_proxy "$gate_on" "$good_user" "$good_password"; then
  expect_request "gate on: an anonymous request to / is refused, with no app content" 401 '!stub-app' /
  expect_request "gate on: an anonymous request to /api/* is refused"                 401 '!stub-app' /api/yards
  expect_request "gate on: an anonymous request to any other path is refused"         401 '!stub-app' /any/other/path
  request / -D -
  case "$body" in
    *'WWW-Authenticate: Basic realm="Apiary-Keeper"'*) pass "gate on: the refusal asks for basic authentication" ;;
    *) fail "gate on: the refusal asks for basic authentication" "no WWW-Authenticate: $body" ;;
  esac
  expect_request "gate on: valid credentials pass through with the app's response, Authorization kept from it" \
    200 'stub-app authorization=[]' / -u "$user:$password"
  expect_request "gate on: valid credentials reach /api/*" 200 'stub-app' /api/yards -u "$user:$password"
  expect_request "gate on: a wrong password is refused"    401 '!stub-app' / -u "$user:${password}x"
  expect_request "gate on: an unknown user is refused"     401 '!stub-app' / -u "someone-else:$password"
  expect_request "gate on: /health is open"                200 'stub-app' /health
  expect_request "gate on: /health/x is not open"          401 '!stub-app' /health/x
  expect_request "gate on: /HEALTH is not open (only /health, exactly)" 401 '!stub-app' /HEALTH
  expect_metrics_refused "gate on, anonymous"
  expect_health_hash_refused "gate on, anonymous"
  # Out of /health by dot segments is resolved before the gate: the gate asks, and nothing reaches the app.
  for target in '/health/../api/yards' '/health/%2e%2e/api/yards' '/health;/../api/yards' '/health/x/../../' \
                '/health/..' '/health/x/..'; do
    expect_request "gate on, anonymous: $target is the gate's 401, never the app" 401 '!stub-app' / --request-target "$target"
  done
  expect_request "gate on, anonymous: /x/../health is the open /health, and the app is sent /health" \
    200 'target=[/health]' / --request-target '/x/../health'
  expect_health_hash_refused "gate on, valid credentials" -u "$user:$password"
  expect_request "gate on, valid credentials: /health/../api/yards reaches the app as /api/yards" \
    200 'target=[/api/yards]' / --request-target '/health/../api/yards' -u "$user:$password"
  expect_metrics_refused "gate on, valid credentials" -u "$user:$password"
  expect_request "gate on: an encoded slash in the query string still reaches the app" \
    200 'stub-app' '/api/yards?cursor=a%2Fb' -u "$user:$password"
  metrics="$(curl -s http://127.0.0.1:9113/metrics)"
  case "$metrics" in
    *nginx_connections_active*) pass "gate on: the metrics port answers the scrape as before" ;;
    *) fail "gate on: the metrics port answers the scrape as before" "$metrics" ;;
  esac
  stop_proxy
  encoded="$(printf '%s' "$user:$password" | base64 | tr -d '\n')"
  name="gate on: the logs never hold the password"
  if grep -qF "$password" "$log" || grep -qF "$encoded" "$log"; then fail "$name" "$(cat "$log")"
  elif ! grep -q '"GET / HTTP/1.1" 401' "$log"; then fail "$name" "no access line was logged: $(cat "$log")"
  else pass "$name"; fi
  name="gate on: the access log holds the path as sent, without its query string"
  if ! grep -qF '"GET /metrics/x%2F../.. HTTP/1.1" 404' "$log"; then fail "$name" "no raw path logged: $(cat "$log")"
  elif grep -qF 'cursor=' "$log"; then fail "$name" "the query string was logged: $(grep -F 'cursor=' "$log")"
  else pass "$name"; fi
else
  fail "gate on: nginx starts" "$(cat "$log")"
fi

if start_proxy; then
  expect_request "gate off: an anonymous request passes through" 200 'stub-app' /
  expect_request "gate off: an encoded slash in the query string still reaches the app" 200 'stub-app' '/?next=%2Fyards'
  expect_metrics_refused "gate off"
  expect_health_hash_refused "gate off"
  expect_request "gate off: the app is sent the path nginx resolved, re-encoded, and the query string as sent" \
    200 'target=[/a/cA%20d%3F?x=%2F&y=1+2]' / --request-target '/a/./b/../c%41%20d%3F?x=%2F&y=1+2'
  stop_proxy
else
  fail "gate off: nginx starts" "$(cat "$log")"
fi
rm -f "$stub" "$log"

if [ "$cases" -ne "$expected_cases" ]; then
  echo "FAIL ran $cases cases, expected $expected_cases"
  exit 1
fi
if [ "$failed" -ne 0 ]; then
  echo "$failed of $cases proxy configuration tests failed"
  exit 1
fi
rm -f "$rendered" "$htpasswd"
echo "all $cases proxy configuration tests passed"
