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

Reporting per layer (--by-project)
----------------------------------
Day 31 asked for coverage "at each layer", and one merged percentage cannot
answer that. A modular monolith at 85% could be a domain at 98% and a
composition root at 20%, or the reverse, and those are different codebases with
different risks. --by-project groups the merged lines by the project directory
each file sits in and prints one row per project, so the shape of the number is
visible rather than only its total.

It changes output only. The gate is still the merged figure, deliberately:
a per-project threshold would fail a project the moment it is created and
before anyone could have written a test for it, which teaches people to write
a token test rather than a useful one.

Usage
-----
    check-coverage.py [--by-project] <threshold> <cobertura.xml> [...]

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


def load(path: str) -> tuple[dict[str, dict[int, int]], dict[str, str]]:
    """Return ({resolved_filename: {line: hits}}, {resolved_filename: display}).

    The second map keeps the filename as the report spelled it, because the
    first one is lower-cased for keying and "capstone.curation.domain" is a
    poor thing to print at somebody who has to read the table.
    """
    hits_by_file: dict[str, dict[int, int]] = defaultdict(dict)
    display: dict[str, str] = {}
    root = ET.parse(path).getroot()

    sources = [s.text.strip() for s in root.iter("source") if s.text and s.text.strip()]
    source_root = sources[0] if sources else ""

    for cls in root.iter("class"):
        raw = cls.get("filename") or cls.get("name") or "<unknown>"
        filename = _norm(raw)

        spelled = raw.replace("\\", "/")

        if not _is_absolute(filename) and sources:
            filename = _norm(_norm(source_root) + "/" + filename)
            spelled = source_root.replace("\\", "/").rstrip("/") + "/" + spelled

        display.setdefault(filename, spelled)

        for line in cls.iter("line"):
            number = int(line.get("number", "0"))
            hits = int(line.get("hits", "0"))
            existing = hits_by_file[filename].get(number, 0)
            hits_by_file[filename][number] = max(existing, hits)

    return hits_by_file, display


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


def project_of(display_path: str) -> str:
    """The project a source file belongs to, read off its path.

    The last directory whose name contains a dot, which is the .NET convention
    this repository follows everywhere: Capstone.Curation.Domain, Capstone.Api,
    Quotes.Messaging. A project without a dot in its name -- QuotesApi -- falls
    back to the directory holding the file.
    """
    parts = [p for p in display_path.split("/") if p]
    directories = parts[:-1]

    for segment in reversed(directories):
        if "." in segment:
            return segment

    return directories[-1] if directories else "(root)"


def main(argv: list[str]) -> int:
    args = list(argv[1:])
    by_project = "--by-project" in args
    args = [a for a in args if a != "--by-project"]

    if len(args) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    threshold = float(args[0])
    reports = args[1:]

    merged: dict[str, dict[int, int]] = defaultdict(dict)
    display: dict[str, str] = {}

    for path in reports:
        try:
            hits_by_file, spelled = load(path)
        except (OSError, ET.ParseError) as exc:
            print(f"::error::Could not read coverage report {path}: {exc}")
            return 2

        for filename, lines in hits_by_file.items():
            for number, hits in lines.items():
                existing = merged[filename].get(number, 0)
                merged[filename][number] = max(existing, hits)

        for filename, name in spelled.items():
            display.setdefault(filename, name)

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

    if by_project:
        per_project: dict[str, list[int]] = defaultdict(lambda: [0, 0])

        for filename, lines in merged.items():
            key = project_of(display.get(filename, filename))
            per_project[key][0] += sum(1 for hits in lines.values() if hits > 0)
            per_project[key][1] += len(lines)

        width = max(len(name) for name in per_project)

        print("Coverage by project:")
        print(f"  {'project'.ljust(width)}  {'covered':>7}  {'total':>5}  {'rate':>7}")
        for name in sorted(per_project):
            project_covered, project_total = per_project[name]
            project_rate = (project_covered / project_total * 100) if project_total else 0.0
            print(
                f"  {name.ljust(width)}  {project_covered:>7}  "
                f"{project_total:>5}  {project_rate:>6.2f}%"
            )
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
