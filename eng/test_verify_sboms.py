#!/usr/bin/env python3
"""Tests for eng/verify-sboms.py's version-floor cross-check (standard library only).

    python3 -m unittest discover -s eng -p "test_*.py"

CI runs this in the `pack (dry run)` job. The cross-check compares the SBOM's component versions
with the floors the .nuspec declares; the rest of verify-sboms.py is exercised by running it on
the packages that job packs.
"""

import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location('verify_sboms', Path(__file__).with_name('verify-sboms.py'))
verify_sboms = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(verify_sboms)

NUSPEC = """<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Demo</id>
    <version>1.0.0</version>
    <dependencies>
      <group targetFramework=".NETStandard2.0">
        <dependency id="Lib.Old" version="1.0.0" />
        <dependency id="Shared" version="[1.0.0, )" />
      </group>
      <group targetFramework="net10.0">
        <dependency id="Shared" version="[2.0.0, )" />
        <dependency id="Capped" version="(, 3.0.0)" />
      </group>
    </dependencies>
  </metadata>
</package>
"""


def component(name: str, version: str) -> dict:
    return {'name': name, 'version': version}


def make_package(directory: str) -> Path:
    path = Path(directory) / 'Demo.1.0.0.nupkg'
    with zipfile.ZipFile(path, 'w') as archive:
        archive.writestr('Demo.nuspec', NUSPEC)
    return path


class VersionRange(unittest.TestCase):
    def test_floor_of_every_range_shape(self):
        floors = {'1.2.3': '1.2.3', '[1.2.3, )': '1.2.3', '[1.2.3]': '1.2.3',
                  '(1.2.3, 2.0)': '1.2.3', '(, 2.0)': '', '': ''}
        for text, expected in floors.items():
            with self.subTest(text):
                self.assertEqual(expected, verify_sboms.version_floor(text))

    def test_versions_order_as_semver(self):
        key = verify_sboms.version_key
        self.assertLess(key('4.6.3'), key('10.0.12'))
        self.assertLess(key('1.4.0-pr.2'), key('1.4.0-pr.10'))
        self.assertLess(key('1.4.0-pr.17.3'), key('1.4.0'))
        self.assertEqual(key('1.4.0+abc'), key('1.4.0'))
        self.assertEqual(key('1.4'), key('1.4.0.0'))


class DeclaredDependencies(unittest.TestCase):
    def test_collects_one_floor_per_target_framework_group(self):
        with tempfile.TemporaryDirectory() as directory:
            declared = verify_sboms.declared_dependencies(make_package(directory))
        self.assertEqual({'lib.old': ['1.0.0'], 'shared': ['1.0.0', '2.0.0'], 'capped': []}, declared)


class BelowFloor(unittest.TestCase):
    declared = {'lib.old': ['1.0.0'], 'shared': ['1.0.0', '2.0.0'], 'capped': []}

    def test_equal_or_higher_is_accepted(self):
        listed = [component('Lib.Old', '1.0.0'), component('Shared', '2.5.0'), component('Capped', '0.1.0')]
        self.assertEqual([], verify_sboms.below_floor(self.declared, listed))

    def test_versions_compare_numerically_not_as_text(self):
        # 10.0.12 sorts below 4.6.3 as a string; System.Memory and Microsoft.Bcl.* really do span it.
        self.assertEqual([], verify_sboms.below_floor({'system.memory': ['4.6.3']},
                                                      [component('System.Memory', '10.0.12')]))

    def test_a_version_below_the_floor_is_reported(self):
        listed = [component('Lib.Old', '0.9.9'), component('Shared', '2.0.0')]
        self.assertEqual(['the .nuspec requires lib.old >= 1.0.0, the SBOM lists 0.9.9'],
                         verify_sboms.below_floor(self.declared, listed))

    def test_the_union_of_target_frameworks_has_to_reach_the_highest_floor(self):
        self.assertEqual(['the .nuspec requires shared >= 2.0.0, the SBOM lists 1.0.0'],
                         verify_sboms.below_floor(self.declared, [component('Shared', '1.0.0')]))
        self.assertEqual([], verify_sboms.below_floor(
            self.declared, [component('Shared', '1.0.0'), component('Shared', '2.0.0')]))

    def test_check_reports_it(self):
        # Wiring: check() has to call the cross-check, not just define it.
        with tempfile.TemporaryDirectory() as directory:
            package = make_package(directory)
            document = {'components': [component('Lib.Old', '0.9.9'), component('Shared', '2.0.0'),
                                       component('Capped', '1.0.0')]}
            problems = verify_sboms.check(document, 'Demo', package, '1.0.0')
        self.assertIn('the .nuspec requires lib.old >= 1.0.0, the SBOM lists 0.9.9', problems)


if __name__ == '__main__':
    unittest.main()
