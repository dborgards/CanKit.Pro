#!/usr/bin/env python3
"""Tests for the cited-id check of eng/verify-requirements-traceability.py (standard library only).

    python3 -m unittest discover -s eng -p "test_*.py"
"""

import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path
from unittest import mock

_SPEC = importlib.util.spec_from_file_location(
    'traceability', Path(__file__).with_name('verify-requirements-traceability.py'))
traceability = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(traceability)

KNOWN_REQS = {'FR-RAW-001', 'NFR-004'}
KNOWN_ADRS = {'ADR-7'}


def write(root: Path, name: str, text: str) -> None:
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8')


class UnknownCitations(unittest.TestCase):
    def check(self, files: dict) -> dict:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, text in files.items():
                write(root, name, text)
            result = traceability.unknown_citations([root], KNOWN_REQS, KNOWN_ADRS)
        return {cited: [Path(f).name for f in found] for cited, found in result.items()}

    def test_defined_ids_are_accepted(self):
        self.assertEqual({}, self.check({'A.cs': '/// FR-RAW-001, NFR-004 and ADR-7.'}))

    def test_an_unknown_requirement_is_reported_with_its_file(self):
        self.assertEqual({'FR-RAW-999': ['A.cs']}, self.check({'A.cs': '// FR-RAW-999', 'B.cs': '// FR-RAW-001'}))

    def test_an_unknown_decision_is_reported(self):
        self.assertEqual({'ADR-99': ['README.md']}, self.check({'README.md': 'see ADR-99'}))

    def test_generated_directories_are_skipped(self):
        self.assertEqual({}, self.check({'obj/Generated.cs': '// FR-RAW-999', 'bin/X.md': 'ADR-99'}))

    def test_other_file_types_are_ignored(self):
        self.assertEqual({}, self.check({'notes.txt': 'FR-RAW-999'}))

    def test_a_missing_root_is_not_an_error(self):
        self.assertEqual({}, traceability.unknown_citations([Path('/nonexistent-root')], KNOWN_REQS, KNOWN_ADRS))


class ExitCode(unittest.TestCase):
    """main() on a miniature repository: the cited-id check has to fail the run."""

    SRS = '| ID | Anforderung | Priorität | Verifikation |\n| --- | --- | --- | --- |\n' \
          '| `FR-RAW-001` | Eine Anforderung | Must | Unit-Test |\n'

    def run_main(self, source: str) -> int:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write(root, 'docs/requirements/SRS-CanKit.Pro.md', self.SRS)
            write(root, 'docs/architecture/arc42-CanKit.Pro.md', '### ADR-7 (umgesetzt): Entscheidung\n')
            write(root, 'tests/T.cs', '// FR-RAW-001')
            write(root, 'src/S.cs', source)
            with mock.patch.object(traceability, 'WAIVERS', {}), \
                    mock.patch.object(traceability, 'KNOWN_GAPS', set()), \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                return traceability.main(['verify-requirements-traceability.py'], root)

    def test_a_clean_repository_passes(self):
        self.assertEqual(0, self.run_main('/// FR-RAW-001, ADR-7'))

    def test_an_unknown_requirement_fails_the_run(self):
        self.assertEqual(1, self.run_main('/// FR-RAW-002'))

    def test_an_unknown_decision_fails_the_run(self):
        self.assertEqual(1, self.run_main('/// ADR-8'))


class Definitions(unittest.TestCase):
    def test_decisions_are_read_from_arc42_headings(self):
        text = '### ADR-5 (umgesetzt): L2-Demux\n#### ADR-12: Safety\nSee ADR-7 in the text.\n'
        self.assertEqual({'ADR-5', 'ADR-12'}, set(traceability.ADR_HEADING.findall(text)))

    def test_the_real_decision_list_is_found(self):
        arc42 = Path(__file__).resolve().parent.parent / 'docs' / 'architecture' / 'arc42-CanKit.Pro.md'
        self.assertIn('ADR-7', set(traceability.ADR_HEADING.findall(arc42.read_text(encoding='utf-8'))))


if __name__ == '__main__':
    unittest.main()
