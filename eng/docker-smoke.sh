#!/usr/bin/env bash
#
# Brings up samples/03-modular-data and asserts the claim the whole approach rests on: three
# services, one image, and the difference between them is an environment variable.
#
# What makes this worth a script rather than a paragraph is the negative half. That the monolith
# serves everything proves little; that the customers replica returns 404 for /billing/overdue is
# what says the topology is real and not just a label.
#
# Usage: eng/docker-smoke.sh

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose="$root/samples/03-modular-data/docker-compose.yml"
failures=0

log()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
pass() { printf '  \033[32mok\033[0m   %s\n' "$*"; }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }

# The teardown must not become the exit code. An EXIT trap whose last command succeeds will
# otherwise report success for a run that died on the build — which is precisely the kind of
# green-and-wrong this repository exists to remove.
cleanup() {
    local status=$?

    if [ "$status" -ne 0 ] || [ "$failures" -ne 0 ]; then
        log "Logs"
        docker compose -f "$compose" logs --tail 80 || true
    fi

    docker compose -f "$compose" down -v --remove-orphans > /dev/null 2>&1 || true
    exit "$status"
}
trap cleanup EXIT

log "Building and starting"
docker compose -f "$compose" up --build -d

# The monolith owns the schema and the other two wait on it, so readiness is not the same moment
# for all three. Poll rather than sleep: the first migration is the slow part and how slow depends
# on the machine.
await() {
    local url="$1"
    for _ in $(seq 1 60); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)" = "200" ]; then return 0; fi
        sleep 2
    done
    return 1
}

log "Waiting for the three services"
for port in 5010 5011 5012; do
    if await "http://localhost:$port/"; then pass "localhost:$port answers"; else fail "localhost:$port never answered"; fi
done

# Only the monolith migrates; the other two take the database as they find it. So an answer on /
# means the process is up, and an answer on a data endpoint means the schema is there — which is
# what the followers are about to be asked for.
if await http://localhost:5010/customers; then pass "the schema is in place"; else fail "the monolith never finished migrating"; fi

# Every topology runs the same binaries; what differs is which modules were named at startup.
check() {
    local name="$1" url="$2" expected="$3"
    local actual
    actual="$(curl -s -o /dev/null -w '%{http_code}' "$url" || true)"
    if [ "$actual" = "$expected" ]; then pass "$name"; else fail "$name — got $actual, expected $expected"; fi
}

log "Each topology serves its own modules and no others"
check "monolith serves /customers"            http://localhost:5010/customers      200
check "monolith serves /billing/overdue"      http://localhost:5010/billing/overdue 200

check "customers serves /customers"           http://localhost:5011/customers      200
check "customers does not serve /billing"     http://localhost:5011/billing/overdue 404

check "billing serves /billing/overdue"       http://localhost:5012/billing/overdue 200
check "billing does not serve /customers"     http://localhost:5012/customers      404

log "Result"
if [ "$failures" -eq 0 ]; then
    echo "  all checks passed"
else
    echo "  $failures check(s) failed"
    exit 1
fi
