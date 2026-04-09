#!/usr/bin/env bash

set -euo pipefail

SNAPSHOT_BRANCH="${SNAPSHOT_BRANCH:-strava-snapshots}"
SNAPSHOT_FILE="${SNAPSHOT_FILE:-strava-stats.json}"

snapshot_dir="$(mktemp -d /tmp/strava-snapshot-worktree.XXXXXX)"

cleanup() {
	git worktree remove --force "${snapshot_dir}" || true
}

trap cleanup EXIT

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"

if git ls-remote --exit-code --heads origin "${SNAPSHOT_BRANCH}" >/dev/null 2>&1; then
	git worktree add "${snapshot_dir}" --detach "origin/${SNAPSHOT_BRANCH}"
	git -C "${snapshot_dir}" switch -C "${SNAPSHOT_BRANCH}" --track "origin/${SNAPSHOT_BRANCH}"
else
	git worktree add "${snapshot_dir}" --detach HEAD
	git -C "${snapshot_dir}" switch --orphan "${SNAPSHOT_BRANCH}"
fi

find "${snapshot_dir}" -mindepth 1 -maxdepth 1 ! -name .git ! -name "${SNAPSHOT_FILE}" -exec rm -rf {} +
cp "${SNAPSHOT_FILE}" "${snapshot_dir}/${SNAPSHOT_FILE}"
git -C "${snapshot_dir}" add -A

if git -C "${snapshot_dir}" diff --cached --quiet; then
	echo "No JSON changes detected; skipping commit"
	exit 0
fi

git -C "${snapshot_dir}" commit -m "Update Strava stats snapshot"
git -C "${snapshot_dir}" push origin "HEAD:${SNAPSHOT_BRANCH}"
