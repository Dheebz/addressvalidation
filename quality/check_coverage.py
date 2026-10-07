#!/usr/bin/env python3
"""Enforce business-code coverage floors from one dotnet test collection run."""

import argparse
import sys
import xml.etree.ElementTree as et
from pathlib import Path

# Coverage reports are emitted separately for unit and integration test projects.
# The strongest report for each assembly is a conservative floor for their union.
MINIMUMS = {
    "Address.Core": (90.0, 60.0),
    "Address.Data": (80.0, 60.0),
}


def main():
    """Fail when a collected business assembly misses a coverage floor."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "results",
        type=Path,
        help="Directory containing coverage.cobertura.xml attachments",
    )
    args = parser.parse_args()
    reports = list(args.results.glob("**/coverage.cobertura.xml"))
    if not reports:
        parser.error("no Cobertura reports found")

    best = {}
    for report in reports:
        root = et.parse(report).getroot()
        for package in root.findall(".//package"):
            name = package.attrib["name"]
            if name not in MINIMUMS:
                continue
            rates = (
                float(package.attrib["line-rate"]) * 100,
                float(package.attrib["branch-rate"]) * 100,
            )
            previous = best.get(name, (0.0, 0.0))
            best[name] = tuple(max(a, b) for a, b in zip(previous, rates))

    failures = []
    for name, (min_lines, min_branches) in MINIMUMS.items():
        lines, branches = best.get(name, (0.0, 0.0))
        print(
            f"{name}: lines {lines:.1f}% (min {min_lines:.0f}%), "
            f"branches {branches:.1f}% (min {min_branches:.0f}%)"
        )
        if lines < min_lines or branches < min_branches:
            failures.append(name)
    if failures:
        print("Coverage floor failed: " + ", ".join(failures), file=sys.stderr)
        return 1
    print("Coverage floors passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
