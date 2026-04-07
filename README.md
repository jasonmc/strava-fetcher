# strava-fetcher

Small F#/.NET CLI plus a GitHub Actions workflow for fetching Strava ride data, normalizing it to JSON, and committing the snapshot back into this repository on a dedicated branch.

## What it does

- Reads `STRAVA_CLIENT_ID`, `STRAVA_CLIENT_SECRET`, and `STRAVA_REFRESH_TOKEN` from the environment.
- Refreshes the Strava access token through `https://www.strava.com/oauth/token`.
- Prints the latest rotated refresh token to `stderr` as `Latest STRAVA_REFRESH_TOKEN=...`.
- Fetches the current athlete, athlete stats, and all paginated athlete activities.
- Keeps only cycling activities.
- Writes normalized JSON to `stdout`.
- Separates the data-fetching CLI from the workflow logic that updates secrets and publishes the snapshot branch.

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

Schema summary:

```json
{
  "athlete_id": 123456,
  "fetched_at": "2026-04-07T15:30:00Z",
  "biggest_ride_distance": 128.4,
  "biggest_climb_elevation_gain": 10492.0,
  "recent_ride_totals": {
    "count": 12,
    "distance": 615432.5,
    "moving_time": 64231.0,
    "elapsed_time": 70102.0,
    "elevation_gain": 8234.2,
    "achievement_count": 3
  },
  "ytd_ride_totals": {
    "count": 84,
    "distance": 3251801.0,
    "moving_time": 260746.0,
    "elapsed_time": 281334.0,
    "elevation_gain": 44127.6,
    "achievement_count": null
  },
  "all_ride_totals": {
    "count": 1432,
    "distance": 48219012.4,
    "moving_time": 3897421.0,
    "elapsed_time": 4211805.0,
    "elevation_gain": 712554.9,
    "achievement_count": null
  },
  "weekly_ride_miles": [
    {
      "week_start": "2026-03-30",
      "miles": 87.6
    }
  ],
  "weekly_ride_hours": [
    {
      "week_start": "2026-03-30",
      "hours": 5.8
    }
  ],
  "annual_ride_totals": [
    {
      "year": 2026,
      "rides": 84,
      "miles": 2020.4,
      "hours": 72.4,
      "elevation": 44127.6
    }
  ]
}
```

Aggregation notes:

- `weekly_ride_miles` is grouped by ISO-style week start date (Monday) and stored as `[{ "week_start": "YYYY-MM-DD", "miles": ... }]`.
- `weekly_ride_hours` is grouped the same way and stores hours.
- `annual_ride_totals` is stored as `[{ "year": YYYY, "rides": ..., "miles": ..., "hours": ..., "elevation": ... }]`.
- Distances in normalized activity/weekly/annual output are converted to miles. Strava totals from the stats endpoint are preserved as returned by Strava.
- Raw activities are fetched privately so weekly and annual aggregates can be computed, but they are not published in the JSON output.

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

Optional source-repo variable:

- `SNAPSHOT_BRANCH`: branch name that will store `strava-stats.json` only. Defaults to `strava-snapshots`.

`AUTOMATION_TOKEN` should be a PAT or GitHub App token that can:

- write to this repo
- update Actions secrets in this repo, specifically `STRAVA_REFRESH_TOKEN`

Important behavior:

- The workflow preserves the latest rotated refresh token by updating the repo secret immediately after a successful fetch, before publishing to the snapshot branch. That prevents later scheduled runs from breaking even if the push step fails.
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

Project layout:

- `src/StravaFetcher`: library with the fetch/normalize logic
- `src/StravaFetcher.Cli`: thin CLI entrypoint
- `tests/StravaFetcher.Tests`: xUnit F# test project

The flake uses `nuget-packageslock2nix.lib` with checked-in lockfiles.
If package dependencies change, regenerate the lock files with:

```bash
dotnet restore src/StravaFetcher/StravaFetcher.fsproj --use-lock-file
dotnet restore src/StravaFetcher.Cli/StravaFetcher.Cli.fsproj --use-lock-file
dotnet restore tests/StravaFetcher.Tests/StravaFetcher.Tests.fsproj --use-lock-file
```

Run tests locally with:

```bash
dotnet test tests/StravaFetcher.Tests/StravaFetcher.Tests.fsproj
```
