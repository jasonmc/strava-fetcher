namespace StravaFetcher

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

type StravaApiException(message: string) =
    inherit Exception(message)

module Env =
    let require name =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> raise (StravaApiException($"Missing required environment variable: {name}"))
        | value -> value

module Json =
    let options =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        )

    let deserializeRequired<'T> context (content: string) =
        try
            match JsonSerializer.Deserialize<'T>(content, options) with
            | null -> raise (StravaApiException($"Decoded JSON for {context} was null"))
            | value -> value
        with
        | :? StravaApiException ->
            reraise ()
        | ex ->
            raise (StravaApiException($"Failed to decode {context} JSON: {ex.Message}"))

module Http =
    let client =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(60.0)
        c.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
        c

    let private trimBody (body: string) =
        if String.IsNullOrWhiteSpace(body) then "<empty>"
        elif body.Length <= 400 then body
        else body.Substring(0, 400) + "..."

    let ensureJsonResponse (response: HttpResponseMessage) (body: string) =
        let mediaType =
            match response.Content.Headers.ContentType with
            | null -> ""
            | contentType ->
                match contentType.MediaType with
                | null -> ""
                | value -> value

        if not (String.IsNullOrWhiteSpace(mediaType)) && not (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) then
            raise (
                StravaApiException(
                    $"Expected JSON response but received content-type '{mediaType}' with body: {trimBody body}"
                )
            )

    let ensureSuccess (response: HttpResponseMessage) (body: string) =
        if not response.IsSuccessStatusCode then
            let requestUri =
                match response.RequestMessage with
                | null -> "<unknown-url>"
                | request ->
                    match request.RequestUri with
                    | null -> "<unknown-url>"
                    | uri -> uri.ToString()

            raise (
                StravaApiException(
                    $"HTTP {(int response.StatusCode)} ({response.ReasonPhrase}) from {requestUri}: {trimBody body}"
                )
            )

    let postFormAsync (url: string) (pairs: (string * string) list) =
        task {
            use content =
                new FormUrlEncodedContent(
                    pairs
                    |> Seq.map (fun (k, v) -> KeyValuePair<string, string>(k, v))
                )

            use! response = client.PostAsync(url, content)
            let! body = response.Content.ReadAsStringAsync()
            ensureSuccess response body
            ensureJsonResponse response body
            return body
        }

    let getJsonAsync (url: string) (accessToken: string) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            ensureSuccess response body
            ensureJsonResponse response body
            return body
        }

type TokenResponse =
    { access_token: string
      refresh_token: string }

type Athlete =
    { id: int64 }

type RideTotals =
    { count: int
      distance: float
      moving_time: int
      elapsed_time: int
      elevation_gain: float
      achievement_count: int }

type AthleteStats =
    { biggest_ride_distance: float
      biggest_climb_elevation_gain: float
      recent_ride_totals: RideTotals
      ytd_ride_totals: RideTotals
      all_ride_totals: RideTotals }

type Activity =
    { start_date: DateTimeOffset
      distance: float
      moving_time: int
      total_elevation_gain: float
      sport_type: string
      ``type``: string option }

module Strava =
    let private baseUrl = "https://www.strava.com"

    let refreshTokenAsync clientId clientSecret refreshToken =
        task {
            let! body =
                Http.postFormAsync
                    $"{baseUrl}/oauth/token"
                    [ "client_id", clientId
                      "client_secret", clientSecret
                      "refresh_token", refreshToken
                      "grant_type", "refresh_token" ]

            let parsed = Json.deserializeRequired<TokenResponse> "token response" body
            return parsed
        }

    let getAthleteAsync accessToken =
        task {
            let! body = Http.getJsonAsync $"{baseUrl}/api/v3/athlete" accessToken
            let parsed = Json.deserializeRequired<Athlete> "athlete" body
            return parsed
        }

    let getStatsAsync accessToken athleteId =
        task {
            let! body = Http.getJsonAsync $"{baseUrl}/api/v3/athletes/{athleteId}/stats" accessToken
            let parsed = Json.deserializeRequired<AthleteStats> "athlete stats" body
            return parsed
        }

    let getActivitiesPageAsync accessToken page =
        task {
            let! body =
                Http.getJsonAsync $"{baseUrl}/api/v3/athlete/activities?per_page=200&page={page}" accessToken

            let parsed = Json.deserializeRequired<Activity array> $"activities page {page}" body
            return parsed
        }

