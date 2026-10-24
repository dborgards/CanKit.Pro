#!/usr/bin/env python3
"""Keeps the scripts' copies of "the packable projects" in step with src/ (standard library only).

    python3 -m unittest discover -s eng -p "test_*.py"

The set of shipped packages is `src/*/*.csproj` (src/Directory.Build.props makes each packable).
It is also written down in eng/verify-packages.py and eng/verify-sboms.py as a count, in
eng/build-api-docs.sh as a list, and in tests/CanKit.Pro.Tests as PublicApiSurfaceTests.Tracked
(which that class checks itself). Adding a package and forgetting one of those copies used to fail
late or not at all; this fails on the pull request that adds it.
"""

import re
import unittest
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parent.parent


def packable_projects() -> list:
    return sorted(path.stem for path in (REPOSITORY / 'src').glob('*/*.csproj')
                  if '<IsPackable>false</IsPackable>' not in path.read_text(encoding='utf-8'))


def expected_count(script: str) -> int:
    text = (REPOSITORY / 'eng' / script).read_text(encoding='utf-8')
    return int(re.search(r'^EXPECTED_PACKAGE_COUNT = (\d+)$', text, re.MULTILINE).group(1))


def documented_packages() -> list:
    text = (REPOSITORY / 'eng' / 'build-api-docs.sh').read_text(encoding='utf-8')
    return sorted(re.search(r'^packages=\(\n(.*?)^\)', text, re.MULTILINE | re.DOTALL).group(1).split())


class PackageRegistry(unittest.TestCase):
    def test_there_are_packable_projects(self):
        self.assertTrue(packable_projects(), 'src/*/*.csproj matched nothing -- the glob is stale')

    def test_verify_packages_expects_that_many(self):
        self.assertEqual(len(packable_projects()), expected_count('verify-packages.py'))

    def test_verify_sboms_expects_that_many(self):
        self.assertEqual(len(packable_projects()), expected_count('verify-sboms.py'))

    def test_the_api_reference_covers_exactly_these(self):
        self.assertEqual(packable_projects(), documented_packages())


if __name__ == '__main__':
    unittest.main()
