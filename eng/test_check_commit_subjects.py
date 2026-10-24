#!/usr/bin/env python3
"""Tests for eng/check-commit-subjects.py (standard library only).

    python3 -m unittest discover -s eng -p "test_*.py"
"""

import contextlib
import importlib.util
import io
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

_SPEC = importlib.util.spec_from_file_location(
    'check_commit_subjects', Path(__file__).with_name('check-commit-subjects.py'))
checker = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(checker)


class Subjects(unittest.TestCase):
    def test_conventional_commits_are_accepted(self):
        for subject in ('feat: add a thing', 'fix(actor): stop Dispose deadlocking', 'feat(addressing)!: rename it',
                        'fix!: break it', 'ci: check commit subjects', 'build(deps): bump x from 1 to 2',
                        'revert: drop it', 'perf(isotp): avoid a copy', 'docs(j1939tp): explain', 'test(uds): pin 0x78'):
            with self.subTest(subject):
                self.assertTrue(checker.is_conventional(subject))

    def test_everything_else_is_not(self):
        for subject in ('Fix the thing', 'feat add a thing', 'feat:no space', 'feat: ', 'feat():x', 'feat(a b): x',
                        'feature: x', 'FEAT: x', 'fix (actor): x', 'fixup! fix: x', 'Revert "feat: x"', '', 'wip',
                        'fix(a)(b): x', 'fix!!: x'):
            with self.subTest(subject):
                self.assertFalse(checker.is_conventional(subject))

    def test_offending_keeps_order_and_skips_conventional_subjects(self):
        subjects = ['feat: a', 'Fix b', 'wip', "Merge branch 'main' into x"]
        self.assertEqual(['Fix b', 'wip', "Merge branch 'main' into x"], checker.offending(subjects))
        self.assertEqual([], checker.offending([]))


def git(cwd: str, *args: str) -> None:
    subprocess.run(['git', '-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'commit.gpgsign=false', *args],
                   cwd=cwd, check=True, capture_output=True)


class BranchRange(unittest.TestCase):
    """The range is `origin/<base>..HEAD`: base commits are not the branch's, the branch's are."""

    def run_in_repo(self, subjects_on_branch, merge_subjects=()):
        with tempfile.TemporaryDirectory() as directory:
            git(directory, 'init', '-q', '-b', 'main')
            git(directory, 'commit', '-q', '--allow-empty', '-m', 'Initial commit that is not conventional')
            git(directory, 'update-ref', 'refs/remotes/origin/main', 'HEAD')
            for subject in subjects_on_branch:
                git(directory, 'commit', '-q', '--allow-empty', '-m', subject)
            for subject in merge_subjects:
                git(directory, 'switch', '-q', '-c', 'side', 'HEAD~1' if subjects_on_branch else 'HEAD')
                git(directory, 'commit', '-q', '--allow-empty', '-m', 'feat: on the side')
                git(directory, 'switch', '-q', 'main')
                git(directory, 'merge', '-q', '--no-ff', '-m', subject, 'side')
                git(directory, 'branch', '-q', '-D', 'side')
            previous = os.getcwd()
            os.chdir(directory)
            try:
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    code = checker.main(['check-commit-subjects.py', 'main'])
            finally:
                os.chdir(previous)
        return code, out.getvalue()

    def test_a_clean_branch_passes_and_the_base_is_not_checked(self):
        code, output = self.run_in_repo(['feat: a', 'fix(x): b'])
        self.assertEqual(0, code)
        self.assertIn('2 commit subject(s) checked', output)

    def test_a_branch_with_a_bad_subject_fails_and_names_it(self):
        code, output = self.run_in_repo(['feat: a', 'Fixed stuff'])
        self.assertEqual(1, code)
        self.assertIn("'Fixed stuff'", output)
        self.assertNotIn("'feat: a'", output)

    def test_a_two_parent_commit_is_skipped_whatever_its_subject(self):
        code, output = self.run_in_repo(['feat: a'], merge_subjects=['merge(canopen): bring main in', 'Merged stuff'])
        self.assertEqual(0, code)
        self.assertIn('3 commit subject(s) checked', output)  # feat: a, plus one side commit per merge

    def test_a_one_parent_commit_with_the_same_subject_fails(self):
        for subject in ('merge(canopen): bring main in', "Merge branch 'main' into x"):
            with self.subTest(subject):
                code, output = self.run_in_repo(['feat: a', subject])
                self.assertEqual(1, code)
                self.assertIn(repr(subject), output)

    def test_an_empty_branch_passes(self):
        code, output = self.run_in_repo([])
        self.assertEqual(0, code)
        self.assertIn('0 commit subject(s) checked', output)


class Usage(unittest.TestCase):
    def test_the_base_branch_is_required(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, checker.main(['check-commit-subjects.py']))

    def test_main_reads_the_range_from_branch_subjects(self):
        with mock.patch.object(checker, 'branch_subjects', return_value=['wip']) as subjects, \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, checker.main(['x', 'develop']))
        subjects.assert_called_once_with('develop')


if __name__ == '__main__':
    unittest.main()
