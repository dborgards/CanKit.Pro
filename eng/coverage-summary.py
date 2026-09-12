#!/usr/bin/env python3
"""Turn the cobertura files coverlet writes into a line in the job summary.

CI has been passing --collect:"XPlat Code Coverage" and doing nothing with the result: the files
rode along in the test-results artifact, which means the number existed but nobody saw it. This
prints it where a pull request shows it.

Deliberately no threshold. A coverage gate during a bug-fix series blocks the work that raises
coverage; the number is here to be watched first, and can grow a floor once it has stopped moving.
"""

import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


def main(directory: str) -> int:
    reports = sorted(Path(directory).rglob('coverage.cobertura.xml'))
    if not reports:
        print(f'No coverage.cobertura.xml under {directory}.')
        return 0

    lines_covered = lines_valid = branches_covered = branches_valid = 0
    for report in reports:
        root = ElementTree.parse(report).getroot()
        lines_covered += int(root.get('lines-covered', 0))
        lines_valid += int(root.get('lines-valid', 0))
        branches_covered += int(root.get('branches-covered', 0))
        branches_valid += int(root.get('branches-valid', 0))

    def percent(covered: int, valid: int) -> str:
        return f'{covered / valid * 100:.1f}%' if valid else 'n/a'

    print('### Coverage\n')
    print('| | Covered | Total | |')
    print('| --- | ---: | ---: | ---: |')
    print(f'| Lines | {lines_covered} | {lines_valid} | '
          f'**{percent(lines_covered, lines_valid)}** |')
    print(f'| Branches | {branches_covered} | {branches_valid} | '
          f'**{percent(branches_covered, branches_valid)}** |')
    print(f'\n<sub>{len(reports)} report(s), net10.0 on Linux. No threshold is enforced.</sub>')
    return 0


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: coverage-summary.py <directory to search>')
    sys.exit(main(sys.argv[1]))
