#!/usr/bin/env bash
# Checks the container stack from the outside, through the HTTPS proxy. Start it first:
#   docker compose --profile gateway up -d --build --wait
# Then run deploy/smoke-test.sh from anywhere. It needs curl, and the stack running with a local certificate
# (GATEWAY_TLS=internal, the default). It makes failed sign-in attempts, so it pauses nothing but its own address.
set -euo pipefail
cd "$(dirname "$0")/.."

domain="${GATEWAY_DOMAIN:-localhost}"
https_port="${GATEWAY_HTTPS_PORT:-443}"
http_port="${GATEWAY_HTTP_PORT:-80}"
base="https://${domain}:${https_port}"
compose=(docker compose --profile gateway)
work="$(mktemp -d)"
trap 'rm -rf -- "${work:?}"' EXIT

failed() { echo "FAILED: $*" >&2; exit 1; }
passed() { echo "ok: $*"; }

# Caddy's local certificate authority signed the certificate; trust it for these requests only. Right after the
# stack starts, Caddy may still be making it.
for _ in $(seq 1 30); do
  "${compose[@]}" cp proxy:/data/caddy/pki/authorities/local/root.crt "$work/root.crt" >/dev/null 2>&1 && break
  sleep 1
done
[[ -s "$work/root.crt" ]] || failed "the proxy has no local certificate authority (is GATEWAY_TLS=internal?)"
https() { curl --silent --show-error --cacert "$work/root.crt" --resolve "${domain}:${https_port}:127.0.0.1" "$@"; }
for _ in $(seq 1 30); do
  https --output /dev/null "$base/health" 2>/dev/null && break
  sleep 1
done

status=$(https --output "$work/environment.json" --write-out '%{http_code}' "$base/v1/environment")
[[ "$status" == 200 ]] || failed "GET /v1/environment over HTTPS answered $status"
grep -q '"mode":"Sandbox"' "$work/environment.json" || failed "the API is not in sandbox mode"
grep -q '"realFundsMovement":false' "$work/environment.json" || failed "the API allows real money"
passed "HTTPS with a certificate that verifies; the API is in sandbox mode"

redirect=$(curl --silent --output /dev/null --write-out '%{http_code} %{redirect_url}' \
  --resolve "${domain}:${http_port}:127.0.0.1" "http://${domain}:${http_port}/health")
[[ "$redirect" == 308\ https://* ]] || failed "plain HTTP was not redirected to HTTPS: $redirect"
passed "plain HTTP is redirected to HTTPS"

https --dump-header "$work/headers.txt" --output /dev/null "$base/review"
grep -qi '^strict-transport-security:' "$work/headers.txt" || failed "no Strict-Transport-Security header"
grep -qi "^content-security-policy:.*frame-ancestors 'none'" "$work/headers.txt" || failed "the console lost its content security policy"
if grep -qi '^server:' "$work/headers.txt"; then failed "the proxy names its software"; fi
grep -q '^HTTP/[0-9.]* 200' "$work/headers.txt" || failed "the console did not answer from this machine"
passed "security headers are set, and the console answers from a private network"

# Behind the proxy, each client has its own sign-in limit: this machine uses up its attempts, then a client inside
# the proxy's container (another address) still gets an ordinary answer, not 429.
attempt='{"username":"smoke-test","password":"not a real password","code":"000000"}'
codes=""
for _ in $(seq 1 11); do
  codes+="$(https --output /dev/null --write-out '%{http_code}' -H 'Content-Type: application/json' --data "$attempt" "$base/v1/review/sign-in") "
done
[[ "$codes" == "401 401 401 401 401 401 401 401 401 401 429 " ]] || failed "sign-in attempts from this machine answered: $codes"
other=$("${compose[@]}" exec -T proxy wget -q -O /dev/null --no-check-certificate --header 'Content-Type: application/json' \
  --post-data "$attempt" https://localhost/v1/review/sign-in 2>&1 || true)
[[ "$other" == *"401"* ]] || failed "another client was not given its own limit: $other"
passed "sign-in attempts are limited per client, by the address the proxy saw"

# The console and review API answer only from GATEWAY_REVIEW_FROM: with a network that excludes this machine,
# they disappear while the rest of the API stays up.
GATEWAY_REVIEW_FROM=192.0.2.0/24 "${compose[@]}" up -d --wait proxy >/dev/null 2>&1
for path in /review /v1/review/queue /v1/review/sign-in; do
  status=$(https --output /dev/null --write-out '%{http_code}' "$base$path")
  [[ "$status" == 404 ]] || { "${compose[@]}" up -d --wait proxy >/dev/null 2>&1; failed "$path answered $status from outside GATEWAY_REVIEW_FROM"; }
done
status=$(https --output /dev/null --write-out '%{http_code}' "$base/health")
"${compose[@]}" up -d --wait proxy >/dev/null 2>&1
[[ "$status" == 200 ]] || failed "the API was unreachable while the review paths were closed"
passed "review paths answer only from GATEWAY_REVIEW_FROM"

echo "All container checks passed."
