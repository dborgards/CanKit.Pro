// Prints the version semantic-release would publish from this commit, or nothing if there
// is no release. Used by the release workflow's `verify` job so it can pack at that version
// *before* the job that holds `id-token: write` starts.
//
// This is not a full `semantic-release --dry-run`. That loads the GitHub and git plugins,
// which check push permission and want a write token. The version is decided only by the
// commit analyser, so that is the only plugin invoked. No token, no pack, no push.

import { readFileSync } from 'node:fs';
import semanticRelease from 'semantic-release';

const config = JSON.parse(readFileSync(new URL('../.releaserc.json', import.meta.url), 'utf8'));

const versionPlugins = (config.plugins ?? []).filter((entry) => {
  const name = Array.isArray(entry) ? entry[0] : entry;
  return name === '@semantic-release/commit-analyzer';
});

const result = await semanticRelease(
  {
    dryRun: true,
    // GitHub Actions sets CI=true; without this the library demands a write token even though
    // nothing here publishes. The analyser only needs the git history.
    ci: false,
    branches: config.branches,
    tagFormat: config.tagFormat,
    plugins: versionPlugins,
  },
  // semantic-release writes its own logs to stdout by default. The workflow captures stdout
  // as the version, so the chatter has to go to stderr.
  { stdout: process.stderr, stderr: process.stderr },
);

process.stdout.write(result?.nextRelease?.version ?? '');
