// Fails the build if the pinned tooling cannot load the configured plugins/presets, preserve
// release decisions, or render the configured visible/hidden/breaking release-note entries.
//
// This is deliberately not a full `semantic-release --dry-run`: that talks to the GitHub API and
// needs a token with push rights, which a pull-request build (especially from a fork) does not
// have. The release workflow runs the real dry run on `main`, where the token is right. This check
// invokes the analyser and notes generator with synthetic commits, and the GitHub publisher
// with an injected API stub: no network, push, tag or publication. Rendering catches incompatible
// writer/preset combinations that still resolve successfully (for example preset 10 with writer 8, #14).

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

  // package.json replaces @semantic-release/npm with a placeholder that throws when loaded, so
  // the npm CLI it bundles stays out of the tree (#460). It resolves fine, so say it here.
  if (name === '@semantic-release/npm') {
    problems.push(`plugin ${name} is disabled in this repository (see eng/semantic-release-npm-disabled)`);
    console.log(`DISABLED plugin ${name}`);
    continue;
  }

  check('plugin', name);

  const preset = options?.preset;
  if (preset) check('preset', `conventional-changelog-${preset}`);
}

if (!Array.isArray(config.branches) || config.branches.length === 0) {
  problems.push('branches must list at least one release branch');
}

// What a Dependabot commit publishes (#257). A shipped dependency moving is a patch; the
// `test-tooling` group (.github/dependabot.yml) tests nothing that ships, and the `repo-tooling`
// group builds and documents without shipping either, so neither publishes anything -- the rule
// tells them apart by the group's name in the subject. Run through the analyser
// itself, with the configured rules, because the matching is not obvious: a later `release: false`
// rule beats an earlier match, a glob negation lives in the subject, and a typo in either passes
// every resolvability check above while releasing nine packages for a coverlet bump.
const analyzerOptions = (config.plugins ?? [])
  .filter(Array.isArray)
  .find(([name]) => name === '@semantic-release/commit-analyzer')?.[1];

if (analyzerOptions) {
  const { analyzeCommits } = await import('@semantic-release/commit-analyzer');
  const expectRelease = async (subject, expected) => {
    const commit = { message: subject, subject, body: '', hash: '0' };
    const actual = await analyzeCommits(analyzerOptions, {
      commits: [commit],
      logger: { log() {} },
      cwd: process.cwd(),
    });
    if (actual !== expected) {
      problems.push(`"${subject}" would publish ${JSON.stringify(actual)}, expected ${JSON.stringify(expected)}`);
    } else {
      console.log(`ok       "${subject}" -> ${JSON.stringify(expected)}`);
    }
  };
  await expectRelease('build(deps): Bump the test-tooling group with 1 update', null);
  await expectRelease('build(deps): Bump the test-tooling group across 1 directory with 2 updates', null);
  await expectRelease('build(deps): Bump the test-tooling-security group with 1 update', null);
  await expectRelease('build(deps): Bump the repo-tooling group with 1 update', null);
  await expectRelease('build(deps): Bump the repo-tooling-security group with 1 update', null);
  await expectRelease('build(deps): Bump EdsDcfNet from 1.14.0 to 1.15.0', 'patch');
  await expectRelease('chore(deps): Bump the semantic-release group with 3 updates', null);
}

// Resolution and version analysis alone do not exercise the writer. Use the actual configured
// generator and preset, including presetConfig, so both a writer incompatibility and changes to
// hidden commit types fail on the PR that introduces them. Markers are unique subjects rather
// than a snapshot of formatting, generated dates or heading names.
const notesEntry = (config.plugins ?? [])
  .map((entry) => Array.isArray(entry) ? entry : [entry, {}])
  .find(([name]) => name === '@semantic-release/release-notes-generator');

