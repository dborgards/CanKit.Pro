#!/usr/bin/env python3
"""Checks that every commit subject a pull request adds is a Conventional Commit.

    python3 eng/check-commit-subjects.py <base-branch>

semantic-release analyses every commit on the branch, not the pull-request title (CLAUDE.md,
CONTRIBUTING.md), so a subject that is not `type(scope)?!?: description` is silently ignored by the
analyser: a `fix` written as "Fix the thing" publishes nothing. This reports such subjects on the
pull request that introduces them. Standard library only; the unit tests are eng/test_check_commit_subjects.py.

The commits checked are `git log --no-merges origin/<base>..HEAD --format=%s`; the workflow checks
out the pull request's head commit, not GitHub's synthetic merge commit, so the range is the
branch's own.

Commits with more than one parent are skipped whatever their subject. Merge commits are written by
git (`Merge branch '…' into …`, `Merge remote-tracking branch '…' into …`) or by people and agents
(`merge(canopen): …`); semantic-release does read them, but a subject that is not a Conventional
Commit is ignored, so there is nothing to report. There is no subject allow-list: a commit with a
single parent is not a merge, so `Merge pull request …` as the subject of one (a squash would carry
the pull-request title instead) is reported like any other non-conventional subject. `Revert "…"`
and `fixup!` are not exempt either.

The job that runs this is deliberately not a required check of the `main` ruleset: it reports, the
maintainer decides.
"""

import re
import subprocess
import sys

# The types .releaserc.json maps to a release rule or a changelog section.
TYPES = ('feat', 'fix', 'perf', 'revert', 'build', 'docs', 'refactor', 'test', 'ci', 'chore', 'style')

CONVENTIONAL = re.compile(r'^(?:' + '|'.join(TYPES) + r')(?:\([^()\s]+\))?!?: \S')


def is_conventional(subject: str) -> bool:
    return CONVENTIONAL.match(subject) is not None


def offending(subjects):
    """The subjects that are not a Conventional Commit."""
    return [s for s in subjects if not is_conventional(s)]


def branch_subjects(base: str):
    result = subprocess.run(
        ['git', 'log', '--no-merges', f'origin/{base}..HEAD', '--format=%s'],
        check=True, capture_output=True, text=True, encoding='utf-8')
    return result.stdout.splitlines()


def main(argv) -> int:
    if len(argv) != 2:
        print(__doc__.strip().split('\n\n')[0], file=sys.stderr)
        return 2
    subjects = branch_subjects(argv[1])
    bad = offending(subjects)
    for subject in bad:
        print(f'not a Conventional Commit: {subject!r}')
    if bad:
        print(f'{len(bad)} of {len(subjects)} commit subject(s) on this branch do not read as '
              '`type(scope)?!?: description` (see CONTRIBUTING.md, "Commits and pull-request titles '
              'decide the release"). semantic-release ignores them.')
        return 1
    print(f'{len(subjects)} commit subject(s) checked, all fine.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
