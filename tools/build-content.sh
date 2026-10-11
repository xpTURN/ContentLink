#!/usr/bin/env bash
# Builds a ContentLink content directory from the command line.
#
# The Unity call is fixed here so nobody has to remember it:
#   Unity -batchmode -nographics -quit -projectPath <project> -buildTarget <target> -logFile <log> \
#         -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build -contentlink.<key> <value> ...
#
# Usage:
#   tools/build-content.sh [-p <projectPath>] [-t <buildTarget>] [-l <logFile>] [-c] [-contentlink.<key> <value> ...]
#
#   tools/build-content.sh -contentlink.profile Main
#   tools/build-content.sh -p samples/LinkSample -contentlink.profile all
#   tools/build-content.sh -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.archive Packed
#   tools/build-content.sh -c -contentlink.profile Main      # catalogs only: no build (-executeMethod ContentBuildCLI.UpdateCatalogs)
#
# Keys (after -contentlink.): profile (name | all) · source · output · archive (None | Engine | Packed) · maxArchiveMB
#   · compression (Uncompressed | LZ4 | LZMA) · cleanBuildCache · disableWriteTypeTree · failBuildWhenErrorsLogged
#   (true | false) · postProcessors (comma separated type names; Namespace.Type, Assembly is one) · nameStyle
#   (PathWithoutExtension | PathWithExtension) · contentVersion (a string stamped into every packed index the run
#   builds). Overrides are the run's: the profile is put back after it, and the settings file is never written. With
#   `profile all` no profile override is allowed; contentVersion belongs to the run and is accepted there too. No
#   profile = the active one.
#
# Defaults: project samples/LinkSample (relative to the ContentLink root) · target StandaloneOSX · log Logs/build-content.log
#
# Exit code: Unity's — 0 when the build succeeded, 1 when the build failed or an option was wrong
# (ContentBuildCLI calls EditorApplication.Exit(1)), 2 when this script refused to start.
#
# The project must not be open in another editor instance: batch mode needs the project lock and fails
# without a clear message otherwise, so the lock file is checked first.
#
# Environment variables:
#   UNITY_PATH        Override the Unity executable path (default: Hub 6000.6.0f1)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

UNITY_VERSION="6000.6.0f1"
UNITY_PATH="${UNITY_PATH:-/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity}"
PROJECT="${REPO_ROOT}/samples/LinkSample"
TARGET="StandaloneOSX"
LOG="${REPO_ROOT}/Logs/build-content.log"
METHOD="Build"
CL_ARGS=()

usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "${BASH_SOURCE[0]}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    -p|-t|-l)
      [[ $# -ge 2 && -n "$2" ]] || { echo "$1 needs a value" >&2; exit 2; }
      case "$2" in -p|-t|-l|-c|-h|--help|-contentlink.*) echo "$1 needs a value, not the option $2" >&2; exit 2 ;; esac
      case "$1" in -p) PROJECT="$2" ;; -t) TARGET="$2" ;; -l) LOG="$2" ;; esac
      shift 2 ;;
    -c) METHOD="UpdateCatalogs"; shift ;;
    -h|--help) usage; exit 0 ;;
    -contentlink.*)
      [[ $# -ge 2 ]] || { echo "$1 needs a value" >&2; exit 2; }
      case "$2" in -p|-t|-l|-c|-h|--help|-contentlink.*) echo "$1 needs a value, not the option $2" >&2; exit 2 ;; esac
      CL_ARGS+=("$1" "$2"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -d "${PROJECT}" ]] || PROJECT="${REPO_ROOT}/${PROJECT}"
PROJECT="$(cd "${PROJECT}" 2>/dev/null && pwd)" || { echo "project folder not found" >&2; exit 2; }
[[ -f "${PROJECT}/ProjectSettings/ProjectVersion.txt" ]] || { echo "not a Unity project: ${PROJECT}" >&2; exit 2; }

if [[ ! -x "${UNITY_PATH}" ]]; then
  echo "Unity executable not found: ${UNITY_PATH}" >&2
  echo "  Set the UNITY_PATH environment variable to point at it." >&2
  exit 2
fi

if [[ -f "${PROJECT}/Temp/UnityLockfile" ]]; then
  echo "refusing to start: ${PROJECT} is open in another editor instance (Temp/UnityLockfile exists)." >&2
  echo "  Batch mode needs the project lock. Close the editor, or delete the stale lock file after a crash." >&2
  exit 2
fi

mkdir -p "$(dirname "${LOG}")"
echo "▶ ContentLink ${METHOD} · project ${PROJECT} · target ${TARGET} · ${#CL_ARGS[@]} option value(s)"
echo "  log: ${LOG}"

# ${CL_ARGS[@]+…} below: bash < 4.4 calls an empty array unbound under set -u
"${UNITY_PATH}" \
  -batchmode \
  -nographics \
  -quit \
  -projectPath "${PROJECT}" \
  -buildTarget "${TARGET}" \
  -logFile "${LOG}" \
  -executeMethod "xpTURN.ContentLink.Editor.ContentBuildCLI.${METHOD}" \
  ${CL_ARGS[@]+"${CL_ARGS[@]}"}
rc=$?

# The runner's and the entry point's own lines are the useful part of a long editor log.
[[ -f "${LOG}" ]] && grep -h "\[ContentLink" < "${LOG}" | sed 's/^/  /' | tail -n 20   # a regular file only: `-l -` logs to stdout
if [[ ${rc} -eq 0 ]]; then
  echo "✔ ${METHOD} done (exit 0)"
else
  echo "✗ failed (exit ${rc}) — see ${LOG}"
fi
exit ${rc}
