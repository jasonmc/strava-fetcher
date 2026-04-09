#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_PATH="${SCRIPT_PATH:-${ROOT_DIR}/scripts/publish-snapshot-branch.sh}"

fail() {
	echo "FAIL: $*" >&2
	exit 1
}

assert_eq() {
	local expected="$1"
	local actual="$2"
	local message="$3"

	if [[ "${expected}" != "${actual}" ]]; then
		fail "${message}: expected '${expected}', got '${actual}'"
	fi
}

assert_contains() {
	local haystack="$1"
	local needle="$2"
	local message="$3"

	if [[ "${haystack}" != *"${needle}"* ]]; then
		fail "${message}: missing '${needle}'"
	fi
}

run_git() {
	local workdir="$1"
	shift
	git -C "${workdir}" "$@"
}

cleanup_dirs=()

cleanup() {
	local path

	for path in "${cleanup_dirs[@]}"; do
		rm -rf "${path}"
	done
}

trap cleanup EXIT

make_temp_dir() {
	local path
	path="$(mktemp -d "/tmp/strava-fetcher-test.XXXXXX")"
	cleanup_dirs+=("${path}")
	printf '%s\n' "${path}"
}

create_repo_with_origin() {
	local bare_repo
	local work_repo

	bare_repo="$(make_temp_dir)"
	work_repo="$(make_temp_dir)"

	git init --bare "${bare_repo}" >/dev/null
	git -C "${work_repo}" init -b master >/dev/null
	git -C "${work_repo}" config user.name "Test User"
	git -C "${work_repo}" config user.email "test@example.com"
	printf 'seed\n' >"${work_repo}/README.md"
	git -C "${work_repo}" add README.md
	git -C "${work_repo}" commit -m "seed" >/dev/null
	git -C "${work_repo}" remote add origin "${bare_repo}"
	git -C "${work_repo}" push -u origin master >/dev/null

	printf '%s\n%s\n' "${bare_repo}" "${work_repo}"
}

run_publish() {
	local work_repo="$1"

	(
		cd "${work_repo}"
		SNAPSHOT_BRANCH="strava-snapshots" bash "${SCRIPT_PATH}"
	)
}

test_creates_snapshot_branch() {
	local bare_repo
	local work_repo
	mapfile -t repos < <(create_repo_with_origin)
	bare_repo="${repos[0]}"
	work_repo="${repos[1]}"

	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"
	run_publish "${work_repo}" >/dev/null

	local branch_name
	local file_contents
	branch_name="$(run_git "${bare_repo}" rev-parse --verify strava-snapshots)"
	file_contents="$(run_git "${bare_repo}" show strava-snapshots:strava-stats.json)"

	[[ -n "${branch_name}" ]] || fail "snapshot branch should exist"
	assert_eq '{"athlete_id":42}' "${file_contents}" "snapshot file should match published JSON"
}

test_skips_commit_when_json_is_unchanged() {
	local bare_repo
	local work_repo
	local first_head
	local second_head
	local stdout

	mapfile -t repos < <(create_repo_with_origin)
	bare_repo="${repos[0]}"
	work_repo="${repos[1]}"

	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"
	run_publish "${work_repo}" >/dev/null
	first_head="$(run_git "${bare_repo}" rev-parse strava-snapshots)"

	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"
	stdout="$(run_publish "${work_repo}")"
	second_head="$(run_git "${bare_repo}" rev-parse strava-snapshots)"

	assert_eq "${first_head}" "${second_head}" "snapshot branch head should stay unchanged"
	assert_contains "${stdout}" "No JSON changes detected; skipping commit" "second run should skip commit"
}

test_creates_snapshot_branch
test_skips_commit_when_json_is_unchanged

echo "publish-snapshot-branch tests passed"
