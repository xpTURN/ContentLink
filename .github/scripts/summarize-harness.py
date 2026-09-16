#!/usr/bin/env python3
"""summarize-harness.py — turn the verification harness log into CI-visible output.

Job logs need repository admin rights to download, so a bare "exit code 1" tells
nobody which check broke. This prints a per-log table, writes it to the job
summary, and emits one ::error:: annotation per failed check (annotations are
readable from the run page and the public checks API).

The harness (com.xpTURN.ContentLink/Tests~/Verify) is a console program: one
"  PASS <check>" / "  FAIL <check>" line per check and a final "ALL PASS" or
"<n> FAIL" line. No final line means the build failed or the program crashed.

Usage: summarize-harness.py <results-dir>
Always exits 0 — the run step owns the job's pass/fail verdict.
"""
import glob
import os
import re
import sys


def main():
    results_dir = sys.argv[1] if len(sys.argv) > 1 else "Logs/test-results"
    paths = sorted(glob.glob(os.path.join(results_dir, "harness-*.log")))
    if not paths:
        print(f"No harness log under {results_dir}")
        print("::error::No harness log was produced — the build or the run itself failed before any check ran.")
        return 0

    rows = []       # (suite, passed, failed, note)
    failures = []   # (suite, check)
    for path in paths:
        suite = os.path.basename(path)[len("harness-"):-len(".log")]
        text = open(path, encoding="utf-8", errors="replace").read()
        passed = len(re.findall(r"^  PASS ", text, re.M))
        failed_names = [m.strip() for m in re.findall(r"^  FAIL (.*)$", text, re.M)]
        final = re.search(r"^(ALL PASS|(\d+) FAIL)$", text, re.M)
        if final is None:
            rows.append((suite, passed, len(failed_names), "no final line — build error or crash"))
            errors = re.findall(r"^.*error CS\d+.*$", text, re.M)
            for e in errors[:10]:
                print(f"::error::{suite}: {e.strip()}")
            if not errors:
                print(f"::error::{suite}: the harness produced no final result line (build error or crash)")
        else:
            rows.append((suite, passed, len(failed_names), final.group(1)))
        for name in failed_names:
            failures.append((suite, name))
            print(f"::error::{suite}: {name}")

    lines = ["| Harness | Passed | Failed | Result |", "|---|---:|---:|---|"]
    for suite, p, f, note in rows:
        lines.append(f"| {suite} | {p} | {f} | {note} |")
    if failures:
        lines.append("")
        lines.append("**Failed checks**")
        for suite, name in failures:
            lines.append(f"- `{suite}`: {name}")
    table = "\n".join(lines)
    print(table)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write("## Verification harness\n\n" + table + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
