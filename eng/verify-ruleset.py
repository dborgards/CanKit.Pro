#!/usr/bin/env python3
"""Checks that every status check the `main` ruleset requires is a job that ci.yml produces.

    python3 eng/verify-ruleset.py [ruleset.json [ci.yml]]

A required check that no job reports blocks every pull request for good (GitHub waits for a check
that never arrives), and renaming a job's `name:` in ci.yml does exactly that without any file
saying so. .github/rulesets/main.json is the ruleset as GitHub's API returns it
(`gh api repos/dborgards/CanKit.Pro/rulesets/<id>`, minus node ids, timestamps and links); this
reads its `required_status_checks` rule and compares each `context` with the job names in ci.yml.

The export is a record, not a deployment: editing it changes nothing on GitHub, and a change made
in Settings > Rules is not in it until somebody re-exports. The check therefore pins ci.yml
against what was last exported, not against the live ruleset.

ci.yml is read as text, standard library only. A job's check name is its `name:`, or its id when it
has none; a `${{ matrix.<key> }}` in the name is expanded over that key's inline list in the job's
`strategy.matrix`. Any other expression is an error rather than a guess.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_RULESET = ROOT / '.github' / 'rulesets' / 'main.json'
DEFAULT_WORKFLOW = ROOT / '.github' / 'workflows' / 'ci.yml'

JOB_ID = re.compile(r'^  ([A-Za-z_][\w-]*):\s*(?:#.*)?$')
JOB_NAME = re.compile(r'^    name:\s*(.*?)\s*$')
MATRIX_LIST = re.compile(r'^\s+([A-Za-z_][\w-]*):\s*\[(.*)\]\s*(?:#.*)?$')
EXPRESSION = re.compile(r'\$\{\{\s*(.*?)\s*\}\}')


def _scalar(text: str) -> str:
    text = text.strip()
    if len(text) >= 2 and text[0] == text[-1] and text[0] in '"\'':
        return text[1:-1]
    return re.sub(r'\s+#.*$', '', text)


def job_check_names(workflow: str) -> set:
    """The check names the jobs of a workflow report, as a set."""
    jobs = {}  # job id -> list of that job's lines
    current = None
    in_jobs = False
    for line in workflow.splitlines():
        if re.match(r'^\S', line):
            in_jobs = line.startswith('jobs:')
            current = None
            continue
        if not in_jobs:
            continue
        match = JOB_ID.match(line)
        if match:
            current = match.group(1)
            jobs[current] = []
        elif current is not None:
            jobs[current].append(line)

    names = set()
    for job_id, lines in jobs.items():
        name = next((_scalar(m.group(1)) for m in map(JOB_NAME.match, lines) if m), job_id)
        names.update(_expand(job_id, name, lines))
    return names


def _expand(job_id: str, name: str, lines) -> set:
    expressions = EXPRESSION.findall(name)
    if not expressions:
        return {name}
    if len(expressions) != 1 or not expressions[0].startswith('matrix.'):
        raise ValueError(f"job '{job_id}': cannot resolve the check name {name!r}")
    key = expressions[0][len('matrix.'):]
    for line in lines:
        match = MATRIX_LIST.match(line)
        if match and match.group(1) == key:
            values = [_scalar(v) for v in match.group(2).split(',') if v.strip()]
            return {EXPRESSION.sub(lambda _: value, name) for value in values}
    raise ValueError(f"job '{job_id}': no inline matrix list for '{key}' in the check name {name!r}")


def required_contexts(ruleset: dict) -> list:
    rules = [r for r in ruleset.get('rules', []) if r.get('type') == 'required_status_checks']
    if not rules:
        raise ValueError('the ruleset has no required_status_checks rule')
    return [c['context'] for r in rules for c in r['parameters']['required_status_checks']]


def missing_checks(ruleset: dict, workflow: str) -> list:
    """Required contexts that no job of the workflow reports."""
    produced = job_check_names(workflow)
    return [context for context in required_contexts(ruleset) if context not in produced]


def main(argv) -> int:
    ruleset_path = Path(argv[1]) if len(argv) > 1 else DEFAULT_RULESET
    workflow_path = Path(argv[2]) if len(argv) > 2 else DEFAULT_WORKFLOW
    ruleset = json.loads(ruleset_path.read_text(encoding='utf-8'))
    missing = missing_checks(ruleset, workflow_path.read_text(encoding='utf-8'))
    for context in missing:
        print(f"the ruleset requires '{context}', but no job in {workflow_path.name} reports it", file=sys.stderr)
    if missing:
        return 1
    print(f'{len(required_contexts(ruleset))} required check(s) in {ruleset_path.name}, all reported by {workflow_path.name}.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
