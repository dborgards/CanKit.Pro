"""MkDocs hooks for the CanKit.Pro website (wired up under `hooks:` in mkdocs.yml).

Two jobs, both about the requirement ids (`FR-RAW-030`) and decision ids (`ADR-7`) that the XML
documentation comments quote in prose:

1. The SRS lists its requirements as table rows, and a table row has no heading, so nothing in the
   rendered page can be linked to. Every row whose first cell is a requirement id gets an
   `id="fr-raw-030"` anchor, so `…/requirements/SRS-CanKit.Pro/#fr-raw-030` jumps to that row.

2. On the generated API pages under `api/`, every requirement and decision id becomes a link to
   that anchor and to the matching `### ADR-n …` heading in the arc42 document. Ids that neither
   document defines (`ADR-12` at the time of writing) stay plain text rather than becoming a dead
   link, and a range such as `FR-RAW-010..013` is linked as a whole to its first id.

The same pass also fixes the upstream references DefaultDocumentation cannot resolve. It renders
any `<see cref>` it does not know as a learn.microsoft.com URL, which is right for `System.*` and
wrong for CanKit: `CanKit.Abstractions.*` and `CanKit.Core.*` are pointed at the CanKit repository
instead, and CanKit.Pro's own non-public types — which have no page here — lose their link and
stay as text.

Neither document is edited on disk; this happens to the markdown on its way into the build.
"""

from __future__ import annotations

import posixpath
import re
from pathlib import Path

from markdown.extensions.toc import slugify

SRS_PAGE = "requirements/SRS-CanKit.Pro.md"
ARC42_PAGE = "architecture/arc42-CanKit.Pro.md"
API_PREFIX = "api/"

CANKIT_REPO = "https://github.com/pkuyo/CanKit"

# "| FR-RAW-030 | Das System MUSS …" — the id column of a requirement table.
REQUIREMENT_ROW = re.compile(r"^\|\s*((?:FR|NFR)-[A-Z0-9]+(?:-[0-9]+)?)\s*\|")

# "### ADR-7 (umgesetzt): TX-Confirm-Abstraktion"
ADR_HEADING = re.compile(r"^#{2,6}\s+(ADR-[0-9]+)\b(?P<rest>.*)$")

# Requirement and decision ids in prose. DefaultDocumentation escapes hyphens and dots in the text
# it renders itself ("FR\-RAW\-030") but passes the contents of a <remarks> block through as-is, so
# both spellings occur; the backslashes are optional here and stripped before the id is looked up.
IDS = re.compile(
    r"\b(?:"
    r"(?P<nfr>NFR\\?-[0-9]+)"
    r"|(?P<fr>FR\\?-[A-Z0-9]+\\?-[0-9]+(?:(?:\\?\.){2}[0-9]+)?)"
    r"|(?P<adr>ADR\\?-[0-9]+)"
    r")"
)

# A learn.microsoft.com link DefaultDocumentation invented for a CanKit type it could not resolve.
# The title argument is always present in its output, which is what bounds the URL match.
UPSTREAM_LINK = re.compile(
    r"\[(?P<text>(?:[^\[\]\\]|\\.)*)\]"
    r"\(https://learn\.microsoft\.com/en-us/dotnet/api/(?P<id>cankit[^\s]*?)"
    r" '(?P<title>[^']*)'\)"
)

# Things that must not be rewritten: fenced code, inline code, and anything already a link (a
# nested link is not a link at all). Masked out before the ids are turned into links.
PROTECTED = re.compile(
    r"```.*?```"
    r"|`[^`\n]*`"
    r"|\[(?:[^\[\]\\]|\\.)*\]\((?:[^()\\]|\\.|\([^()]*\))*\)",
    re.DOTALL,
)

_requirements: set[str] = set()
_decisions: dict[str, str] = {}


def _read(config, page: str) -> str:
    return Path(config["docs_dir"], page).read_text(encoding="utf-8")


def on_config(config):
    """Collect the anchors the API pages are allowed to link to."""
    _requirements.clear()
    _decisions.clear()

    for line in _read(config, SRS_PAGE).splitlines():
        match = REQUIREMENT_ROW.match(line)
        if match:
            _requirements.add(match.group(1))

    for line in _read(config, ARC42_PAGE).splitlines():
        match = ADR_HEADING.match(line)
        if match and match.group(1) not in _decisions:
            # The heading id MkDocs will generate: the toc extension slugifies the heading text
            # with its inline markup resolved, so strip the backticks and emphasis first.
            text = match.group(1) + match.group("rest")
            text = re.sub(r"[`*_]", "", text)
            _decisions[match.group(1)] = slugify(text, "-")

    return config


def on_page_markdown(markdown, page, config, files):
    src = page.file.src_uri
    if src == SRS_PAGE:
        return _anchor_requirements(markdown)
    if src.startswith(API_PREFIX):
        return _link_ids(_fix_upstream_links(markdown), src)
    return markdown


def _anchor_requirements(markdown: str) -> str:
    seen: set[str] = set()
    out = []
    fenced = False
    for line in markdown.splitlines():
        if line.startswith("```"):
            fenced = not fenced
        match = None if fenced else REQUIREMENT_ROW.match(line)
        if match and match.group(1) not in seen:
            seen.add(match.group(1))
            anchor = f'<a id="{slugify(match.group(1), "-")}"></a>'
            line = line[: match.start(1)] + anchor + line[match.start(1) :]
        out.append(line)
    return "\n".join(out)


def _fix_upstream_links(markdown: str) -> str:
    def replace(match: re.Match) -> str:
        if match.group("id").startswith("cankit.pro."):
            # One of CanKit.Pro's own internal types: it has no page, so drop the link.
            return match.group("text")
        return f"[{match.group('text')}]({CANKIT_REPO} '{match.group('title')} — documented in CanKit')"

    return UPSTREAM_LINK.sub(replace, markdown)


def _link_ids(markdown: str, src: str) -> str:
    here = posixpath.dirname(src)
    srs = posixpath.relpath(SRS_PAGE, here)
    arc42 = posixpath.relpath(ARC42_PAGE, here)

    def target(match: re.Match) -> str | None:
        text = match.group(0)
        plain = text.replace("\\", "")
        if match.group("adr"):
            slug = _decisions.get(plain)
            return f"{arc42}#{slug}" if slug else None
        # "FR-RAW-010..013" is linked as a whole to the first id of the range.
        first = plain.split("..")[0]
        return f"{srs}#{slugify(first, '-')}" if first in _requirements else None

    def linkify(text: str) -> str:
        def replace(match: re.Match) -> str:
            url = target(match)
            return f"[{match.group(0)}]({url})" if url else match.group(0)

        return IDS.sub(replace, text)

    # Mask code and existing links, rewrite what is left, put them back.
    masked: list[str] = []

    def stash(match: re.Match) -> str:
        masked.append(match.group(0))
        return f"\x00{len(masked) - 1}\x00"

    result = linkify(PROTECTED.sub(stash, markdown))
    return re.sub(r"\x00([0-9]+)\x00", lambda m: masked[int(m.group(1))], result)
