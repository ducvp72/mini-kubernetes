#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${OPEN_API_URL:-http://127.0.0.1:8080}"
ORDER_HEALTH_URL="${ORDER_HEALTH_URL:-http://127.0.0.1:8081}"
WEBHOOK_HEALTH_URL="${WEBHOOK_HEALTH_URL:-http://127.0.0.1:8082}"
CALLBACK_URL="${CALLBACK_URL:-http://mock-webhook:8090/callback}"
KAFKA_UI_URL="${KAFKA_UI_URL:-http://127.0.0.1:8085}"

echo "==> 1. Checking health & readiness probes"
curl -fsS "${BASE_URL}/healthz" >/dev/null
curl -fsS "${BASE_URL}/readyz" >/dev/null
curl -fsS "${ORDER_HEALTH_URL}/healthz" >/dev/null
curl -fsS "${ORDER_HEALTH_URL}/readyz" >/dev/null
curl -fsS "${WEBHOOK_HEALTH_URL}/healthz" >/dev/null
curl -fsS "${WEBHOOK_HEALTH_URL}/readyz" >/dev/null
KAFKA_UI_HTTP=$(curl -sS -o /dev/null -w "%{http_code}" "${KAFKA_UI_URL}")
if [ "${KAFKA_UI_HTTP}" != "200" ]; then
  echo "FAIL: Kafka UI at ${KAFKA_UI_URL} returned HTTP ${KAFKA_UI_HTTP} (expected 200)"
  exit 1
fi
echo "OK: All 3 services healthy & ready, and Kafka UI (${KAFKA_UI_URL}) responded HTTP 200."

IDEM_KEY="smoke-$(date +%s)-$$"
CUSTOMER_ID="cust-smoke-$$"

echo "==> 2. Creating order via POST /api/v1/orders (expect 202 Accepted)"
CREATE_HTTP=$(curl -sS -o /tmp/smoke_create.json -w "%{http_code}" \
  -X POST "${BASE_URL}/api/v1/orders" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: ${IDEM_KEY}" \
  -d "{\"customerId\":\"${CUSTOMER_ID}\",\"sku\":\"PROMO-FLASH-01\",\"quantity\":2,\"unitPrice\":49.99,\"callbackUrl\":\"${CALLBACK_URL}\",\"simulateWorkMs\":10}")

if [ "${CREATE_HTTP}" != "202" ]; then
  echo "FAIL: expected 202, got ${CREATE_HTTP}: $(cat /tmp/smoke_create.json)"
  exit 1
fi

ORDER_CODE=$(python3 -c 'import json; d=json.load(open("/tmp/smoke_create.json")); print(d["orderCode"])')
echo "OK: Created orderCode=${ORDER_CODE}"

echo "==> 3. Testing idempotency duplicate submission"
DUP_HTTP=$(curl -sS -o /tmp/smoke_dup.json -w "%{http_code}" \
  -X POST "${BASE_URL}/api/v1/orders" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: ${IDEM_KEY}" \
  -d "{\"customerId\":\"${CUSTOMER_ID}\",\"sku\":\"PROMO-FLASH-01\",\"quantity\":2,\"unitPrice\":49.99,\"callbackUrl\":\"${CALLBACK_URL}\",\"simulateWorkMs\":10}")

if [ "${DUP_HTTP}" != "202" ]; then
  echo "FAIL: duplicate request returned ${DUP_HTTP}"
  exit 1
fi

python3 - "${ORDER_CODE}" <<'PY'
import json, sys
expected = sys.argv[1]
data = json.load(open("/tmp/smoke_dup.json"))
assert data["orderCode"] == expected, f"Expected {expected}, got {data}"
assert data["deduplicated"] is True, f"Expected deduplicated=True, got {data}"
print("OK: Idempotency verified (same orderCode, deduplicated=true).")
PY

echo "==> 4. Polling GET /api/v1/orders/${ORDER_CODE} until CONFIRMED (SQL Server -> Outbox -> Redpanda -> Consumer -> Go Webhook)"
CONFIRMED=0
for i in $(seq 1 30); do
  curl -fsS "${BASE_URL}/api/v1/orders/${ORDER_CODE}" -o /tmp/smoke_get.json
  STATUS=$(python3 -c 'import json; print(json.load(open("/tmp/smoke_get.json"))["status"])')
  if [ "${STATUS}" = "CONFIRMED" ]; then
    CONFIRMED=1
    echo "OK: Order ${ORDER_CODE} reached CONFIRMED on attempt ${i}."
    break
  fi
  sleep 1
done

if [ "${CONFIRMED}" -ne 1 ]; then
  echo "FAIL: Order did not reach CONFIRMED in time: $(cat /tmp/smoke_get.json)"
  exit 1
fi

echo "==> 5. Creating 3 extra orders and testing bidirectional cursor pagination (next & prev)"
for n in 1 2 3; do
  curl -fsS -X POST "${BASE_URL}/api/v1/orders" \
    -H "Content-Type: application/json" \
    -H "Idempotency-Key: ${IDEM_KEY}-extra-${n}" \
    -d "{\"customerId\":\"${CUSTOMER_ID}\",\"sku\":\"PROMO-FLASH-0${n}\",\"quantity\":${n},\"unitPrice\":19.50,\"callbackUrl\":\"${CALLBACK_URL}\"}" >/dev/null
done

curl -fsS "${BASE_URL}/api/v1/orders?customerId=${CUSTOMER_ID}&pageSize=2" -o /tmp/smoke_page1.json
NEXT_CURSOR=$(python3 -c 'import json; d=json.load(open("/tmp/smoke_page1.json")); assert len(d["items"])==2 and d["hasNext"] is True; print(d["nextCursor"])')

curl -fsS "${BASE_URL}/api/v1/orders?customerId=${CUSTOMER_ID}&pageSize=2&nextCursor=${NEXT_CURSOR}" -o /tmp/smoke_page2.json
PREV_CURSOR=$(python3 -c 'import json; d=json.load(open("/tmp/smoke_page2.json")); assert len(d["items"])==2 and d["hasPrev"] is True; print(d["prevCursor"])')

curl -fsS "${BASE_URL}/api/v1/orders?customerId=${CUSTOMER_ID}&pageSize=2&prevCursor=${PREV_CURSOR}" -o /tmp/smoke_page1_back.json
python3 <<'PY'
import json
p1 = [x["orderCode"] for x in json.load(open("/tmp/smoke_page1.json"))["items"]]
p2 = [x["orderCode"] for x in json.load(open("/tmp/smoke_page2.json"))["items"]]
p1_back = [x["orderCode"] for x in json.load(open("/tmp/smoke_page1_back.json"))["items"]]
assert set(p1).isdisjoint(set(p2)), f"Overlap between page 1 {p1} and page 2 {p2}"
assert p1 == p1_back, f"Backward cursor mismatch: expected {p1}, got {p1_back}"
print("OK: Bidirectional cursor pagination verified (page1 -> nextCursor -> page2 -> prevCursor -> exact page1).")
PY

echo "==> 6. Verifying webhook-service delivered HTTP callback to mock-webhook"
if docker logs mini-k8s-mock-webhook 2>&1 | grep -q "${ORDER_CODE}"; then
  echo "OK: mock-webhook received callback for ${ORDER_CODE}."
else
  echo "WARN: mock-webhook log did not contain ${ORDER_CODE}; checking webhook-service logs..."
  docker logs --tail 20 mini-k8s-webhook-service
  exit 1
fi

echo "=== ALL PHASE 1 E2E SMOKE CHECKS PASSED ==="
