// Fails the build if .releaserc.json references a plugin or changelog preset that the pinned
// tooling in package.json cannot actually load.
//
// This is deliberately not a full `semantic-release --dry-run`: that talks to the GitHub API and
// needs a token with push rights, which a pull-request build (especially from a fork) does not
// have. The release workflow runs the real dry run on `main`, where the token is right. What this
// catches is the failure mode a PR *can* introduce: a typo'd plugin name, a plugin dropped from
// package.json, or a preset/tooling version combination that no longer resolves.

import { readFileSync } from 'node:fs';

const config = JSON.parse(readFileSync(new URL('../.releaserc.json', import.meta.url), 'utf8'));

const problems = [];

// import.meta.resolve (not require.resolve): the semantic-release plugins that actually load
// these presets (@semantic-release/commit-analyzer, @semantic-release/release-notes-generator)
// do so via `import-from-esm`, which understands ESM-only packages. require.resolve does not --
// it fails on a package whose exports map has no "require" condition, even though the real
// pipeline loads it fine. conventional-changelog-conventionalcommits went ESM-only in 10.x.
const check = (what, id) => {
  try {
    import.meta.resolve(id);
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

// The temporary breaking -> minor override, and the guard that makes it temporary.
//
// docs/decisions/0001-versioning-and-api-stability.md puts the public API in a window where
// breaking changes are allowed, so that the four surface-level defects the review found can be
// fixed rather than shadowed. The override keeps a `!` commit from publishing 2.0.0 while that
// window is open. It has to go once 1.3.0 is out, or a real breaking change later becomes a
// silent minor -- the same class of accident that produced the 1.x line in the first place.
//
// The changelog rather than `git tag` is the source of truth here: the release-config job checks
// out shallow and without tags, and semantic-release writes the version into CHANGELOG.md as part
// of the release it is asking about anyway.
const analyzer = (config.plugins ?? [])
  .filter(Array.isArray)
  .find(([name]) => name === '@semantic-release/commit-analyzer')?.[1];

const breakingRule = (analyzer?.releaseRules ?? []).find((rule) => rule.breaking === true);

// Sortable ordinal for a major.minor pair; the patch level does not matter to this check.
const minorOrdinal = (major, minor) => Number(major) * 1000 + Number(minor);

if (breakingRule && breakingRule.release !== 'major') {
  const changelog = readFileSync(new URL('../CHANGELOG.md', import.meta.url), 'utf8');
  const released = [...changelog.matchAll(/^#{1,3} \[?(\d+)\.(\d+)\.(\d+)/gm)]
    .map(([, major, minor]) => minorOrdinal(major, minor));

  if (released.some((version) => version >= minorOrdinal(1, 3))) {
    problems.push(
      `releaseRules still maps breaking changes to "${breakingRule.release}", but 1.3.0 has been ` +
        'released. Restore { "breaking": true, "release": "major" } -- see ' +
        'docs/decisions/0001-versioning-and-api-stability.md',
    );
  } else {
    console.log(
      `ok       breaking -> ${breakingRule.release} (pre-1.3.0 window, see ` +
        'docs/decisions/0001-versioning-and-api-stability.md)',
    );
  }
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in .releaserc.json:`);
  for (const p of problems) console.error(`  - ${p}`);
  process.exit(1);
}

console.log('\n.releaserc.json is loadable with the pinned tooling.');
