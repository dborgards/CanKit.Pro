#!/usr/bin/env python3
"""Point package validation at the release that is about to be published.

src/Directory.Build.props compares every packed package with PackageValidationBaselineVersion,
the last release on nuget.org. semantic-release runs this as the exec plugin's prepareCmd, after
eng/verify-packages.py and before @semantic-release/git commits the changelog, with the version
it is about to tag; the props file is in that commit's assets, so the baseline moves with every
release and is never a number somebody has to remember to bump (#257).

Exactly one element has to change. Zero means the props file no longer carries the element this
script was written for and the release stops here, before the tag exists, instead of shipping
with a baseline that silently stopped moving.
"""

import re
import sys
from pathlib import Path

PROPS = Path(__file__).resolve().parent.parent / "src" / "Directory.Build.props"
ELEMENT = re.compile(r"<PackageValidationBaselineVersion>([^<]*)</PackageValidationBaselineVersion>")


def main(argv: list[str]) -> int:
    if len(argv) != 2 or not re.fullmatch(r"\d+\.\d+\.\d+", argv[1]):
        print(f"usage: {Path(argv[0]).name} X.Y.Z", file=sys.stderr)
        return 2
    version = argv[1]
    text = PROPS.read_text(encoding="utf-8")
    new_text, count = ELEMENT.subn(f"<PackageValidationBaselineVersion>{version}</PackageValidationBaselineVersion>", text)
    if count != 1:
        print(f"expected exactly one PackageValidationBaselineVersion in {PROPS}, found {count}", file=sys.stderr)
        return 1
    previous = ELEMENT.search(text).group(1)
    PROPS.write_text(new_text, encoding="utf-8")
    print(f"PackageValidationBaselineVersion: {previous} -> {version}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
