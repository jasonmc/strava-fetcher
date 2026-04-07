# strava-fetcher

Small F#/.NET CLI plus a GitHub Actions workflow for fetching Strava ride data, normalizing it to JSON, and publishing the snapshot into a separate repository.

## What it does

- Reads `STRAVA_CLIENT_ID`, `STRAVA_CLIENT_SECRET`, and `STRAVA_REFRESH_TOKEN` from the environment.
- Refreshes the Strava access token through `https://www.strava.com/oauth/token`.
- Prints the latest rotated refresh token to `stderr` as `Latest STRAVA_REFRESH_TOKEN=...`.
- Fetches the current athlete, athlete stats, and all paginated athlete activities.
- Keeps only cycling activities.
- Writes normalized JSON to `stdout`.
- Separates the data-fetching CLI from the workflow logic that updates secrets and pushes to the canonical data repo.

## Output shape

The CLI emits JSON with these top-level keys:

- `athlete_id`
- `fetched_at`
- `biggest_ride_distance`
- `biggest_climb_elevation_gain`
- `recent_ride_totals`
- `ytd_ride_totals`
- `all_ride_totals`
- `weekly_ride_miles`
- `weekly_ride_hours`
- `annual_ride_totals`
- `activities`

Activity entries keep only:

- `start_date`
- `activity_distance`
- `activity_moving_time`
- `activity_elevation_gain`
- `sport_type`

Aggregation notes:

- `weekly_ride_miles` is grouped by ISO-style week start date (Monday) and stored as `[{ "week_start": "YYYY-MM-DD", "miles": ... }]`.
- `weekly_ride_hours` is grouped the same way and stores hours.
- `annual_ride_totals` is stored as `[{ "year": YYYY, "rides": ..., "miles": ..., "hours": ..., "elevation": ... }]`.
- Distances in normalized activity/weekly/annual output are converted to miles. Strava totals from the stats endpoint are preserved as returned by Strava.

## Local run

Required environment variables:

- `STRAVA_CLIENT_ID`
- `STRAVA_CLIENT_SECRET`
- `STRAVA_REFRESH_TOKEN`

Local testing example:

```bash
STRAVA_CLIENT_ID=... \
STRAVA_CLIENT_SECRET=... \
STRAVA_REFRESH_TOKEN=... \
nix run . > strava-stats.json
```

If you prefer the dev shell first:

```bash
nix develop
nix run .
```

Failure behavior:

- Missing env vars fail clearly.
- HTTP errors fail clearly with status and response snippet.
- JSON decode failures fail clearly with endpoint context.
- The CLI rejects obvious non-JSON/HTML responses.

## GitHub Actions setup

Required source-repo secrets:

- `STRAVA_CLIENT_ID`
- `STRAVA_CLIENT_SECRET`
- `STRAVA_REFRESH_TOKEN`
- `AUTOMATION_TOKEN`

Required source-repo variable:

- `TARGET_REPO`: `owner/repo` for the separate canonical data repository that will store only `strava-stats.json`

`AUTOMATION_TOKEN` should be a PAT or GitHub App token that can:

- write to the target repo
- update Actions secrets in the source repo, specifically `STRAVA_REFRESH_TOKEN`

Important behavior:

- The workflow preserves the latest rotated refresh token by updating the source repo secret immediately after a successful fetch, before publishing to the target repo. That prevents later scheduled runs from breaking even if the push step fails.
- The workflow commits only when `strava-stats.json` changed.
- The workflow does not force-push.
- The workflow fails if `git push` fails.

Example workflow file:

- [`.github/workflows/refresh-strava.yml`](/Users/jason/code/strava-fetcher/.github/workflows/refresh-strava.yml)

## Nix packaging

The flake builds the CLI with `buildDotnetModule` and exposes:

- `nix run .`
- `nix build .`
- `nix develop`

The flake uses `nuget-packageslock2nix.lib` with checked-in `packages.lock.json`.
If package dependencies change, regenerate the lock file with:

```bash
dotnet restore
```
