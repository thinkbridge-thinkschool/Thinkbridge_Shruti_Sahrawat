#!/usr/bin/env python3
"""Merge Cobertura reports from several test projects and enforce a threshold.

Why merge at all
----------------
Coverage was previously gated per test project, and that is not a number any
of them can pass honestly:

  * Quotes.Tests.Unit never boots the app, so QuotesApi/Program.cs -- 150-odd
    lines of DI, Serilog, OpenTelemetry and Polly wiring -- is dead weight in
    its report no matter how many unit tests get written.
  * Quotes.Tests.Integration boots the app but deliberately owns none of the
    pure domain assertions, which live in the unit suite.
  * Every line either suite covers is a line the codebase has a test for.

So the question "is this code tested" is only meaningful across the whole
suite. This script takes the union: a line is covered if ANY test project
executed it.

Resolving filenames
-------------------
Cobertura stores a <sources> root and writes each class's `filename` relative
to it. Coverlet does not pick the same root for every run: a report whose
classes all live under one project can end up rooted at that project directory,
so the same file appears as "Program.cs" in one report and
"QuotesApi/Program.cs" in another.

Ignoring <sources> and keying on the raw filename therefore fails to merge
them, and the failure is silent and flattering-in-reverse: the file shows up
twice, once with real coverage and once at zero, and the zero copy drags the
total down. The first run of this script reported 29.96% with `Program.cs`
listed three times and `EndpointExtensions.cs` twice at 0% covered -- while the
integration suite was demonstrably exercising every endpoint in it.

So: join each filename onto the source root, normalise separators and case, and
key on that.

Usage
-----
    check-coverage.py <threshold> <cobertura.xml> [<cobertura.xml> ...]

Exits 1 if merged line coverage is below the threshold.
"""

from __future__ import annotations

import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def _norm(path: str) -> str:
    """Separator- and case-insensitive form, so Windows paths compare equal."""
    return path.replace("\\", "/").rstrip("/").lower()


def _is_absolute(path: str) -> bool:
    # Unix absolute, or a Windows drive letter such as C:/...
    return path.startswith("/") or (len(path) > 1 and path[1] == ":")


def load(path: str) -> dict[str, dict[int, int]]:
    """Return {resolved_filename: {line_number: hits}} for one Cobertura report."""
    hits_by_file: dict[str, dict[int, int]] = defaultdict(dict)
    root = ET.parse(path).getroot()

    sources = [s.text.strip() for s in root.iter("source") if s.text and s.text.strip()]

    for cls in root.iter("class"):
        filename = cls.get("filename") or cls.get("name") or "<unknown>"
        filename = _norm(filename)

        if not _is_absolute(filename) and sources:
            filename = _norm(_norm(sources[0]) + "/" + filename)

        for line in cls.iter("line"):
            number = int(line.get("number", "0"))
            hits = int(line.get("hits", "0"))
            existing = hits_by_file[filename].get(number, 0)
            hits_by_file[filename][number] = max(existing, hits)

    return hits_by_file


def shorten(paths: list[str]) -> dict[str, str]:
    """Strip the longest common directory prefix, for readable output."""
    if not paths:
        return {}
    split = [p.split("/") for p in paths]
    common = 0
    shortest = min(len(s) for s in split)
    while common < shortest - 1 and len({s[common] for s in split}) == 1:
        common += 1
    return {p: "/".join(s[common:]) for p, s in zip(paths, split)}


def main(argv: list[str]) -> int:
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2

    threshold = float(argv[1])
    reports = argv[2:]

    merged: dict[str, dict[int, int]] = defaultdict(dict)
    for path in reports:
        try:
            for filename, lines in load(path).items():
                for number, hits in lines.items():
                    existing = merged[filename].get(number, 0)
                    merged[filename][number] = max(existing, hits)
        except (OSError, ET.ParseError) as exc:
            print(f"::error::Could not read coverage report {path}: {exc}")
            return 2

    if not merged:
        print("::error::No coverage data found in any report")
        return 2

    total = sum(len(lines) for lines in merged.values())
    covered = sum(
        sum(1 for hits in lines.values() if hits > 0) for lines in merged.values()
    )
    rate = (covered / total * 100) if total else 0.0

    print(f"Reports merged:  {len(reports)}")
    print(f"Files:           {len(merged)}")
    print(f"Lines covered:   {covered} / {total}")
    print(f"Line coverage:   {rate:.2f}%   (threshold {threshold:.0f}%)")
    print()

    labels = shorten(sorted(merged))

    gaps = []
    for filename, lines in merged.items():
        file_total = len(lines)
        file_covered = sum(1 for hits in lines.values() if hits > 0)
        missing = file_total - file_covered
        if missing:
            gaps.append((missing, file_covered, file_total, labels[filename]))

    if gaps:
        gaps.sort(reverse=True)
        print("Still uncovered, worst first:")
        print(f"  {'missing':>7}  {'covered':>7}  {'total':>5}  file")
        for missing, file_covered, file_total, label in gaps[:25]:
            print(f"  {missing:>7}  {file_covered:>7}  {file_total:>5}  {label}")
        if len(gaps) > 25:
            print(f"  ... and {len(gaps) - 25} more files with gaps")
        print()

    if rate < threshold:
        print(
            f"::error::Merged line coverage {rate:.2f}% is below the "
            f"{threshold:.0f}% threshold"
        )
        return 1

    print(f"Coverage gate passed: {rate:.2f}% >= {threshold:.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
