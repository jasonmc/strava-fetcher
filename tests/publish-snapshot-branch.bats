#!/usr/bin/env bats

setup() {
	ROOT_DIR="$(cd "$(dirname "$BATS_TEST_FILENAME")/.." && pwd)"
	SCRIPT_PATH="${SCRIPT_PATH:-${ROOT_DIR}/scripts/publish-snapshot-branch.sh}"
	bare_repo="$(mktemp -d "/tmp/strava-fetcher-test.XXXXXX")"
	work_repo="$(mktemp -d "/tmp/strava-fetcher-test.XXXXXX")"

	git init --bare "${bare_repo}" >/dev/null
	git -C "${work_repo}" init -b master >/dev/null
	git -C "${work_repo}" config user.name "Test User"
	git -C "${work_repo}" config user.email "test@example.com"
	printf 'seed\n' >"${work_repo}/README.md"
	git -C "${work_repo}" add README.md
	git -C "${work_repo}" commit -m "seed" >/dev/null
	git -C "${work_repo}" remote add origin "${bare_repo}"
	git -C "${work_repo}" push -u origin master >/dev/null
}

teardown() {
	rm -rf "${bare_repo}" "${work_repo}"
}

run_publish() {
	(
		cd "${work_repo}"
		SNAPSHOT_BRANCH="strava-snapshots" bash "${SCRIPT_PATH}"
	)
}

@test "creates snapshot branch" {
	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"

	run run_publish
	[ "$status" -eq 0 ]

	run git -C "${bare_repo}" rev-parse --verify strava-snapshots
	[ "$status" -eq 0 ]
	[ -n "$output" ]

	run git -C "${bare_repo}" show strava-snapshots:strava-stats.json
	[ "$status" -eq 0 ]
	[ "$output" = '{"athlete_id":42}' ]
}

@test "skips commit when json is unchanged" {
	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"
	run_publish >/dev/null
	first_head="$(git -C "${bare_repo}" rev-parse strava-snapshots)"

	printf '{"athlete_id":42}\n' >"${work_repo}/strava-stats.json"
	run run_publish
	[ "$status" -eq 0 ]
	[[ "$output" == *"No meaningful JSON changes detected; skipping commit"* ]]

	second_head="$(git -C "${bare_repo}" rev-parse strava-snapshots)"
	[ "$first_head" = "$second_head" ]
}

@test "skips commit when only fetched_at changes" {
	cat >"${work_repo}/strava-stats.json" <<'EOF'
{"athlete_id":42,"fetched_at":"2026-04-09T00:00:00Z","weekly_ride_kilometers":[],"weekly_ride_hours":[],"annual_ride_totals":[]}
EOF
	run_publish >/dev/null
	first_head="$(git -C "${bare_repo}" rev-parse strava-snapshots)"

	cat >"${work_repo}/strava-stats.json" <<'EOF'
{"athlete_id":42,"fetched_at":"2026-04-10T00:00:00Z","weekly_ride_kilometers":[],"weekly_ride_hours":[],"annual_ride_totals":[]}
EOF
	run run_publish
	[ "$status" -eq 0 ]
	[[ "$output" == *"No meaningful JSON changes detected; skipping commit"* ]]

	second_head="$(git -C "${bare_repo}" rev-parse strava-snapshots)"
	[ "$first_head" = "$second_head" ]
}
