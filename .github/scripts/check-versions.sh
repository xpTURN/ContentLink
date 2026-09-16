#!/usr/bin/env bash
#
# check-versions.sh — verify the package version is consistent across the manifests.
#
# Sources of truth compared here:
#   com.xpTURN.ContentLink/package.json   (UPM package version — the one the tag must match)
#   com.xpTURN.ContentLink/CHANGELOG.md   (must have a "## [<version>]" heading, or "## [Unreleased]"
#                                          while nothing has been released)
#
# Usage:
#   .github/scripts/check-versions.sh              # manifests only
#   .github/scripts/check-versions.sh --tag v0.1.0 # also require the tag to match
#
# Exit code: non-zero on any mismatch.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,/^set /p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

PKG_JSON="${REPO_ROOT}/com.xpTURN.ContentLink/package.json"
CHANGELOG="${REPO_ROOT}/com.xpTURN.ContentLink/CHANGELOG.md"
[[ -f "${PKG_JSON}" ]] || { echo "package.json not found: ${PKG_JSON}" >&2; exit 1; }
[[ -f "${CHANGELOG}" ]] || { echo "CHANGELOG.md not found: ${CHANGELOG}" >&2; exit 1; }

# The manifest has to be valid JSON with the three fields UPM requires.
read -r NAME VERSION UNITY < <(python3 - "${PKG_JSON}" <<'PY'
import json, sys
m = json.load(open(sys.argv[1], encoding="utf-8"))
missing = [k for k in ("name", "version", "unity") if not m.get(k)]
if missing:
    print("MISSING " + ",".join(missing) + " -"); sys.exit(0)
print(m["name"], m["version"], m["unity"])
PY
) || { echo "package.json is not valid JSON" >&2; exit 1; }
if [[ "${NAME}" == "MISSING" ]]; then
  echo "package.json lacks required field(s): ${VERSION}" >&2
  exit 1
fi
echo "package.json: ${NAME} ${VERSION} (unity ${UNITY})"

FAILED=0

if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "  version '${VERSION}' is not a semantic version (MAJOR.MINOR.PATCH[-pre])" >&2
  FAILED=1
fi

# The CHANGELOG must carry the version — or still be unreleased.
if grep -qE "^## \[${VERSION//./\\.}\]" "${CHANGELOG}"; then
  echo "  CHANGELOG.md: heading [${VERSION}] found"
elif grep -qE '^## \[Unreleased\]' "${CHANGELOG}"; then
  echo "  CHANGELOG.md: [Unreleased] (nothing released yet)"
else
  echo "  CHANGELOG.md has neither a [${VERSION}] heading nor [Unreleased]" >&2
  FAILED=1
fi

if [[ -n "${TAG}" ]]; then
  if [[ "${TAG}" == "v${VERSION}" ]]; then
    echo "  tag ${TAG} matches package.json"
    if ! grep -qE "^## \[${VERSION//./\\.}\]" "${CHANGELOG}"; then
      echo "  a release tag needs a [${VERSION}] heading in CHANGELOG.md (not just [Unreleased])" >&2
      FAILED=1
    fi
  else
    echo "  tag ${TAG} does not match package.json version ${VERSION} (expected v${VERSION})" >&2
    FAILED=1
  fi
fi

if [[ ${FAILED} -ne 0 ]]; then
  echo "Version check failed." >&2
  exit 1
fi
echo "Version manifests agree."