if (!notesEntry) {
  problems.push('the configured release-notes generator is missing');
} else {
  const fixtures = [
    ['feat', 'release-check-visible-feature', true],
    ['fix', 'release-check-visible-fix', true],
    ['perf', 'release-check-visible-performance', true],
    ['revert', 'release-check-visible-revert', true],
    ['build', 'release-check-visible-build', true],
    ['docs', 'release-check-visible-documentation', true],
    ['refactor', 'release-check-visible-refactoring', true],
    ['test', 'release-check-hidden-test', false],
    ['ci', 'release-check-hidden-ci', false],
    ['chore', 'release-check-hidden-chore', false],
    ['style', 'release-check-hidden-style', false],
    ['chore!', 'release-check-breaking-header', true],
    ['test', 'release-check-breaking-hidden-type', true, '\n\nBREAKING CHANGE: release-check-breaking-footer'],
  ];
  const commits = fixtures.map(([type, marker, , footer = ''], index) => ({
    message: `${type}: ${marker}${footer}`,
    subject: `${type}: ${marker}`,
    hash: String(index + 1).padStart(40, '0'),
  }));
  try {
    const { generateNotes } = await import('@semantic-release/release-notes-generator');
    const notes = await generateNotes(notesEntry[1], {
      cwd: process.cwd(),
      env: {},
      commits,
      logger: { log() {} },
      options: { repositoryUrl: 'https://github.com/dborgards/CanKit.Pro' },
      lastRelease: { version: '1.3.0', gitTag: 'v1.3.0' },
      nextRelease: { version: '1.4.0', gitTag: 'v1.4.0' },
    });
    for (const [, marker, visible] of fixtures) {
      if (notes.includes(marker) !== visible) {
        problems.push(`release notes must ${visible ? 'include' : 'hide'} ${marker}`);
      } else {
        console.log(`ok       release notes ${visible ? 'include' : 'hide'} ${marker}`);
      }
    }
    if (!notes.includes('release-check-breaking-footer')) {
      problems.push('release notes lost the BREAKING CHANGE footer');
    }
  } catch (error) {
    problems.push(`release notes cannot be rendered: ${error instanceof Error ? error.message : String(error)}`);
  }
}

// A successful dry run still skips GitHub publication. Exercise the pinned publisher's actual
// template rendering against a stub API before any NuGet push can happen (#346). Keep normal
// notes intact, and link to the complete, tagged changelog when GitHub's 125000-character body
// limit would be at risk. The full notes must remain available to the changelog/git plugins.
const githubOptions = (config.plugins ?? [])
  .filter(Array.isArray)
  .find(([name]) => name === '@semantic-release/github')?.[1];

if (!githubOptions) {
  problems.push('the configured GitHub release publisher is missing');
} else {
  try {
    const { publish } = await import('@semantic-release/github');
    const changelogAsset = githubOptions.assets?.some((asset) => asset?.path === 'CHANGELOG.md');
    if (!changelogAsset) problems.push('GitHub release assets must include the complete CHANGELOG.md');
    for (const size of [0, 120000, 120001, 125000, 125001, 250000]) {
      const notes = 'x'.repeat(size);
      let request;
      class OfflineOctokit {
        async request(route, parameters) {
          if (route === 'GET /repos/{owner}/{repo}') {
            return { data: {
              permissions: { push: true },
              clone_url: 'https://github.com/dborgards/CanKit.Pro.git',
            } };
          }
          if (route !== 'POST /repos/{owner}/{repo}/releases') {
            throw new Error(`unexpected GitHub operation: ${route}`);
          }
          request = parameters;
          return { data: { id: 1, html_url: 'https://example.invalid/release' } };
        }
      }
      const nextRelease = { version: '9.8.7', gitTag: 'v9.8.7', notes };
      await publish({ ...githubOptions, assets: [] }, {
        cwd: process.cwd(),
        env: { GITHUB_TOKEN: 'offline-placeholder' },
        options: { repositoryUrl: 'https://github.com/dborgards/CanKit.Pro' },
        branch: { name: 'main', type: 'release', main: true },
        nextRelease,
        logger: { log() {} },
      }, { Octokit: OfflineOctokit });
      if (typeof request?.body !== 'string' || request.body.length > 125000) {
        problems.push(`GitHub release body exceeds its limit for ${size} characters of notes`);
      } else if (size <= 120000 ? request.body !== notes
        : !request.body.includes('https://github.com/dborgards/CanKit.Pro/blob/v9.8.7/CHANGELOG.md')) {
        problems.push(`GitHub release body must preserve notes or link to their tagged changelog (${size})`);
      } else if (nextRelease.notes !== notes) {
        problems.push('GitHub release rendering must not shorten the full changelog notes');
      } else {
        console.log(`ok       GitHub release body fits for ${size} characters of notes`);
      }
    }
  } catch (error) {
    problems.push(`GitHub release body cannot be rendered: ${error instanceof Error ? error.message : String(error)}`);
  }
}

