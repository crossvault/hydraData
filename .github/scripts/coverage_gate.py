#!/usr/bin/env python3
"""Fail the build if coverage is below the gate (T08.5): Line >= 90%, Branch >= 80%.

Reads a ReportGenerator TextSummary (Summary.txt). Usage: coverage_gate.py <Summary.txt>
"""
import re
import sys

LINE_MIN = 90.0
BRANCH_MIN = 80.0


def pct(text, label):
    m = re.search(rf"{label} coverage:\s*([0-9.]+)%", text)
    if not m:
        print(f"::error::could not find '{label} coverage' in summary")
        sys.exit(2)
    return float(m.group(1))


def main(path):
    with open(path, encoding="utf-8-sig") as fh:
        text = fh.read()
    line = pct(text, "Line")
    branch = pct(text, "Branch")
    ok = line >= LINE_MIN and branch >= BRANCH_MIN
    print(f"Line: {line}% (gate {LINE_MIN}%), Branch: {branch}% (gate {BRANCH_MIN}%)")
    if not ok:
        print(f"::error::Coverage below gate (Line>={LINE_MIN}% / Branch>={BRANCH_MIN}%).")
        return 1
    print("Coverage gate passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
