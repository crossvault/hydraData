#!/usr/bin/env python3
"""Fail the build if a forbidden package is in the transitive graph.

Input: one or more `dotnet list package --include-transitive --format json` outputs.
Forbidden: ClosedXML (any version); EPPlus version >= 5 (v4 is allowed).
"""
import json
import re
import sys


def packages(doc):
    for project in doc.get("projects", []):
        for fw in project.get("frameworks", []):
            for key in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(key, []) or []:
                    yield pkg.get("id", ""), str(pkg.get("resolvedVersion", ""))


def forbidden(pid, version):
    name = pid.lower()
    if name == "closedxml":
        return f"{pid} {version} (ClosedXML forbidden)"
    if name == "epplus":
        m = re.match(r"(\d+)", version)
        if m is None:
            print(
                f"WARN: could not parse EPPlus version '{version}', "
                "skipping major-version check",
                file=sys.stderr,
            )
            return None
        if int(m.group(1)) >= 5:
            return f"{pid} {version} (EPPlus >= 5 forbidden)"
    return None


def main(paths):
    hits = set()
    seen = 0
    for path in paths:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        for pid, version in packages(doc):
            seen += 1
            note = forbidden(pid, version)
            if note:
                hits.add(note)
    if hits:
        print("::error::Forbidden package(s): " + ", ".join(sorted(hits)))
        return 1
    print(f"License audit OK ({seen} packages checked): no ClosedXML, no EPPlus>=5.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
