#!/usr/bin/env bash
# Checks backups end to end on a running stack (docker compose --profile gateway up -d --build --wait) whose
# BACKUP_AGE_RECIPIENTS holds the public half of the key in BACKUP_TEST_KEY (a throwaway key file). With
# SMOKE_MERCHANT_KEY set, it first creates two paid orders, so the backup holds ledger entries and payment attempts:
# one paid through the sandbox API, one paid the way a customer does on the hosted page (a typed test card and the
# bank's one-time password), through the HTTPS proxy and the real database.
# CI runs it after deploy/smoke-test.sh.
set -euo pipefail
cd "$(dirname "$0")/../.."

key_file="${BACKUP_TEST_KEY:?Set BACKUP_TEST_KEY to the private key file for BACKUP_AGE_RECIPIENTS}"
compose=(docker compose --profile gateway)
db="$(sed -n 's/^POSTGRES_DB=//p' .env)"
db_user="$(sed -n 's/^POSTGRES_USER=//p' .env)"
backups="${BACKUP_DIR:-./backups}"
work="$(mktemp -d)"
trap 'rm -rf -- "${work:?}"' EXIT

failed() { echo "FAILED: $*" >&2; exit 1; }
passed() { echo "ok: $*"; }
sql() { "${compose[@]}" exec -T postgres psql -qXAt -U "$db_user" -d "$1" -c "$2"; }
restore() { "${compose[@]}" run --rm -T -v "$(realpath "$2"):/run/secrets/backup-key:ro" backup gateway-restore "$1" "${3:-}" 2>&1; }

