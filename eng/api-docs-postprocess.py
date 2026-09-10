#!/usr/bin/env python3
"""Tidy up what DefaultDocumentation writes, for eng/build-api-docs.sh.

DefaultDocumentation names every page after the full name of the type it documents
(``CanKit.Pro.RawCan.ICanBusService.md``). Because we already put each package into its own
directory under ``docs/api/``, that prefix is pure repetition in every URL, so this script strips
it: ``docs/api/CanKit.Pro.RawCan/ICanBusService.md``, published as ``/api/CanKit.Pro.RawCan/
ICanBusService/``. The namespace page of a package's root namespace becomes that package's
``index.md``.

Two modes, both used by the build script:

``links <links-file> <package>``
    Rewrite a generated links file in place, so the *next* generation pass emits cross-package
    references that already point at the renamed files.

``pages <api-dir> <package>...``
    Rename the pages of every package, fix the intra-package links, and write ``index.md`` plus the
    ``SUMMARY.md`` that mkdocs-literate-nav turns into the API tab.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# The file-name part of a link whose target sits in the same directory. Only that part is matched:
# what may follow is an anchor full of parentheses (member signatures) and a quoted title, neither
# of which a regex should try to balance. Targets containing a "/" are cross-package links, and the
# links files already carry those renamed.
SAME_DIR_LINK = re.compile(r"\]\((?P<file>[^()\s/#]+\.md)(?=[#)\s])")


def rename(base: str, package: str) -> str:
    """Map a generated file name to its published one, within `package`."""
    stem, _, suffix = base.rpartition(".md")
    if suffix or not stem:  # not a .md file name
        return base
    if stem == package:
        return "index.md"
    if stem.startswith(package + ".") and len(stem) > len(package) + 1:
        return stem[len(package) + 1 :] + ".md"
    return base


def rewrite_links(text: str, package: str) -> str:
    return SAME_DIR_LINK.sub(lambda m: "](" + rename(m["file"], package), text)


def promote_headings(text: str) -> str:
    """Give each page a proper heading hierarchy.

    DefaultDocumentation writes every page assuming its members live on pages of their own, so it
    titles both the type and each of its members `##` and puts the "Properties"/"Methods" group
    headings at `###` — which, with the members inlined, nests the wrong way round and leaves the
    page without a level-one heading. Shifting the type title to `#`, the group headings to `##`
    and the member titles to `###` puts "Property Value"/"Parameters"/"Returns" (already `####`)
    back underneath the member they belong to, and gives Material a table of contents that follows
    the page.
    """
    out = []
    first = True
    fenced = False
    for line in text.splitlines():
        if line.startswith("```"):
            fenced = not fenced
        elif not fenced:
            if line.startswith("## "):
                line = ("# " if first else "### ") + line[3:]
                first = False
            elif line.startswith("### "):
                line = "## " + line[4:]
        out.append(line)
    return "\n".join(out) + "\n"


def do_links(links_file: Path, package: str) -> None:
    """Rewrite the file-name column of a links file (id|file#anchor|display)."""
    lines = links_file.read_text(encoding="utf-8").splitlines()
    out = []
    for index, line in enumerate(lines):
        parts = line.split("|")
        # The first line is the base url, and it has no columns.
        if index == 0 or len(parts) < 2:
            out.append(line)
            continue
        target, _, anchor = parts[1].partition("#")
        parts[1] = rename(target, package) + (("#" + anchor) if anchor else "")
        out.append("|".join(parts))
    links_file.write_text("\n".join(out) + "\n", encoding="utf-8")


def title_of(page: Path) -> str:
    """The display name for a page: the type name out of its '# Foo Interface' heading."""
    for line in page.read_text(encoding="utf-8").splitlines():
        if line.startswith("# "):
            # "# ICanBusService Interface" / "# CanKit\.Pro\.RawCan Namespace"
            heading = line[2:].strip().replace("\\", "")
            for kind in ("Class", "Struct", "Interface", "Enum", "Delegate", "Namespace"):
                if heading.endswith(" " + kind):
                    return heading[: -len(kind) - 1]
            return heading
    return page.stem


def is_namespace_page(page: Path) -> bool:
    """A namespace page lists the types of a namespace; every other page documents one type."""
    first = page.read_text(encoding="utf-8").split("\n", 1)[0]
    return first.startswith("# ") and first.rstrip().endswith(" Namespace")


def do_pages(api_dir: Path, packages: list[str]) -> None:
    summary = ["# API reference", "", "* [Overview](index.md)"]
    total = 0

    for package in packages:
        package_dir = api_dir / package
        if not package_dir.is_dir():
            raise SystemExit(f"{package_dir} does not exist — did the generator run?")

        for page in sorted(package_dir.glob("*.md")):
            new_name = rename(page.name, package)
            if new_name != page.name:
                page = page.replace(package_dir / new_name)

        for page in sorted(package_dir.glob("*.md")):
            text = rewrite_links(page.read_text(encoding="utf-8"), package)
            page.write_text(promote_headings(text), encoding="utf-8")

        index = package_dir / "index.md"
        if not index.exists():
            raise SystemExit(f"{index} is missing — the root namespace page was not generated.")

        pages = sorted(
            (p for p in package_dir.glob("*.md") if p.name != "index.md"),
            key=lambda p: p.stem.lower(),
        )
        # A package may hold more than its root namespace (CANopen has Emcy, Nmt, Pdo and Sdo).
        # Those pages are named "Sdo.SdoAbortCode.md", and belong under their namespace's own page
        # in the navigation. Sorting by name already puts each namespace ahead of its types.
        namespaces = sorted((p.stem for p in pages if is_namespace_page(p)), key=len, reverse=True)

        total += len(pages) - len(namespaces)
        summary.append(f"* [{package}]({package}/index.md)")
        for page in pages:
            if is_namespace_page(page):
                summary.append(f"    * [{page.stem}]({package}/{page.name})")
            elif any(page.stem.startswith(n + ".") for n in namespaces):
                summary.append(f"        * [{title_of(page)}]({package}/{page.name})")
            else:
                summary.append(f"    * [{title_of(page)}]({package}/{page.name})")

    (api_dir / "SUMMARY.md").write_text("\n".join(summary) + "\n", encoding="utf-8")
    write_overview(api_dir, packages, total)
    print(f"api docs: {total} type pages across {len(packages)} packages")


def write_overview(api_dir: Path, packages: list[str], total: int) -> None:
    lines = [
        "# API reference",
        "",
        "Generated from the XML documentation comments of the published assemblies by",
        "`eng/build-api-docs.sh`, one page per public type. It is not checked in — every build of",
        "the website regenerates it from the sources, so it can never drift from the code.",
        "",
        "Everything listed here is the public surface of the `net10.0` build; the `netstandard2.0`",
        "build offers the same API.",
        "",
        "| Package | Types |",
        "| :--- | ---: |",
    ]
    for package in packages:
        count = len([p for p in (api_dir / package).glob("*.md") if not is_namespace_page(p)])
        lines.append(f"| [{package}]({package}/index.md) | {count} |")
    lines += [
        "",
        f"{total} types in total. Requirement ids (`FR-…`) and decision ids (`ADR-…`) quoted in",
        "these pages link into [the requirements](../requirements/SRS-CanKit.Pro.md) and",
        "[the architecture document](../architecture/arc42-CanKit.Pro.md).",
        "",
        "Types from [CanKit](https://github.com/pkuyo/CanKit) itself — `CanKit.Abstractions.*` and",
        "`CanKit.Core.*` — are documented upstream, not here.",
        "",
    ]
    (api_dir / "index.md").write_text("\n".join(lines), encoding="utf-8")


def main(argv: list[str]) -> None:
    if len(argv) >= 3 and argv[0] == "links":
        do_links(Path(argv[1]), argv[2])
    elif len(argv) >= 3 and argv[0] == "pages":
        do_pages(Path(argv[1]), argv[2:])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
