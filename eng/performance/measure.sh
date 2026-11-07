#!/usr/bin/env bash
#
# Times FusionModules' analyzers and generators in a solution's build, and the build with and without
# them.
#
#   bash eng/performance/measure.sh <solution or project> [runs] [extra build arguments...]
#
# Every build is --no-incremental and -m:1: the compiler's report per compilation must not
# interleave. The first build only warms the compiler server; a cold one charges the analyzer
# assembly's load and JIT to whichever compilation comes first.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
target="$1"; shift
runs="${1:-3}"; [ $# -gt 0 ] && shift
work="$(mktemp -d)"
native() { if command -v cygpath > /dev/null 2>&1; then cygpath -w "$1"; else printf %s "$1"; fi; }
csc() { grep -E '(мс|ms) +(вызовов|calls)? *[0-9]+ +Csc *$' "$1" | tail -1 | awk '{print $1}'; }
errors() { grep -cE ': error ' "$1" || true; }
calls() { grep -E '(мс|ms) +(вызовов|calls)? *[0-9]+ +Csc *$' "$1" | tail -1 | awk '{print $(NF-1)}'; }
build() { dotnet build "$target" --no-incremental -m:1 -clp:PerformanceSummary "$@" > "$log" 2>&1 || true; }

log="$work/warm.log"; build -v:m "$@"

log="$work/report.log"; build -v:d -p:ReportAnalyzer=true "$@"
echo "== What the compiler charges to each analyzer and generator"
python "$here/analyzer-times.py" "$log"

echo
echo "== Csc time with and without FusionModules' analyzers, ms (calls), alternating"
without="-p:CustomAfterMicrosoftCommonTargets=$(native "$here/WithoutFusionModulesAnalyzers.targets")"
for i in $(seq "$runs"); do
    log="$work/with-$i.log"; build -v:m "$@"; printf 'with:    %s (%s)\n' "$(csc "$log")" "$(calls "$log")"
    log="$work/without-$i.log"; build -v:m "$without" "$@"; printf 'without: %s (%s), errors: %s\n' "$(csc "$log")" "$(calls "$log")" "$(errors "$log")"
done
echo "logs in $work"