if [[ -n "${SMOKE_MERCHANT_KEY:-}" ]]; then
  "${compose[@]}" cp proxy:/data/caddy/pki/authorities/local/root.crt "$work/root.crt" >/dev/null 2>&1
  api() { curl --silent --show-error --fail --cacert "$work/root.crt" -H "Authorization: Bearer $SMOKE_MERCHANT_KEY" -H 'Content-Type: application/json' "$@"; }
  api -X POST https://localhost/v1/settlement/wallets --data '{"network":"ethereum","address":"0x1111111111111111111111111111111111111111"}' >/dev/null || true
  new_order() {
    local quote
    quote="$(api -X POST https://localhost/v1/quotes --data '{"sourceCurrency":"INR","sourceAmount":"10000.00","destinationAsset":"USDC@ethereum"}' | sed -n 's/.*"quoteId":"\([^"]*\)".*/\1/p')"
    api -X POST https://localhost/v1/orders -H "Idempotency-Key: backup-test-$1-$(date +%s)" --data "{\"quoteId\":\"$quote\",\"customerId\":\"cust_verified\"}"
  }
  # Let an order finish, so nothing changes in the database between the backup and the comparison.
  finish_order() {
    local status=""
    for _ in $(seq 1 60); do
      status="$(api "https://localhost/v1/orders/$1" | python3 -c 'import json, sys; print(json.load(sys.stdin)["status"])')"
      [[ "$status" == Completed ]] && break
      if [[ "$status" == CryptoSent ]]; then api -X POST "https://localhost/v1/sandbox/orders/$1/confirm" >/dev/null || true; fi
      sleep 1
    done
    [[ "$status" == Completed ]] || failed "the test order $1 did not complete (it is $status)"
  }

  order="$(new_order sandbox | sed -n 's/.*"orderId":"\([^"]*\)".*/\1/p')"
  api -X POST "https://localhost/v1/sandbox/orders/$order/pay" >/dev/null
  finish_order "$order"
  passed "created, paid and completed order $order"

  # A customer pays the second order on the hosted page: a typed test card, then the bank's one-time password.
  page() { curl --silent --show-error --cacert "$work/root.crt" "$@"; }
  created="$(new_order customer)"
  order="$(sed -n 's/.*"orderId":"\([^"]*\)".*/\1/p' <<<"$created")"
  checkout="$(sed -n 's/.*"checkoutUrl":"\([^"]*\)".*/\1/p' <<<"$created")"
  [[ "$checkout" == https://localhost/checkout/* ]] || failed "the order's checkout link is $checkout"
  grep -q 'name="card_number"' <<<"$(page "$checkout")" || failed "the checkout page has no card form"
  expiry="12/$(printf '%02d' $(( ($(date +%Y) + 2) % 100 )))"
  location="$(page --output /dev/null --write-out '%{redirect_url}' -X POST "$checkout/pay" \
    --data-urlencode method=card --data-urlencode 'card_number=4242 4242 4242 4242' --data-urlencode "card_expiry=$expiry" --data-urlencode card_code=737)"
  [[ "$location" == "$checkout" ]] || failed "paying by card was answered with: $location"
  html="$(page "$checkout")"
  grep -q 'Enter the code from your bank' <<<"$html" || failed "the checkout page did not ask for the one-time password"
  # The page may name the card by its last four digits, and the sandbox help lists the published test numbers; nothing else.
  shown="$(python3 -c 'import re, sys; print(re.sub(r"<details class=\"sandbox-help\">.*?</details>", "", sys.stdin.read(), flags=re.S))' <<<"$html")"
  ! grep -qE '4242 ?4242' <<<"$shown" || failed "the page shows more of the card number than its last four digits"
  attempt="$(sed -n 's/.*name="attempt" value="\([0-9a-f-]*\)".*/\1/p' <<<"$html" | head -n 1)"
  [[ -n "$attempt" ]] || failed "the page has no attempt to answer"
  location="$(page --output /dev/null --write-out '%{redirect_url}' -X POST "$checkout/authenticate" --data-urlencode "attempt=$attempt" --data-urlencode code=123456)"
  [[ "$location" == "$checkout" ]] || failed "the one-time password was answered with: $location"
  grep -q 'Payment received' <<<"$(page "$checkout")" || failed "the checkout page does not show the payment as received"
  finish_order "$order"
  passed "a customer paid order $order on the hosted page with a typed card and a one-time password, through the proxy"
fi

[[ "$(docker inspect --format '{{.State.Health.Status}}' "$("${compose[@]}" ps -q backup)")" == healthy ]] || failed "the backup container is not healthy"
"${compose[@]}" exec -T backup gateway-backup > "$work/backup.log" 2>&1 || { cat "$work/backup.log"; failed "a backup run failed"; }
grep -q 'restore check passed' "$work/backup.log" || { cat "$work/backup.log"; failed "the backup was not checked by restoring it"; }
newest="$("${compose[@]}" exec -T backup sh -c 'ls -t /backups/gateway-*.dump.age | head -n 1')"
newest="$(basename "$newest")"
passed "backed up and restore-checked: $newest"
[[ "$(sql "$db" "SELECT count(*) FROM ops.backup_run WHERE succeeded AND file = '$newest'")" == 1 ]] || failed "the backup was not recorded in the database"
passed "the backup is recorded in the database, where alerts watch it"

header="$("${compose[@]}" exec -T backup head -c 21 "/backups/$newest")"
[[ "$header" == "age-encryption.org/v1" ]] || failed "the backup is not age-encrypted"
if "${compose[@]}" exec -T backup grep -q PGDMP "/backups/$newest"; then failed "the backup contains an unencrypted dump"; fi
passed "the backup file is encrypted"

restore "$newest" "$key_file" gateway_restore_test > "$work/restore.log" || { cat "$work/restore.log"; failed "restoring with the right key failed"; }
grep -q 'restore check passed' "$work/restore.log" || { cat "$work/restore.log"; failed "the restored database was not checked"; }
for table in ledger.journal_entry ledger.journal_line gateway.order_event gateway.payment_order gateway.payment_attempt gateway.payment_attempt_event; do
  live="$(sql "$db" "SELECT count(*) FROM $table")"
  copy="$(sql gateway_restore_test "SELECT count(*) FROM $table")"
  [[ "$copy" == "$live" ]] || failed "$table: $copy rows restored, $live in the live database"
done
if [[ -n "${SMOKE_MERCHANT_KEY:-}" ]]; then
  [[ "$(sql gateway_restore_test 'SELECT count(*) FROM ledger.journal_entry')" -gt 0 ]] || failed "the restored ledger is empty"
  # The test order was paid through the gateway's own processor, so its payment attempt and history must be in the copy.
  [[ "$(sql gateway_restore_test 'SELECT count(*) FROM gateway.payment_attempt')" -gt 0 ]] || failed "the restored payment attempts are empty"
fi
sql postgres 'DROP DATABASE gateway_restore_test' >/dev/null
passed "restored with the private key into a new database that matches the live one"

"${compose[@]}" run --rm --no-deps -T backup age-keygen > "$work/wrong-key.txt" 2>/dev/null
if restore "$newest" "$work/wrong-key.txt" gateway_restore_wrong_key > "$work/wrong.log"; then failed "another key opened the backup"; fi
grep -q 'could not decrypt' "$work/wrong.log" || { cat "$work/wrong.log"; failed "a wrong key was not reported as such"; }
passed "no other key opens the backup"

"${compose[@]}" exec -T backup sh -c "cp /backups/$newest /backups/damaged-test.dump.age && printf 'x' | dd of=/backups/damaged-test.dump.age bs=1 seek=400 conv=notrunc 2>/dev/null"
if restore damaged-test.dump.age "$key_file" gateway_restore_damaged > "$work/damaged.log"; then failed "a damaged backup was restored"; fi
"${compose[@]}" exec -T backup rm -f /backups/damaged-test.dump.age
passed "a damaged backup is refused"

if restore "$newest" "$key_file" "$db" > "$work/live.log"; then failed "a restore overwrote the live database"; fi
grep -q 'refusing to restore over the live database' "$work/live.log" || { cat "$work/live.log"; failed "restoring over the live database was not refused clearly"; }
passed "the live database can never be the restore target"

if [[ -n "$(sed -n 's/^BACKUP_COPY_TO=//p' .env)" ]]; then
  "${compose[@]}" exec -T backup test -f "/backups/offsite-copy/$newest" || failed "the backup was not copied off-site"
  passed "the backup was copied to the off-site destination"
fi

echo "All backup checks passed."
