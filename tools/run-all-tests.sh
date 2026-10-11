#!/usr/bin/env bash
#
# run-all-tests.sh — ContentLink integrated test runner
#
# Runs the package's dotnet verification harness (Tests~/Verify, `dotnet run`
# against API stubs and a fake engine) and the Unity Test Runner suites of
# tests/LinkTests (EditMode, then PlayMode in the editor; optionally PlayMode in
# a standalone player) from a single entry point, then prints a final console
# summary report.
#
# The harness is not a `dotnet test` project: it is a console program that
# prints one "  PASS <name>" / "  FAIL <name>" line per check and ends with
# "ALL PASS" or "<n> FAIL", so its console output is parsed.
#
# EditMode runs first on purpose: its TestContentBuildTests.S0 builds the test
# content directories (StreamingAssets/ContentLinkTests/{A,B,C}) that every
# PlayMode fixture's prebuild hook only validates. The Unity project must not be
# open in another editor instance (batch mode needs the project lock).
#
# Usage:
#   tools/run-all-tests.sh                 # dotnet harness + Unity EditMode + Unity PlayMode (editor)
#   tools/run-all-tests.sh --dotnet-only   # dotnet harness only
#   tools/run-all-tests.sh --unity-only    # Unity suites only
#   tools/run-all-tests.sh --editmode-only # Unity EditMode only (builds the test content, runs the builder tests)
#   tools/run-all-tests.sh --no-playmode   # skip the editor PlayMode run
#   tools/run-all-tests.sh --player        # also run PlayMode in a StandaloneOSX player (builds a player: minutes)
#   tools/run-all-tests.sh -h | --help
#
# One -only option at most. --dotnet-only and --editmode-only stand alone; --unity-only, like no -only option,
# takes --no-playmode and --player. Any other mix is refused (exit 2), whatever the order.
#
# Environment variables:
#   UNITY_PATH        Override the Unity executable path (default: Hub 6000.6.0f1)
#
# Exit code: non-zero if anything fails (CI friendly). A suite that ran no tests fails, and so does a fixture whose
# one-time teardown failed, an inconclusive test, or a skipped one that is not on the expected list (EXPECTED_SKIPS in
# the summary below).

set -uo pipefail

# ── Path resolution (derive the ContentLink root from the script's own location) ──
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Intermediate artifacts go under the gitignored Logs/ directory.
RESULTS_DIR="${REPO_ROOT}/Logs/test-results"

# Unity project & version (tests/LinkTests/ProjectSettings/ProjectVersion.txt)
UNITY_VERSION="6000.6.0f1"
UNITY_PROJECT="${REPO_ROOT}/tests/LinkTests"
UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity}"

# dotnet verification harness (console program, see header)
HARNESS_PROJECT="${REPO_ROOT}/com.xpTURN.ContentLink/Tests~/Verify"

# ── Option parsing ───────────────────────────────────────────────────────────
RUN_DOTNET=1
RUN_EDITMODE=1
RUN_PLAYMODE=1
RUN_PLAYER=0
ONLY=""        # the -only option given, if any
ONLY2=""       # a second one: refused
MODIFIER=""    # --no-playmode or --player, if given

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "${BASH_SOURCE[0]}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dotnet-only|--unity-only|--editmode-only)
      if [[ -z "${ONLY}" ]]; then ONLY="$1"; elif [[ "$1" != "${ONLY}" ]]; then ONLY2="$1"; fi ;;
    --no-playmode)   RUN_PLAYMODE=0; MODIFIER="$1" ;;
    --player)        RUN_PLAYER=1; MODIFIER="$1" ;;
    -h|--help)       usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 2 ;;
  esac
  shift
done
# Applied after the loop, so the order of the options does not matter.
[[ -z "${ONLY2}" ]] || { echo "${ONLY} and ${ONLY2} cannot be combined" >&2; usage; exit 2; }
if [[ -n "${MODIFIER}" && ( "${ONLY}" == --dotnet-only || "${ONLY}" == --editmode-only ) ]]; then
  echo "${ONLY} and ${MODIFIER} cannot be combined" >&2; usage; exit 2