// The temporary breaking -> minor override, and the guards that make it temporary.
//
// docs/decisions/0001-versioning-and-api-stability.md puts the public API in a window where
// breaking changes are allowed, so that the four surface-level defects the review found can be
// fixed rather than shadowed. The override keeps a `!` commit from publishing 2.0.0 while that
// window is open. It has to go once 1.3.0 is out, or a real breaking change later becomes a
// silent minor -- the same class of accident that produced the 1.x line in the first place.
//
// The changelog rather than `git tag` is the source of truth: the release-config job checks out
// shallow and without tags, and semantic-release writes the version into CHANGELOG.md as part of
// the release it is asking about anyway.
const ADR = 'docs/decisions/0001-versioning-and-api-stability.md';

// Sortable ordinal for a major.minor pair; the patch level does not matter to these checks.
const minorOrdinal = (major, minor) => Number(major) * 1000 + Number(minor);
const FIRST_STABLE = minorOrdinal(1, 3);

const changelog = readFileSync(new URL('../CHANGELOG.md', import.meta.url), 'utf8');
const windowClosed = [...changelog.matchAll(/^#{1,3} \[?(\d+)\.(\d+)\.(\d+)/gm)]
  .map(([, major, minor]) => minorOrdinal(major, minor))
  .some((version) => version >= FIRST_STABLE);

const analyzer = (config.plugins ?? [])
  .filter(Array.isArray)
  .find(([name]) => name === '@semantic-release/commit-analyzer')?.[1];

// `undefined` when no rule matches `breaking`, in which case the conventionalcommits preset's own
// default (major) applies. That is right after 1.3.0 and wrong before it, so the two branches
// below treat the missing rule differently rather than skipping the check.
const breakingRelease = (analyzer?.releaseRules ?? [])
  .find((rule) => rule.breaking === true)?.release;

if (windowClosed) {
  if (breakingRelease !== undefined && breakingRelease !== 'major') {
    problems.push(
      `releaseRules maps breaking changes to "${breakingRelease}", but 1.3.0 has been released. ` +
        `Restore { "breaking": true, "release": "major" } -- see ${ADR}`,
    );
  } else {
    console.log('ok       breaking -> major (1.3.0 is out, the ADR window is closed)');
  }
} else if (breakingRelease !== 'minor') {
  // Not just "anything but major": "patch" or false would let a breaking change ship under a
  // bump that hides it, which the override exists to prevent as much as 2.0.0 does.
  problems.push(
    `releaseRules must map breaking changes to exactly "minor" until 1.3.0 is released, found ` +
      `${breakingRelease === undefined ? 'no rule for `breaking`' : `"${breakingRelease}"`} ` +
      `-- see ${ADR}`,
  );
} else {
  console.log(`ok       breaking -> minor (pre-1.3.0 window, see ${ADR})`);
}

// Optional argument: the version semantic-release is about to publish. Wired up as the exec
// plugin's verifyReleaseCmd, so this runs inside the release itself, where the number is known.
//
// The ADR rules out intermediate releases before the window closes -- otherwise a batch of plain
// `fix:` commits publishes 1.2.4 and quietly contradicts "1.3.0 is the first stable release",
// which the breaking -> minor override alone does nothing about.
const requestedVersion = process.argv[2];

if (requestedVersion) {
  const parsed = /^(\d+)\.(\d+)\.(\d+)/.exec(requestedVersion);

  if (!parsed) {
    problems.push(`cannot parse the requested release version "${requestedVersion}"`);
  } else if (!windowClosed && requestedVersion !== '1.3.0') {
    // Exactly 1.3.0, not "1.3.0 or above". The ADR says 1.3.0 is the first release this project
    // makes; 1.4.0 or 2.0.0 arriving before it would satisfy a >= check while breaking the
    // statement the check exists to defend. Whatever produced such a number -- a stray tag, a
    // mis-set override -- is worth stopping for.
    problems.push(
      `refusing to release ${requestedVersion}: 1.3.0 is the first release this project makes, ` +
        `and no release happens before the checklist in ${ADR} is done`,
    );
  } else {
    console.log(`ok       releasing ${requestedVersion}`);
  }
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s) in .releaserc.json:`);
  for (const p of problems) console.error(`  - ${p}`);
  process.exit(1);
}

console.log('\n.releaserc.json loads, analyses commits and renders release notes with the pinned tooling.');
