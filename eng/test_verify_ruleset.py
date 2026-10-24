#!/usr/bin/env python3
"""Tests for eng/verify-ruleset.py (standard library only).

    python3 -m unittest discover -s eng -p "test_*.py"
"""

import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

_SPEC = importlib.util.spec_from_file_location('verify_ruleset', Path(__file__).with_name('verify-ruleset.py'))
ruleset_check = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(ruleset_check)

WORKFLOW = """\
name: CI
on:
  pull_request:
jobs:
  version:
    name: version
    runs-on: ubuntu-latest
  build-test:
    name: ${{ matrix.os }}
    strategy:
      matrix:
        os: [ubuntu-latest, 'windows-latest']   # trailing comment
  pack:
    name: pack (dry run)
    runs-on: ubuntu-latest
  unnamed:
    runs-on: ubuntu-latest
    steps:
      - name: Not a job name
        run: echo hi
"""


def ruleset(*contexts: str) -> dict:
    return {'rules': [{'type': 'deletion'},
                      {'type': 'required_status_checks',
                       'parameters': {'required_status_checks': [{'context': c, 'integration_id': 1} for c in contexts]}}]}


class JobCheckNames(unittest.TestCase):
    def test_names_ids_and_matrix_expansion(self):
        self.assertEqual({'version', 'ubuntu-latest', 'windows-latest', 'pack (dry run)', 'unnamed'},
                         ruleset_check.job_check_names(WORKFLOW))

    def test_step_names_are_not_job_names(self):
        self.assertNotIn('Not a job name', ruleset_check.job_check_names(WORKFLOW))

    def test_top_level_keys_outside_jobs_are_ignored(self):
        text = "name: CI\nconcurrency:\n  group: x\njobs:\n  a:\n    name: A\nenv:\n  b:\n    name: B\n"
        self.assertEqual({'A'}, ruleset_check.job_check_names(text))

    def test_an_unresolvable_expression_is_an_error_not_a_guess(self):
        with self.assertRaisesRegex(ValueError, "job 'a'"):
            ruleset_check.job_check_names("jobs:\n  a:\n    name: ${{ inputs.x }}\n")

    def test_a_matrix_key_without_an_inline_list_is_an_error(self):
        with self.assertRaisesRegex(ValueError, "no inline matrix list for 'os'"):
            ruleset_check.job_check_names("jobs:\n  a:\n    name: ${{ matrix.os }}\n    strategy:\n      matrix:\n        os:\n          - x\n")


class MissingChecks(unittest.TestCase):
    def test_all_present(self):
        self.assertEqual([], ruleset_check.missing_checks(ruleset('version', 'windows-latest', 'pack (dry run)'), WORKFLOW))

    def test_a_renamed_job_is_reported(self):
        self.assertEqual(['pack'], ruleset_check.missing_checks(ruleset('version', 'pack'), WORKFLOW))

    def test_a_ruleset_without_required_checks_is_an_error(self):
        with self.assertRaisesRegex(ValueError, 'no required_status_checks'):
            ruleset_check.missing_checks({'rules': [{'type': 'deletion'}]}, WORKFLOW)


class Command(unittest.TestCase):
    def run_main(self, contexts):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'r.json').write_text(json.dumps(ruleset(*contexts)), encoding='utf-8')
            (root / 'ci.yml').write_text(WORKFLOW, encoding='utf-8')
            err, out = io.StringIO(), io.StringIO()
            with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
                code = ruleset_check.main(['x', str(root / 'r.json'), str(root / 'ci.yml')])
        return code, out.getvalue(), err.getvalue()

    def test_exit_codes_and_messages(self):
        code, out, _ = self.run_main(['version'])
        self.assertEqual(0, code)
        self.assertIn('1 required check(s)', out)
        code, _, err = self.run_main(['version', 'gone'])
        self.assertEqual(1, code)
        self.assertIn("requires 'gone'", err)


class ThisRepository(unittest.TestCase):
    """The committed export against the committed workflow: the reason the script exists."""

    def test_every_required_check_of_the_exported_ruleset_is_a_job_of_ci_yml(self):
        exported = json.loads(ruleset_check.DEFAULT_RULESET.read_text(encoding='utf-8'))
        workflow = ruleset_check.DEFAULT_WORKFLOW.read_text(encoding='utf-8')
        self.assertEqual([], ruleset_check.missing_checks(exported, workflow))

    def test_the_export_requires_something(self):
        exported = json.loads(ruleset_check.DEFAULT_RULESET.read_text(encoding='utf-8'))
        self.assertTrue(ruleset_check.required_contexts(exported))


if __name__ == '__main__':
    unittest.main()