fi
case "${ONLY}" in
  --dotnet-only)   RUN_EDITMODE=0; RUN_PLAYMODE=0; RUN_PLAYER=0 ;;
  --unity-only)    RUN_DOTNET=0 ;;
  --editmode-only) RUN_DOTNET=0; RUN_PLAYMODE=0; RUN_PLAYER=0 ;;
esac

# ── Colors (only when writing to a tty) ──────────────────────────────────────
if [[ -t 1 ]]; then
  C_RED=$'\033[31m'; C_GRN=$'\033[32m'; C_YEL=$'\033[33m'; C_CYN=$'\033[36m'; C_BLD=$'\033[1m'; C_RST=$'\033[0m'
else
  C_RED=""; C_GRN=""; C_YEL=""; C_CYN=""; C_BLD=""; C_RST=""
fi

section() { echo; echo "${C_BLD}${C_CYN}=== $* ===${C_RST}"; }

mkdir -p "${RESULTS_DIR}"
# Clean up results from the previous run, logs included (an editor log grows to hundreds of MB and a
# renamed suite must not leave a stale result behind for the summary to pick up).
rm -f "${RESULTS_DIR}"/*.xml "${RESULTS_DIR}"/*.log 2>/dev/null || true

# ── dotnet verification harness (dotnet run) ─────────────────────────────────
# One log; the summary parses the PASS/FAIL lines. The program always exits 0, so the parsed final
# line ("ALL PASS" / "<n> FAIL") is authoritative, and a build error shows up as "no result lines".
run_dotnet_harness() {
  section "dotnet verification harness (Tests~/Verify, dotnet run)"
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "${C_RED}dotnet not found. The .NET SDK must be installed.${C_RST}" >&2
    return 1
  fi
  local log="${RESULTS_DIR}/harness-verify.log"
  echo "${C_BLD}▶ Tests~/Verify${C_RST}  (log: ${log})"
  # The project targets net8.0; roll forward so a newer installed SDK/runtime runs it.
  # stdin from /dev/null: a foreground `dotnet run` can otherwise wait on the terminal.
  DOTNET_ROLL_FORWARD=Major dotnet run --project "${HARNESS_PROJECT}" -v q < /dev/null 2>&1 | tee "${log}"
  local rc=${PIPESTATUS[0]}
  grep -q '^ALL PASS$' "${log}" || rc=1
  return ${rc}
}

# ── Unity Test Runner ────────────────────────────────────────────────────────
# run_unity <label> <testPlatform> <slug>
# The slug names both artifacts (unity-<slug>.xml / .log); the .xml name is what the summary parser
# globs, so it must stay unique per run.
run_unity() {
  local label="$1" platform="$2" slug="$3"

  if [[ ! -x "${UNITY_PATH}" ]]; then
    echo "${C_RED}Unity executable not found:${C_RST} ${UNITY_PATH}" >&2
    echo "  Set the UNITY_PATH environment variable to point at it." >&2
    return 1
  fi

  local results_xml="${RESULTS_DIR}/unity-${slug}.xml"
  local unity_log="${RESULTS_DIR}/unity-${slug}.log"

  echo "${C_BLD}▶ ${label}${C_RST}  (log: ${unity_log})"
  # -runTests handles batch mode and exit automatically (do not combine with -quit).
  # Unity returns an exit code based on the test result (0=pass, 2=test failure, etc.).
  # -buildTarget StandaloneOSX: the content directories are built per target and must match the
  # editor's active target for Play Mode to load them.
  "${UNITY_PATH}" \
    -runTests \
    -batchmode \
    -nographics \
    -projectPath "${UNITY_PROJECT}" \
    -testPlatform "${platform}" \
    -testResults "${results_xml}" \
    -logFile "${unity_log}" \
    -buildTarget StandaloneOSX
  local rc=$?

  if [[ ! -f "${results_xml}" ]]; then
    echo "${C_RED}Unity result file was not produced. Check the log:${C_RST} ${unity_log}" >&2
    # Print the last log lines to help diagnose licence / compile / project-lock errors
    tail -n 20 "${unity_log}" 2>/dev/null
    return 1
  fi
  return ${rc}
}

run_unity_tests() {
  section "Unity Test Runner (tests/LinkTests, ${UNITY_VERSION}, batch mode)"
  local overall=0

  if [[ "${RUN_EDITMODE}" -eq 1 ]]; then
    run_unity "EditMode (builder tests + test content build)" EditMode "linktests-editmode" || overall=1
  else
    echo "${C_YEL}▶ EditMode — skipped${C_RST}"
  fi

  if [[ "${RUN_PLAYMODE}" -eq 1 ]]; then
    run_unity "PlayMode (editor)" PlayMode "linktests-playmode" || overall=1
  else
    echo "${C_YEL}▶ PlayMode (editor) — skipped${C_RST}"
  fi

  if [[ "${RUN_PLAYER}" -eq 1 ]]; then
    run_unity "PlayMode (StandaloneOSX player)" StandaloneOSX "linktests-player" || overall=1
  fi

  return ${overall}
}

# ── Final summary report (parses harness log + Unity NUnit XML) ──────────────
print_summary() {
  section "Final report"
  # Which suites this invocation was supposed to produce. Without it the summary cannot tell
  # "the suite passed" from "the suite left no file". The labels below have to match the ones the
  # parser derives from file names.
  local expected=()
  [[ "${RUN_DOTNET}"   -eq 1 ]] && expected+=("Harness: verify")
  [[ "${RUN_EDITMODE}" -eq 1 ]] && expected+=("Unity: linktests-editmode")
  [[ "${RUN_PLAYMODE}" -eq 1 ]] && expected+=("Unity: linktests-playmode")
  [[ "${RUN_PLAYER}"   -eq 1 ]] && expected+=("Unity: linktests-player")

  EXPECTED_SUITES="$(printf '%s\n' ${expected[@]+"${expected[@]}"})" \
  RESULTS_DIR="${RESULTS_DIR}" DOTNET_RC="${DOTNET_RC}" UNITY_RC="${UNITY_RC}" python3 - <<'PY'
import glob, os, re, sys
import xml.etree.ElementTree as ET

results_dir = os.environ["RESULTS_DIR"]

# Colors (only when stdout is a tty)
tty = sys.stdout.isatty()
RED = "\033[31m" if tty else ""
GRN = "\033[32m" if tty else ""
YEL = "\033[33m" if tty else ""
BLD = "\033[1m"  if tty else ""
RST = "\033[0m"  if tty else ""

rows = []          # (suite, total, passed, failed, skipped)
failures = []      # (suite, test_fullname)
unexpected = []    # (suite, test_fullname, result, reason): skipped or inconclusive, not on EXPECTED_SKIPS
teardown = []      # (suite, fixture_fullname, message): a one-time teardown that failed

# Tests a suite skips on purpose — each says why in its Ignore message. Any other skipped test, and any inconclusive
# one, fails the run: a broken condition or a missing fixture makes tests skip, and a skipped test proves nothing.
EXPECTED_SKIPS = {
    "Unity: linktests-playmode": {   # the editor cannot show these; the player run checks them
        "xpTURN.ContentLink.Tests.AtlasSpriteTests.AS18_TheAtlasName_LoadsTheSpriteOnThePage",
        "xpTURN.ContentLink.Tests.ResourcesSourceTests.T22_Release_UnloadsInAPlayer_SameObject_SamePath_TheSwitch",
        "xpTURN.ContentLink.Tests.ResourcesSourceTests.T23_APrefab_LeavesWithTheSweep",
        "xpTURN.ContentLink.Tests.ResourcesSourceTests.T28_ASweepKeepsADeferral_AndTheSwitchOffLeavesItToTheSweep",
    },
    "Unity: linktests-player": {     # editor-only fixtures leave one ignored test each in a player
        "xpTURN.ContentLink.Tests.SimulationProbeTests.EditorOnly",
        "xpTURN.ContentLink.Tests.SimulationProjectTruthTests.EditorOnly",
        "xpTURN.ContentLink.Tests.SimulationTests.EditorOnly",
        "xpTURN.ContentLink.Tests.SimulationTruthProbeTests.EditorOnly",
    },
}

# Suites the runner said it would produce. Empty when nothing was asked for.
expected = [x.strip() for x in os.environ.get("EXPECTED_SUITES", "").split("\n") if x.strip()]

# --- dotnet harness (console log) ---
# One "  PASS <check>" / "  FAIL <check>" line per check, and a final "ALL PASS" or "<n> FAIL" line.
# The final line is the gate: a build error or a crash mid-run prints no final line at all.
for path in sorted(glob.glob(os.path.join(results_dir, "harness-*.log"))):
    suite = "Harness: " + os.path.basename(path)[len("harness-"):-len(".log")]
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except Exception as e:
        rows.append((suite, 0, 0, 0, 0)); failures.append((suite, f"<parse failed: {e}>")); continue
    passed = len(re.findall(r"^  PASS ", text, re.M))
    failed_names = re.findall(r"^  FAIL (.*)$", text, re.M)
    final = re.search(r"^(ALL PASS|(\d+) FAIL)$", text, re.M)
    if final is None:
        rows.append((suite, passed + len(failed_names), passed, len(failed_names), 0))
        failures.append((suite, "<parse failed: no final result line — build error or the harness crashed>"))
        continue
    failed = len(failed_names) if final.group(1) == "ALL PASS" else int(final.group(2))
    rows.append((suite, passed + failed, passed, failed, 0))
    for name in failed_names:
        failures.append((suite, name.strip()))

# --- Unity NUnit3 XML ---
for path in sorted(glob.glob(os.path.join(results_dir, "unity-*.xml"))):
    suite = "Unity: " + os.path.basename(path)[len("unity-"):-len(".xml")]
    try:
        root = ET.parse(path).getroot()   # <test-run ...>
    except Exception as e:
        rows.append((suite, 0, 0, 0, 0)); failures.append((suite, f"<parse failed: {e}>")); continue
    total   = int(root.get("total", root.get("testcasecount", 0)))
    passed  = int(root.get("passed", 0))
    failed  = int(root.get("failed", 0))
    skipped = int(root.get("skipped", 0)) + int(root.get("inconclusive", 0))
    rows.append((suite, total, passed, failed, skipped))
    for tc in root.iter("test-case"):
        if tc.get("result") == "Failed":
            failures.append((suite, tc.get("fullname", tc.get("name", "?"))))
        elif tc.get("result") in ("Skipped", "Inconclusive"):
            name = tc.get("fullname", tc.get("name", "?"))
            if tc.get("result") == "Inconclusive" or name not in EXPECTED_SKIPS.get(suite, ()):   # the list takes skips only
                unexpected.append((suite, name, tc.get("result"), (tc.findtext("reason/message") or "").strip()))
    # A fixture whose one-time teardown failed (a leak check) says so on its own suite only: its tests passed, the root
    # counts and the editor's exit code (0) miss it.
    for ts in root.iter("test-suite"):
        if ts.get("result") == "Failed" and ts.get("site") == "TearDown":
            teardown.append((suite, ts.get("fullname", ts.get("name", "?")), (ts.findtext("failure/message") or "").strip()))

if not rows:
    # Exiting 0 here would call a run that produced nothing a success.
    if expected:
        print(f"{RED}{BLD}No test results were collected, but {len(expected)} suite(s) were expected:{RST}")
        for name in expected:
            print(f"  {RED}✗{RST} {name}")
        sys.exit(1)
    print(f"{YEL}No test results were collected.{RST}")
    sys.exit(0)

real_failures = [x for x in failures if not x[1].startswith("<parse")]
parse_errors  = [x for x in failures if x[1].startswith("<parse")]
# A suite that ran no tests — none passed, none failed: no test at all (an assembly left out by a define constraint,
# a filter that matches nothing, a harness that printed its final line and no check) or skips alone. A suite whose
# result did not parse is reported there instead.
empty = [r[0] for r in rows if r[2] == 0 and r[3] == 0 and r[0] not in {x[0] for x in parse_errors}]
bad = set(empty) | {x[0] for x in parse_errors + unexpected + teardown}   # rows that fail the gate besides their FAIL count

# Print the table
name_w = max(len(r[0]) for r in rows + [("SUITE", 0, 0, 0, 0)])
def fmt(s, t, p, f, k):
    return f"  {s:<{name_w}}  {t:>6}  {p:>6}  {f:>6}  {k:>7}"
print(f"{BLD}{fmt('SUITE', 'TOTAL', 'PASS', 'FAIL', 'SKIP')}{RST}")
print("  " + "-" * (name_w + 31))
tot = pas = fai = ski = 0
for s, t, p, f, k in rows:
    tot += t; pas += p; fai += f; ski += k
    color = RED if f > 0 or s in bad else GRN
    print(f"{color}{fmt(s, t, p, f, k)}{RST}")
print("  " + "-" * (name_w + 31))
print(f"{BLD}{fmt('TOTAL', tot, pas, fai, ski)}{RST}")

# Suites that were expected but left no result file. A compile error, a licence failure, a locked
# project or a hung editor all land here, and none of them produce a row of their own.
found = {r[0] for r in rows}
missing = [name for name in expected if name not in found]
if missing:
    print()
    print(f"{RED}{BLD}Missing suites ({len(missing)}) — expected but produced no result file:{RST}")
    for name in missing:
        print(f"  {RED}✗{RST} {name}")

# Failure list
if real_failures:
    print()
    print(f"{RED}{BLD}Failed tests ({len(real_failures)}):{RST}")
    for suite, name in real_failures:
        print(f"  {RED}✗{RST} [{suite}] {name}")
if empty:
    print()
    print(f"{RED}{BLD}Suites that ran no tests ({len(empty)}):{RST}")
    for name in empty:
        print(f"  {RED}✗{RST} {name}")
if teardown:
    print()
    print(f"{RED}{BLD}Fixtures whose one-time teardown failed ({len(teardown)}):{RST}")
    for suite, name, msg in teardown:
        print(f"  {RED}✗{RST} [{suite}] {name} — {msg}")
if unexpected:
    print()
    print(f"{RED}{BLD}Inconclusive, or skipped and not on EXPECTED_SKIPS ({len(unexpected)}):{RST}")
    for suite, name, result, reason in unexpected:
        print(f"  {RED}✗{RST} [{suite}] {name} — {result}: {reason}")
if parse_errors:
    print()
    print(f"{YEL}Result parse warnings:{RST}")
    for suite, msg in parse_errors:
        print(f"  {YEL}![{suite}] {msg}{RST}")

# A runner that exited non-zero although nothing failed in its results (a crash after the results were written, an
# editor error): the script exits 1 for it, so the last line must not say all passed.
dotnet_rc, unity_rc = os.environ.get("DOTNET_RC", "0"), os.environ.get("UNITY_RC", "0")   # the harness's exit code · a flag for the Unity runs
runners = ([f"dotnet exited {dotnet_rc}"] if dotnet_rc not in ("", "0") else []) + (["a Unity run exited non-zero"] if unity_rc not in ("", "0") else [])
inconclusive = sum(1 for x in unexpected if x[2] == "Inconclusive")
reasons = fai or parse_errors or missing or empty or unexpected or teardown

print()
if not reasons and not runners:
    print(f"{GRN}{BLD}✔ All passed ({pas}/{tot}){RST}")
    sys.exit(0)
else:
    suffix = f", {len(missing)} suite(s) missing" if missing else ""
    suffix += f", {len(empty)} suite(s) ran no tests" if empty else ""
    suffix += f", {len(parse_errors)} unreadable result(s)" if parse_errors else ""
    suffix += f", {len(teardown)} fixture teardown failure(s)" if teardown else ""
    suffix += f", {inconclusive} inconclusive" if inconclusive else ""
    suffix += f", {len(unexpected) - inconclusive} unexpected skip(s)" if len(unexpected) > inconclusive else ""
    suffix += f", but {' · '.join(runners)} with nothing failed in its results — see its log" if not reasons else ""
    print(f"{RED}{BLD}✗ {fai} failed / {tot} total{suffix}{RST}")
    sys.exit(1)
PY
}

# ── Run ──────────────────────────────────────────────────────────────────────
DOTNET_RC=0
UNITY_RC=0

[[ "${RUN_DOTNET}" -eq 1 ]] && { run_dotnet_harness; DOTNET_RC=$?; }
if [[ "${RUN_EDITMODE}" -eq 1 || "${RUN_PLAYMODE}" -eq 1 || "${RUN_PLAYER}" -eq 1 ]]; then
  run_unity_tests; UNITY_RC=$?
fi

print_summary
SUMMARY_RC=$?

echo
echo "Result files: ${RESULTS_DIR}"

# Exit code: non-zero if any run or the summary failed
if [[ ${DOTNET_RC} -ne 0 || ${UNITY_RC} -ne 0 || ${SUMMARY_RC} -ne 0 ]]; then
  exit 1
fi
exit 0
