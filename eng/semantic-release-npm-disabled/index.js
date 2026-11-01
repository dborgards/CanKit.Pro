// semantic-release 25 depends on @semantic-release/npm, which installs a full npm CLI with its
// own bundled dependencies. .releaserc.json lists its plugins explicitly and npm is not one of
// them, so that plugin never runs here. package.json points the dependency at this directory to
// keep the bundled copies (undici, ip-address, minimatch, ...) out of the tree (#460).
//
// Loading it is always a mistake: fail loudly instead of releasing without the npm step.
throw new Error(
  '@semantic-release/npm is disabled in this repository: CanKit.Pro does not publish to npm. ' +
    'See eng/semantic-release-npm-disabled/index.js and issue #460.',
);
