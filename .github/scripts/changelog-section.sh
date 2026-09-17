#!/usr/bin/env bash
#
# changelog-section.sh — print one version's section body from the package CHANGELOG.
#
# Usage:
#   .github/scripts/changelog-section.sh 0.2.0    # body of "## [0.2.0] - 2026-09-18"
#   .github/scripts/changelog-section.sh v0.2.0   # a leading "v" is dropped
#
# Prints everything between that heading and the next "## " heading, without the blank
# lines around it. release.yml puts the result under "What's Changed".
#
# Exit code: non-zero when the heading is missing or its section is empty — a release
# note that silently loses its changes is worse than a failed release.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CHANGELOG="${REPO_ROOT}/com.xpTURN.ContentLink/CHANGELOG.md"

VERSION="${1:-}"
if [[ -z "${VERSION}" ]]; then
  echo "usage: $(basename "${BASH_SOURCE[0]}") <version>   (0.2.0 or v0.2.0)" >&2
  exit 2
fi
VERSION="${VERSION#v}"
[[ -f "${CHANGELOG}" ]] || { echo "CHANGELOG.md not found: ${CHANGELOG}" >&2; exit 1; }

if ! grep -qE "^## \[${VERSION//./\\.}\]" "${CHANGELOG}"; then
  echo "CHANGELOG.md has no '## [${VERSION}]' heading" >&2
  exit 1
fi

# Collect the section, then print it back without its leading/trailing blank lines.
# Matched by prefix, not by regex, so "## [0.2.0]" cannot pick up "## [0.2.0-rc1]".
SECTION="$(awk -v head="## [${VERSION}]" '
  !inside {
    if (substr($0, 1, length(head)) == head) inside = 1
    next
  }
  /^## / { exit }
  {
    line[++n] = $0
    if ($0 ~ /[^ \t]/) { if (!first) first = n; last = n }
  }
  END { for (i = first; i <= last; i++) print line[i] }
' "${CHANGELOG}")"

if [[ -z "${SECTION}" ]]; then
  echo "CHANGELOG.md section '## [${VERSION}]' is empty" >&2
  exit 1
fi

printf '%s\n' "${SECTION}"
