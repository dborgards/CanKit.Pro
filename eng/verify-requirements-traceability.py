#!/usr/bin/env python3
"""Check that every `Must` requirement in the SRS is traceable to a test.

The SRS states the rule this script enforces (§7, "Grundprinzip"):

    Jede `Must`-Anforderung MUSS mindestens durch Unit- oder
    Virtual-Loopback-Integrationstest abgedeckt sein, bevor die jeweilige
    Ebene als "fertig" gilt.

Until this script existed the rule was unenforced, and nobody could say how
many `Must` requirements met it. That is the gap it closes: "complete" stops
being a judgement and becomes a number the build prints.

A requirement counts as traced when its ID appears anywhere under `tests/`.
That is deliberately weak -- a mention is not a proof -- but it is the
strongest check that can be made mechanically, and it is strictly better than
the nothing that preceded it. What it does catch is a `Must` requirement that
no test so much as refers to, which is a hole nobody has to argue about.

Requirements the SRS itself assigns to a non-test verification (architecture
review, API review) cannot satisfy that rule and are waived below, each with
the SRS's own stated means. A waiver is a claim about the SRS and is checked
against it: naming a requirement whose verification column does *not* match
fails the build, so the list cannot quietly grow to cover real gaps.

The check also runs the other way round: every requirement id (FR-/NFR-) and every ADR id
cited in src/, tests/, samples/ or tools/ must exist -- in the SRS and in the arc42
document's decision list respectively. XML documentation and comments are published verbatim
and the API reference turns those ids into links, so a typo'd or renumbered id is a dead link
nobody notices until a reader follows it.

Usage: python3 eng/verify-requirements-traceability.py [srs_path] [tests_dir]
"""
from __future__ import annotations

import pathlib
import re
import sys

# The middle segment is optional: functional requirements are FR-RAW-001, but the
# non-functional ones are plain NFR-001. Requiring three parts silently dropped all
# twelve NFRs -- the totals looked plausible and the ratchet simply did not cover them
# (Bugbot on #125). eng/mkdocs_hooks.py already treats that segment as optional.
REQ = re.compile(r"\b(?:FR|NFR)-(?:[A-Z0-9]+-)?\d+\b")

# Architecture decisions are cited as ADR-7 and defined as `### ADR-7 (umgesetzt): ...` headings
# of the arc42 document. docs/decisions/ numbers its records on its own (0001-...), not as ADR-n.
ADR = re.compile(r"\bADR-\d+\b")
ADR_HEADING = re.compile(r"^#{2,4}\s+(ADR-\d+)\b", re.MULTILINE)
# .csproj/.props carry citations too (the CANopen package description cites its requirements).
CITATION_SUFFIXES = {".cs", ".md", ".csproj", ".props", ".targets"}
# `FR-RAW-030..034` and `FR-CO-002/003/004` write the later endpoints without the prefix; REQ
# alone sees only the first id, so a typo in a later endpoint would pass.
REQ_SHORTHAND = re.compile(r"\b((?:FR|NFR)-(?:[A-Z0-9]+-)?)(\d+)((?:(?:\.\.|/)\d+)*)")
GENERATED_DIRECTORIES = {"bin", "obj"}

# id -> the substring that must appear in the SRS verification column for the
# waiver to hold. Keep the reason, not just the id: a bare id list is how an
# exemption table turns into a place to hide things.
WAIVERS = {
    # Documented ownership contract. The SRS verifies it by documentation review
    # against the arc42 document, which no test can stand in for.
    "FR-RAW-001": "Dokumentationsreview",
    # The four below are properties of the CI run itself -- that the matrix builds and
    # tests every target, on every platform, with no hardware attached. No single test
    # under tests/ can assert them, because the thing being asserted is the run.
    "NFR-004": "CI-Testmatrix",
    "NFR-005": "Buildmatrix",
    "NFR-009": "CI-Lauf",
    "NFR-010": "Architekturreview",
}

# `Must` requirements that no test mentions today. This is debt, not exemption --
# kept apart from WAIVERS on purpose, because a single list would let a real gap
# sit next to a legitimate one and look the same.
#
# The list may only ever shrink: a new untraced `Must` fails the build, and an
# entry here that has since been traced also fails it, so closing a gap forces
# the line out. Nothing is added without the maintainer deciding to add it.
#
# FR-RAW-032 is echo semantics that #94 reported as untestable on the default
# adapter -- which is why #94 was a missing verification of `Must` requirements,
# not test hygiene. Its sibling FR-RAW-015 left the list in #352: the tests pinning
# it (RawCanSubscriptionTests, on ControllableBus) were there, but cited FR-RAW-010.
# FR-TP-020 was waived here as verified by the package check until Codex pointed out on
# #125 that eng/verify-packages.py inspects package count, version, README, license and
# symbol metadata -- never the dependency list. The SRS says "Paketprüfung", but no
# package check actually looks, so this is debt like the rest until one does.
#
# NFR-001 and NFR-002 name a performance test and a macOS integration test respectively.
# Those are tests, so they are gaps rather than waivers; NFR-001 additionally has no
# target values yet (SRS appendix item 1).
KNOWN_GAPS = {
    "FR-RAW-002", "FR-RAW-003", "FR-RAW-004",
    "FR-RAW-032",
    "FR-TP-013", "FR-TP-014", "FR-TP-017", "FR-TP-020",
    "NFR-001", "NFR-002",
}


def parse_srs(path: pathlib.Path) -> dict[str, tuple[str, str]]:
    """Return {id: (priority, verification)} for every requirement table row."""
    out: dict[str, tuple[str, str]] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.split("|")[1:-1]]
        if len(cells) < 4:
            continue
        ids = REQ.findall(cells[0])
        # Only single-id rows are requirement definitions; a range like
        # "FR-RAW-001..005" in the traceability matrix is a reference, not a row.
        if len(ids) != 1 or ids[0] != cells[0].strip("` "):
            continue
        out[ids[0]] = (cells[2], cells[3])
    return out