module Normalize =
    let private metersToMiles meters = meters * 0.000621371

    let private secondsToHours seconds = float seconds / 3600.0

    let private isCycling (activity: Activity) =
        let knownCycling =
            set [
                "Ride"
                "VirtualRide"
                "EBikeRide"
                "MountainBikeRide"
                "GravelRide"
                "Handcycle"
                "Velomobile"
                "EMountainBikeRide"
            ]

        knownCycling.Contains(activity.sport_type)
        || activity.sport_type.EndsWith("Ride", StringComparison.Ordinal)
        || (activity.``type`` |> Option.defaultValue "" = "Ride")

    let private weekStart (value: DateTimeOffset) =
        let day = int value.DayOfWeek
        let delta = (day + 6) % 7
        value.Date.AddDays(float -delta)

    let private round2 (value: float) =
        Math.Round(value, 2, MidpointRounding.AwayFromZero)

    let build athlete stats (activities: Activity list) =
        let cyclingActivities =
            activities
            |> List.filter isCycling
            |> List.sortBy (fun a -> a.start_date)

        let weeklyMiles =
            cyclingActivities
            |> List.groupBy (fun a -> weekStart a.start_date)
            |> List.sortBy fst
            |> List.map (fun (week, items) ->
                {| week_start = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   miles = items |> List.sumBy (fun a -> metersToMiles a.distance) |> round2 |})

        let weeklyHours =
            cyclingActivities
            |> List.groupBy (fun a -> weekStart a.start_date)
            |> List.sortBy fst
            |> List.map (fun (week, items) ->
                {| week_start = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                   hours = items |> List.sumBy (fun a -> secondsToHours a.moving_time) |> round2 |})

        let annualRideTotals =
            cyclingActivities
            |> List.groupBy (fun a -> a.start_date.Year)
            |> List.sortBy fst
            |> List.map (fun (year, items) ->
                {| year = year
                   rides = items.Length
                   miles = items |> List.sumBy (fun a -> metersToMiles a.distance) |> round2
                   hours = items |> List.sumBy (fun a -> secondsToHours a.moving_time) |> round2
                   elevation = items |> List.sumBy (fun a -> a.total_elevation_gain) |> round2 |})

        let compactActivities =
            cyclingActivities
            |> List.map (fun a ->
                {| start_date = a.start_date.ToString("O", CultureInfo.InvariantCulture)
                   activity_distance = round2 (metersToMiles a.distance)
                   activity_moving_time = a.moving_time
                   activity_elevation_gain = round2 a.total_elevation_gain
                   sport_type = a.sport_type |})

        {| athlete_id = athlete.id
           fetched_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
           biggest_ride_distance = round2 (metersToMiles stats.biggest_ride_distance)
           biggest_climb_elevation_gain = round2 stats.biggest_climb_elevation_gain
           recent_ride_totals = stats.recent_ride_totals
           ytd_ride_totals = stats.ytd_ride_totals
           all_ride_totals = stats.all_ride_totals
           weekly_ride_miles = weeklyMiles
           weekly_ride_hours = weeklyHours
           annual_ride_totals = annualRideTotals
           activities = compactActivities |}

module App =
    type internal FetchDependencies =
        { refreshToken: string -> string -> string -> TokenResponse
          getAthlete: string -> Athlete
          getStats: string -> int64 -> AthleteStats
          getActivitiesPage: string -> int -> Activity array }

    let internal liveDependencies =
        { refreshToken =
            fun clientId clientSecret refreshToken ->
                Strava.refreshTokenAsync clientId clientSecret refreshToken
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getAthlete =
            fun accessToken ->
                Strava.getAthleteAsync accessToken
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getStats =
            fun accessToken athleteId ->
                Strava.getStatsAsync accessToken athleteId
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getActivitiesPage =
            fun accessToken page ->
                Strava.getActivitiesPageAsync accessToken page
                |> Async.AwaitTask
                |> Async.RunSynchronously }

    let internal fetchNormalizedJson dependencies clientId clientSecret refreshToken =
        let tokenResponse = dependencies.refreshToken clientId clientSecret refreshToken

        if String.IsNullOrWhiteSpace(tokenResponse.access_token) then
            raise (StravaApiException("Token refresh response did not include an access_token"))

        if String.IsNullOrWhiteSpace(tokenResponse.refresh_token) then
            raise (StravaApiException("Token refresh response did not include a refresh_token"))

        let athlete = dependencies.getAthlete tokenResponse.access_token
        let stats = dependencies.getStats tokenResponse.access_token athlete.id

        let activities =
            Seq.initInfinite (fun index -> index + 1)
            |> Seq.map (fun page -> dependencies.getActivitiesPage tokenResponse.access_token page)
            |> Seq.takeWhile (fun pageActivities -> pageActivities.Length > 0)
            |> Seq.collect id
            |> Seq.toList

        let normalized = Normalize.build athlete stats activities
        let json = JsonSerializer.Serialize(normalized, Json.options)
        tokenResponse.refresh_token, json

    let fetchNormalizedJsonFromEnv () =
        let clientId = Env.require "STRAVA_CLIENT_ID"
        let clientSecret = Env.require "STRAVA_CLIENT_SECRET"
        let refreshToken = Env.require "STRAVA_REFRESH_TOKEN"
        fetchNormalizedJson liveDependencies clientId clientSecret refreshToken
