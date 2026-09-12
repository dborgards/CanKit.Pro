#!/usr/bin/env python3
"""Assert that every packed .nupkg carries what the release depends on.

The CI pack job used to claim it proved "every package still carries a README, a license
expression and symbols" while only running `dotnet pack` and listing the output. Nothing looked
inside. These are the invariants a release breaks on -- and by then the tag exists, so the cheap
moment to check them is the pull request.

Checks, per package:
  * README.md at the package root (PackageReadmeFile in Directory.Build.props)
  * a <license type="expression"> in the .nuspec
  * a matching .snupkg (IncludeSymbols + SymbolPackageFormat=snupkg)

Plus: the expected number of packages, so a project that silently gains or loses IsPackable is
noticed here rather than on nuget.org.
"""

import sys
import xml.etree.ElementTree as ElementTree
import zipfile
from pathlib import Path

EXPECTED_PACKAGE_COUNT = 9


def check(package: Path) -> list:
    problems = []

    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()

        if 'README.md' not in names:
            problems.append('no README.md at the package root')

        nuspec_name = next((n for n in names if n.endswith('.nuspec') and '/' not in n), None)
        if nuspec_name is None:
            problems.append('no .nuspec')
        else:
            nuspec = ElementTree.fromstring(archive.read(nuspec_name))
            # The .nuspec is namespaced and the namespace URI moves with the schema version, so
            # match on the local tag name instead of hard-coding it.
            license_nodes = [
                node for node in nuspec.iter()
                if node.tag.rpartition('}')[2] == 'license'
            ]
            if not license_nodes:
                problems.append('no <license> in the .nuspec')
            elif license_nodes[0].get('type') != 'expression':
                problems.append(
                    f'<license type="{license_nodes[0].get("type")}">, expected "expression"')
            elif not (license_nodes[0].text or '').strip():
                problems.append('<license type="expression"> is empty')

    if not package.with_suffix('.snupkg').exists():
        problems.append('no matching .snupkg')

    return problems


def main(directory: str) -> int:
    packages = sorted(Path(directory).glob('*.nupkg'))

    if not packages:
        print(f'no .nupkg files in {directory}', file=sys.stderr)
        return 1

    failed = False
    for package in packages:
        problems = check(package)
        if problems:
            failed = True
            print(f'FAIL  {package.name}')
            for problem in problems:
                print(f'        {problem}')
        else:
            print(f'ok    {package.name}')

    if len(packages) != EXPECTED_PACKAGE_COUNT:
        failed = True
        print(f'\nFAIL  packed {len(packages)} packages, expected {EXPECTED_PACKAGE_COUNT}. '
              f'If a project gained or lost IsPackable on purpose, update '
              f'EXPECTED_PACKAGE_COUNT in {Path(__file__).name} and say so in the commit.')

    print(f'\n{len(packages)} package(s) checked')
    return 1 if failed else 0


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: verify-packages.py <directory with .nupkg files>')
    sys.exit(main(sys.argv[1]))
