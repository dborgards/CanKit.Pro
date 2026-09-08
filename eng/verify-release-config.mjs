// Fails the build if .releaserc.json references a plugin or changelog preset that the pinned
// tooling in package.json cannot actually load.
//
// This is deliberately not a full `semantic-release --dry-run`: that talks to the GitHub API and
// needs a token with push rights, which a pull-request build (especially from a fork) does not
// have. The release workflow runs the real dry run on `main`, where the token is right. What this
// catches is the failure mode a PR *can* introduce: a typo'd plugin name, a plugin dropped from
// package.json, or a preset/tooling version combination that no longer resolves.

import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

const require = createRequire(import.meta.url);
const config = JSON.parse(readFileSync(new URL('../.releaserc.json', import.meta.url), 'utf8'));

const problems = [];

const check = (what, id) => {
  try {
    require.resolve(id);
    console.log(`ok       ${what} ${id}`);
  } catch {
    problems.push(`${what} ${id} cannot be resolved`);
    console.log(`MISSING  ${what} ${id}`);
  }
};

for (const entry of config.plugins ?? []) {
  const [name, options] = Array.isArray(entry) ? entry : [entry, {}];
  check('plugin', name);

  const preset = options?.preset;
  if (preset) check('preset', `conventional-changelog-${preset}`);
}

if (!Array.isArray(config.branches) || config.branches.length === 0) {
  problems.push('branches must list at least one release branch');
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in .releaserc.json:`);
  for (const p of problems) console.error(`  - ${p}`);
  process.exit(1);
}

console.log('\n.releaserc.json is loadable with the pinned tooling.');
