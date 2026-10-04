#!/usr/bin/env python3
"""Point package validation at the release that has just been published.

src/Directory.Build.props compares every packed package with PackageValidationBaselineVersion,
the last release on nuget.org. semantic-release runs this inside the exec plugin's publishCmd,
chained directly behind `dotnet nuget push`, with the version it just published: the baseline only
ever names packages that exist. Had it moved in `prepare` (with the changelog commit), a publish
that failed would have left main comparing against a version nuget.org never got, and every later
pack, the tag repack in docs/release-process.md included, would fail to resolve it (Bugbot on
#281). It is not the `success` step either: that runs only once every publish plugin is through,
so a GitHub Release that failed after the packages were out would have left the baseline behind
(Codex on #281). The one commit this makes lands after the tag and carries [skip ci], like the
changelog commit it follows.

Exactly one element has to change. Zero means the props file no longer carries the element this
script was written for; that fails loudly rather than leaving a baseline that silently stopped
moving.

Usage:
  set-package-validation-baseline.py X.Y.Z                    edit the file only
  set-package-validation-baseline.py X.Y.Z --commit-and-push BRANCH
                                                              edit, commit, push to BRANCH

The push URL is built from GITHUB_REPOSITORY and GITHUB_TOKEN (the RELEASE_TOKEN the release
step holds, the same way semantic-release pushes), never taken from the command line, which the
exec plugin logs. PACKAGE_VALIDATION_BASELINE_PUSH_URL overrides it, for a test against a local
repository. git's output is printed with the token redacted.
"""

import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROPS = ROOT / "src" / "Directory.Build.props"
ELEMENT = re.compile(r"<PackageValidationBaselineVersion>([^<]*)</PackageValidationBaselineVersion>")


def set_baseline(version: str) -> str:
    text = PROPS.read_text(encoding="utf-8")
    new_text, count = ELEMENT.subn(
        f"<PackageValidationBaselineVersion>{version}</PackageValidationBaselineVersion>", text)
    if count != 1:
        raise SystemExit(f"expected exactly one PackageValidationBaselineVersion in {PROPS}, found {count}")
    PROPS.write_text(new_text, encoding="utf-8")
    return ELEMENT.search(text).group(1)


def push_url() -> str:
    override = os.environ.get("PACKAGE_VALIDATION_BASELINE_PUSH_URL")
    if override:
        return override
    repo = os.environ.get("GITHUB_REPOSITORY")
    token = os.environ.get("GITHUB_TOKEN")
    if not repo or not token:
        raise SystemExit("GITHUB_REPOSITORY and GITHUB_TOKEN are needed to push (or PACKAGE_VALIDATION_BASELINE_PUSH_URL)")
    return f"https://x-access-token:{token}@github.com/{repo}.git"


def git(*args: str, redact: str = "") -> None:
    completed = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True)
    output = (completed.stdout + completed.stderr).replace(redact, "***") if redact else completed.stdout + completed.stderr
    if output.strip():
        print(output.strip())
    if completed.returncode != 0:
        shown = [("***" if redact and redact in a else a) for a in args]
        raise SystemExit(f"git {' '.join(shown)} failed with exit code {completed.returncode}")


def commit_and_push(version: str, branch: str) -> None:
    # The identity semantic-release gives its own commits, handed down in the environment it
    # runs plugin commands with; its defaults are the fallback for a run outside of it.
    name = os.environ.get("GIT_COMMITTER_NAME", "semantic-release-bot")
    email = os.environ.get("GIT_COMMITTER_EMAIL", "semantic-release-bot@martynus.net")
    url = push_url()
    token = os.environ.get("GITHUB_TOKEN", "")
    git("add", "--", str(PROPS.relative_to(ROOT)))
    git("-c", f"user.name={name}", "-c", f"user.email={email}", "commit", "-q",
        "-m", f"chore(release): point package validation at {version} [skip ci]")
    git("push", url, f"HEAD:refs/heads/{branch}", redact=token)


def main(argv: list[str]) -> int:
    args = argv[1:]
    if not args or not re.fullmatch(r"\d+\.\d+\.\d+", args[0]) or len(args) not in (1, 3) \
            or (len(args) == 3 and args[1] != "--commit-and-push"):
        print(f"usage: {Path(argv[0]).name} X.Y.Z [--commit-and-push BRANCH]", file=sys.stderr)
        return 2
    version = args[0]
    previous = set_baseline(version)
    print(f"PackageValidationBaselineVersion: {previous} -> {version}")
    if len(args) == 3:
        commit_and_push(version, args[2])
        print(f"pushed to {args[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