def cited_requirements(text: str) -> list[str]:
    """Every requirement id cited in `text`, with the endpoints of `A..B` and `A/B/C` shorthand
    spelled out. A range is checked at its endpoints only: the ids in between are not cited."""
    cited = []
    for prefix, first, rest in REQ_SHORTHAND.findall(text):
        cited.append(f"{prefix}{first}")
        cited.extend(f"{prefix}{number}" for number in re.findall(r"\d+", rest))
    return cited


def unknown_citations(roots: list[pathlib.Path], known_reqs: set[str],
                      known_adrs: set[str]) -> dict[str, list[str]]:
    """Return {id: [files citing it]} for every cited FR-/NFR-/ADR- id that is not defined."""
    unknown: dict[str, set[str]] = {}
    for root in roots:
        if not root.is_dir():
            continue
        for f in root.rglob("*"):
            if f.suffix not in CITATION_SUFFIXES or not f.is_file():
                continue
            if GENERATED_DIRECTORIES & set(f.relative_to(root).parts):
                continue
            text = f.read_text(encoding="utf-8", errors="replace")
            cited = [(i, known_reqs) for i in cited_requirements(text)] + \
                    [(i, known_adrs) for i in ADR.findall(text)]
            for cited_id, known in cited:
                if cited_id not in known:
                    unknown.setdefault(cited_id, set()).add(f.as_posix())
    return {i: sorted(files) for i, files in sorted(unknown.items())}


def main(argv: list[str], root: pathlib.Path | None = None) -> int:
    root = root or pathlib.Path(__file__).resolve().parent.parent
    srs = pathlib.Path(argv[1]) if len(argv) > 1 else root / "docs/requirements/SRS-CanKit.Pro.md"
    tests = pathlib.Path(argv[2]) if len(argv) > 2 else root / "tests"

    if not srs.is_file():
        print(f"error: SRS not found at {srs}", file=sys.stderr)
        return 2
    if not tests.is_dir():
        print(f"error: tests directory not found at {tests}", file=sys.stderr)
        return 2

    reqs = parse_srs(srs)
    if not reqs:
        print(f"error: no requirement rows parsed from {srs} -- has the table shape changed?",
              file=sys.stderr)
        return 2

    # The citation check needs the repository's own layout: with explicit paths the caller is
    # pointing the script at a fixture, and the real sources cite the real SRS.
    unknown: dict[str, list[str]] = {}
    if len(argv) <= 1:
        arc42 = root / "docs/architecture/arc42-CanKit.Pro.md"
        if not arc42.is_file():
            print(f"error: arc42 document not found at {arc42}", file=sys.stderr)
            return 2
        known_adrs = set(ADR_HEADING.findall(arc42.read_text(encoding="utf-8")))
        unknown = unknown_citations([root / "src", tests, root / "samples", root / "tools"],
                                    set(reqs), known_adrs)

    traced: set[str] = set()
    for f in tests.rglob("*.cs"):
        traced.update(REQ.findall(f.read_text(encoding="utf-8", errors="replace")))

    musts = {i for i, (prio, _) in reqs.items() if prio.lower().startswith("must")}
    untraced = sorted(musts - traced)

    bad_waivers = []
    for wid, needle in WAIVERS.items():
        if wid not in reqs:
            bad_waivers.append(f"{wid}: waived but not defined in the SRS")
        elif needle.lower() not in reqs[wid][1].lower():
            bad_waivers.append(
                f"{wid}: waiver claims {needle!r} but the SRS verifies it by "
                f"{reqs[wid][1]!r}")

    open_gaps = [i for i in untraced if i not in WAIVERS]
    new_gaps = [i for i in open_gaps if i not in KNOWN_GAPS]
    closed = sorted(KNOWN_GAPS - set(open_gaps))

    print(f"Requirements in SRS:       {len(reqs)}")
    print(f"  of which Must:           {len(musts)}")
    print(f"  Must traced to a test:   {len(musts) - len(untraced)}")
    print(f"  Must waived (SRS §7):    {len([i for i in untraced if i in WAIVERS])}")
    print(f"  Must untraced (recorded):{len(open_gaps) - len(new_gaps)}")
    print(f"  Must untraced (NEW):     {len(new_gaps)}")

    if unknown:
        print("\nCited ids that are defined nowhere:", file=sys.stderr)
        for cited_id, files in unknown.items():
            print(f"  {cited_id}  cited in {', '.join(files[:3])}"
                  f"{' and ' + str(len(files) - 3) + ' more' if len(files) > 3 else ''}",
                  file=sys.stderr)
        print("\nFix the id, or add the requirement to the SRS / the decision to arc42.", file=sys.stderr)

    if bad_waivers:
        print("\nWaiver list is out of step with the SRS:", file=sys.stderr)
        for m in bad_waivers:
            print(f"  {m}", file=sys.stderr)

    if new_gaps:
        print("\nNEW Must requirements that no test mentions:", file=sys.stderr)
        for i in new_gaps:
            print(f"  {i}  {reqs[i][1][:80]}", file=sys.stderr)
        print("\nAdd a test that names the requirement id. Only the maintainer adds to "
              "KNOWN_GAPS -- that list is debt and may only shrink.", file=sys.stderr)

    if closed:
        print("\nKNOWN_GAPS lists requirements that are now traced -- remove them:",
              file=sys.stderr)
        for i in closed:
            print(f"  {i}", file=sys.stderr)

    return 1 if (new_gaps or closed or bad_waivers or unknown) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
