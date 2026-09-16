#!/usr/bin/env bash
#
# check-asset-sidecars.sh — every file Unity imports must carry its .meta sidecar.
#
# Unity refuses to see a .cs without a .cs.meta: the generated csproj simply omits the file, so
# the type it declares stops existing and the assembly fails to compile. An asset without its
# .meta has no GUID and is not an asset at all. Nothing in the dotnet build notices, because
# dotnet does not read .meta. This script is the check that catches it before Unity does.
#
# Scope: the git-tracked files of the package (com.xpTURN.ContentLink — a UPM package needs a
# .meta for every file and folder except the hidden '*~' folders) and of the two Unity
# projects' Assets folders. Tracked files only, so local build output and Library/ never count.
#
# Usage: .github/scripts/check-asset-sidecars.sh
# Exit code: non-zero if any tracked Unity file is missing its .meta.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

ROOTS=(
  "com.xpTURN.ContentLink"
  "tests/LinkTests/Assets"
  "samples/LinkSample/Assets"
)

missing=""
checked=0
for root in "${ROOTS[@]}"; do
  [[ -e "${root}" ]] || continue
  while IFS= read -r f; do
    [[ -n "${f}" ]] || continue
    case "${f}" in
      *.meta) continue ;;                       # the sidecar itself
      */.gitkeep|*/.DS_Store) continue ;;       # never imported
    esac
    # Anything inside a hidden folder (name ending in '~') is not imported and needs no .meta.
    case "/${f}/" in */*~/*) continue ;; esac
    checked=$((checked + 1))
    git ls-files --error-unmatch "${f}.meta" >/dev/null 2>&1 || missing+="${f}"$'\n'
    # every folder on the way needs a .meta too (a folder without one is not imported)
    dir="$(dirname "${f}")"
    while [[ "${dir}" != "${root}" && "${dir}" != "." && "${dir}" == "${root}"/* ]]; do
      case "${dir}" in *~) break ;; esac
      git ls-files --error-unmatch "${dir}.meta" >/dev/null 2>&1 || missing+="${dir}/"$'\n'
      dir="$(dirname "${dir}")"
    done
  done < <(git ls-files -- "${root}")
done

missing="$(printf '%s' "${missing}" | sort -u)"
if [[ -n "${missing}" ]]; then
  echo "Unity .meta missing — these tracked files/folders will be INVISIBLE to Unity:" >&2
  echo "${missing}" | sed 's/^/  /' >&2
  echo >&2
  echo "A .cs without its .cs.meta is left out of the generated csproj, so the types it declares" >&2
  echo "vanish and the assembly stops compiling; any other file without one is not an asset." >&2
  echo "The dotnet harness will not catch this." >&2
  exit 1
fi

echo "Unity sidecars are complete: ${checked} tracked file(s) under ${ROOTS[*]} carry their .meta."
