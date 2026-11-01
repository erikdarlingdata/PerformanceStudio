#!/usr/bin/env bash
# Runs the Core tests as three test-host processes at once, from the one CI job.
#
# Why processes and not threads: the headless UI tests share a single dispatcher thread
# (tests/PlanViewer.Core.Tests/HeadlessUi.cs), so more threads in one process do not help.
# Each process gets its own dispatcher. Measured locally with this split: one process
# ~129s on CI, three processes ~57s.
#
# The split is by class-name initial. Every class is in PlanViewer.Core.Tests.
#   process 1: classes starting A-N
#   process 2: classes starting O, P, Q, R, U, V, W, X, Y, Z
#   process 3: everything else (filter-not-class for every pattern above), so a new class,
#              a lowercase initial or a new namespace cannot be silently dropped.
#
# Usage: bash .github/scripts/run-core-tests-parallel.sh
# Expects the solution to be built already (dotnet build ... -c Release --no-restore).
set -u

PROJECT="tests/PlanViewer.Core.Tests/PlanViewer.Core.Tests.csproj"
NS="PlanViewer.Core.Tests"
LOG_DIR="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/core-tests-parallel"
rm -rf "$LOG_DIR"
mkdir -p "$LOG_DIR"

GROUP1="A B C D E F G H I J K L M N"
GROUP2="O P Q R U V W X Y Z"

# Echoes the "--<flag> <ns>.<letter>*" arguments for each letter given, one per line.
filters() {
  local flag="$1"; shift
  local letter
  for letter in "$@"; do
    printf '%s\n' "--$flag"
    printf '%s\n' "$NS.$letter*"
  done
}

run_group() {
  local name="$1"; shift
  # remaining args: the filter arguments for this process
  dotnet test "$PROJECT" -c Release --no-build --verbosity normal -- \
    --hangdump --hangdump-timeout 5m --hangdump-type none \
    --max-threads 1 \
    --results-directory "$LOG_DIR/results-$name" \
    "$@" > "$LOG_DIR/$name.log" 2>&1
  echo $? > "$LOG_DIR/$name.exit"
}

mapfile -t F1 < <(filters filter-class $GROUP1)
mapfile -t F2 < <(filters filter-class $GROUP2)
mapfile -t F3 < <(filters filter-not-class $GROUP1 $GROUP2)

run_group "1-A-N" "${F1[@]}" &
PID1=$!
run_group "2-O-Z" "${F2[@]}" &
PID2=$!
run_group "3-rest" "${F3[@]}" &
PID3=$!

wait "$PID1" "$PID2" "$PID3"

status=0
for name in 1-A-N 2-O-Z 3-rest; do
  code="$(cat "$LOG_DIR/$name.exit" 2>/dev/null || echo 'missing')"
  echo "::group::Core tests, process $name (exit code $code)"
  cat "$LOG_DIR/$name.log"
  echo "::endgroup::"
  if [ "$code" != "0" ]; then
    echo "::error::Core tests process $name failed with exit code $code"
    status=1
  fi
done

# One summary line per process, outside the groups, so the totals are visible without expanding.
for name in 1-A-N 2-O-Z 3-rest; do
  echo "process $name: exit $(cat "$LOG_DIR/$name.exit" 2>/dev/null || echo missing); $(grep -E 'Total:|total:' "$LOG_DIR/$name.log" | tail -n 1)"
done

exit "$status"
