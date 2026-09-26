#!/usr/bin/env bash
#
# Checks that a GitHub merge-queue ref gets a FullSemVer the rest of CI can pass to
# `dotnet build -p:Version=` unchanged.
#
# The queue has never been enabled, so no `merge_group` run has ever exercised this. The ref
# GitHub will build is `gh-readonly-queue/main/pr-<n>-<sha>`, and a group of two pull requests —
# the shape behind #85 — is a chain of merge commits on that ref. Left unmatched, GitVersion
# 6.8.2 still exits 0 and names it `1.2.4-gh-readonly-queue-main-pr-<n>-<sha>.1+<commits>`.
# `GitVersion.yml`'s `merge-queue` branch is what turns that into `1.2.4-queue.<n>`, the same
# shape as `1.2.4-ci.<n>` on main and `1.2.4-pr.<n>` on a pull request, with no build metadata.
#
# Run from anywhere in the clone. `dotnet tool restore` must already have succeeded (the version
# job does that immediately above this script). The synthetic ref is built in a throwaway clone
# so this does not create branches in the checkout it was launched from.

set -euo pipefail

root=$(git rev-parse --show-toplevel)
tmp=$(mktemp -d)
cleanup() {
  rm -rf "$tmp"
}
trap cleanup EXIT

git -c advice.detachedHead=false clone --shared --quiet "$root" "$tmp/repo"
cp "$root/GitVersion.yml" "$tmp/repo/GitVersion.yml"

# `actions/checkout` on a pull request leaves the workspace detached, with `main` only as
# `origin/main`. `git clone` does not copy remote-tracking refs, so the throwaway clone would
# then have no `main` to build the queue ref on. Take the commit from whichever spelling the
# workspace has and check it out as a local `main` in the clone.
if git -C "$root" show-ref --verify --quiet refs/heads/main; then
  main_sha=$(git -C "$root" rev-parse refs/heads/main)
elif git -C "$root" show-ref --verify --quiet refs/remotes/origin/main; then
  main_sha=$(git -C "$root" rev-parse refs/remotes/origin/main)
else
  echo "cannot find main (refs/heads/main or refs/remotes/origin/main)" >&2
  exit 1
fi

cd "$tmp/repo"
git checkout -q -B main "$main_sha"
git checkout -q -B mq-sim-a main
echo a > mq-sim-a.txt
git add mq-sim-a.txt
git -c user.email="merge-queue-verify@example.com" -c user.name="merge-queue-verify" \
  commit -q -m "sim: first queued pull request"

git checkout -q -B mq-sim-b main
echo b > mq-sim-b.txt
git add mq-sim-b.txt
git -c user.email="merge-queue-verify@example.com" -c user.name="merge-queue-verify" \
  commit -q -m "sim: second queued pull request"

# Forty hex characters: the longest sha GitHub puts in the ref.
suffix=$(printf 'a%.0s' {1..40})
queue="gh-readonly-queue/main/pr-0-${suffix}"
git checkout -q -B "$queue" main
git -c user.email="merge-queue-verify@example.com" -c user.name="merge-queue-verify" \
  merge --no-ff mq-sim-a -q -m "Merge pull request #1"
git -c user.email="merge-queue-verify@example.com" -c user.name="merge-queue-verify" \
  merge --no-ff mq-sim-b -q -m "Merge pull request #2"

queue_sha=$(git rev-parse HEAD)

# GitVersion on a GitHub-hosted runner reads GITHUB_REF rather than the checked-out branch, and
# this script is itself running inside that environment on a pull request. Point the variables at
# the synthetic ref for the queue case, and strip them for main, or both calls version whichever
# pull request launched CI.
queue_full=$(
  GITHUB_ACTIONS=true \
  GITHUB_EVENT_NAME=merge_group \
  GITHUB_REF="refs/heads/${queue}" \
  GITHUB_REF_NAME="${queue}" \
  GITHUB_SHA="${queue_sha}" \
  GITHUB_WORKSPACE="${tmp}/repo" \
  dotnet gitversion "${tmp}/repo" /config "${root}/GitVersion.yml" /showvariable FullSemVer
)

git checkout -q -B main "$main_sha"
main_full=$(
  env -u GITHUB_ACTIONS -u GITHUB_REF -u GITHUB_REF_NAME -u GITHUB_SHA \
      -u GITHUB_HEAD_REF -u GITHUB_BASE_REF -u GITHUB_EVENT_NAME \
    dotnet gitversion "${tmp}/repo" /config "${root}/GitVersion.yml" /showvariable FullSemVer
)

queue_pattern='^[0-9]+\.[0-9]+\.[0-9]+-queue\.[0-9]+$'
main_pattern='^[0-9]+\.[0-9]+\.[0-9]+-ci\.[0-9]+$'

if [[ ! "$queue_full" =~ $queue_pattern ]]; then
  echo "merge-queue ref versioned as '${queue_full}', expected ${queue_pattern}" >&2
  exit 1
fi
if [[ ! "$main_full" =~ $main_pattern ]]; then
  echo "main versioned as '${main_full}', expected ${main_pattern}" >&2
  exit 1
fi

echo "merge-queue ref: ${queue_full}"
echo "main: ${main_full}"
