#!/usr/bin/env python3
"""Apply .github/labels.yml to the repository's labels, and prune what the manifest dropped.

Replaces micnncim/action-label-syncer, which is unmaintained and ran with `issues: write`. The
work it did is three `gh label` calls in a loop; carrying a third-party action with write access
to do that was the wrong trade.

`gh` reads the repository and the token from the environment (GH_REPO / GH_TOKEN), so this makes
no assumptions about which checkout it is running in.
"""

import json
import subprocess
import sys

import yaml


def gh(*args: str) -> str:
    result = subprocess.run(
        ('gh', *args), capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise SystemExit(f'gh {" ".join(args)} failed:\n{result.stderr.strip()}')
    return result.stdout


def main(manifest_path: str) -> int:
    with open(manifest_path, encoding='utf-8') as handle:
        manifest = yaml.safe_load(handle)

    desired = {}
    for entry in manifest:
        name = entry['name']
        if name.casefold() in desired:
            raise SystemExit(f'{manifest_path}: "{name}" is listed twice')
        # GitHub stores colours without the leading '#', which is also how `gh label list`
        # reports them; accept either spelling in the manifest.
        desired[name.casefold()] = {
            'name': name,
            'color': str(entry['color']).lstrip('#').lower(),
            'description': entry.get('description', '') or '',
        }

    existing = {
        label['name'].casefold(): label
        for label in json.loads(
            gh('label', 'list', '--json', 'name,color,description', '--limit', '200'))
    }

    created = updated = deleted = 0

    for key, want in desired.items():
        have = existing.get(key)
        if have is None:
            gh('label', 'create', want['name'],
               '--color', want['color'], '--description', want['description'])
            print(f'created  {want["name"]}')
            created += 1
            continue

        if (have['color'].lower() == want['color']
                and (have['description'] or '') == want['description']
                and have['name'] == want['name']):
            continue

        # `--name` renames; passing it unconditionally also fixes a case-only difference.
        gh('label', 'edit', have['name'], '--name', want['name'],
           '--color', want['color'], '--description', want['description'])
        print(f'updated  {want["name"]}')
        updated += 1

    # Prune, so the manifest stays the single source of truth rather than drifting from the UI.
    for key, have in existing.items():
        if key not in desired:
            gh('label', 'delete', have['name'], '--yes')
            print(f'deleted  {have["name"]}')
            deleted += 1

    print(f'\n{created} created, {updated} updated, {deleted} deleted, '
          f'{len(desired) - created - updated} unchanged')
    return 0


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: sync-labels.py <path to labels.yml>')
    sys.exit(main(sys.argv[1]))
