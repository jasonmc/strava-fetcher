#!/usr/bin/env bash

set -euo pipefail

SNAPSHOT_BRANCH="${SNAPSHOT_BRANCH:-strava-snapshots}"
SNAPSHOT_FILE="${SNAPSHOT_FILE:-strava-stats.json}"

snapshot_dir="$(mktemp -d /tmp/strava-snapshot-worktree.XXXXXX)"
normalized_new_snapshot="$(mktemp /tmp/strava-snapshot-normalized.XXXXXX.json)"
normalized_existing_snapshot=""

cleanup() {
	git worktree remove --force "${snapshot_dir}" || true
	rm -f "${normalized_new_snapshot}"
	rm -f "${normalized_existing_snapshot}"
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

jq --sort-keys 'del(.fetched_at)' "${SNAPSHOT_FILE}" >"${normalized_new_snapshot}"

find "${snapshot_dir}" -mindepth 1 -maxdepth 1 ! -name .git ! -name "${SNAPSHOT_FILE}" -exec rm -rf {} +

if [[ -f "${snapshot_dir}/${SNAPSHOT_FILE}" ]]; then
	normalized_existing_snapshot="$(mktemp /tmp/strava-snapshot-existing.XXXXXX.json)"
	jq --sort-keys 'del(.fetched_at)' "${snapshot_dir}/${SNAPSHOT_FILE}" >"${normalized_existing_snapshot}"

	if cmp -s "${normalized_new_snapshot}" "${normalized_existing_snapshot}"; then
		echo "No meaningful JSON changes detected; skipping commit"
		exit 0
	fi
fi

cp "${SNAPSHOT_FILE}" "${snapshot_dir}/${SNAPSHOT_FILE}"
git -C "${snapshot_dir}" add -A

if git -C "${snapshot_dir}" diff --cached --quiet; then
	echo "No JSON changes detected; skipping commit"
	exit 0
fi

git -C "${snapshot_dir}" commit -m "Update Strava stats snapshot"
git -C "${snapshot_dir}" push origin "HEAD:${SNAPSHOT_BRANCH}"
