namespace StravaFetcher

open System
open System.Collections.Generic
open System.Globalization
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

type StravaApiException(message: string) =
    inherit Exception(message)

module Result =
    let mapError mapper result =
        match result with
        | Ok value -> Ok value
        | Error error -> Error(mapper error)

    let bind binder result =
        match result with
        | Ok value -> binder value
        | Error error -> Error error

    let map mapper result =
        match result with
        | Ok value -> Ok(mapper value)
        | Error error -> Error error

    let require predicate error value =
        if predicate value then Ok value else Error error

module Env =
    let getRequired name =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> Error($"Missing required environment variable: {name}")
        | value -> Ok value

    let require name =
        match getRequired name with
        | Ok value -> value
        | Error error -> raise (StravaApiException(error))

module Json =
    let options =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        )

    let deserializeResult<'T> context (content: string) =
        try
            match JsonSerializer.Deserialize<'T>(content, options) with
            | null -> Error($"Decoded JSON for {context} was null")
            | value -> Ok value
        with
        | ex ->
            Error($"Failed to decode {context} JSON: {ex.Message}")

    let deserializeRequired<'T> context (content: string) =
        match deserializeResult<'T> context content with
        | Ok value -> value
        | Error error -> raise (StravaApiException(error))

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

    let ensureJsonResponseResult (response: HttpResponseMessage) (body: string) =
        let mediaType =
            match response.Content.Headers.ContentType with
            | null -> ""
            | contentType ->
                match contentType.MediaType with
                | null -> ""
                | value -> value

        if not (String.IsNullOrWhiteSpace(mediaType)) && not (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) then
            Error($"Expected JSON response but received content-type '{mediaType}' with body: {trimBody body}")
        else
            Ok ()

    let ensureJsonResponse (response: HttpResponseMessage) (body: string) =
        match ensureJsonResponseResult response body with
        | Ok () -> ()
        | Error error -> raise (StravaApiException(error))

    let ensureSuccessResult (response: HttpResponseMessage) (body: string) =
        if not response.IsSuccessStatusCode then
            let requestUri =
                match response.RequestMessage with
                | null -> "<unknown-url>"
                | request ->
                    match request.RequestUri with
                    | null -> "<unknown-url>"
                    | uri -> uri.ToString()

            Error($"HTTP {(int response.StatusCode)} ({response.ReasonPhrase}) from {requestUri}: {trimBody body}")
        else
            Ok ()

    let ensureSuccess (response: HttpResponseMessage) (body: string) =
        match ensureSuccessResult response body with
        | Ok () -> ()
        | Error error -> raise (StravaApiException(error))

    let postFormResultAsync (url: string) (pairs: (string * string) list) =
        task {
            use content =
                new FormUrlEncodedContent(
                    pairs
                    |> Seq.map (fun (k, v) -> KeyValuePair<string, string>(k, v))
                )

            use! response = client.PostAsync(url, content)
            let! body = response.Content.ReadAsStringAsync()

            return
                ensureSuccessResult response body
                |> Result.bind (fun () -> ensureJsonResponseResult response body)
                |> Result.map (fun () -> body)
        }

    let postFormAsync (url: string) (pairs: (string * string) list) =
        task {
            let! result = postFormResultAsync url pairs

            return
                match result with
                | Ok body -> body
                | Error error -> raise (StravaApiException(error))
        }

    let getJsonResultAsync (url: string) (accessToken: string) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Get, url)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()

            return
                ensureSuccessResult response body
                |> Result.bind (fun () -> ensureJsonResponseResult response body)
                |> Result.map (fun () -> body)
        }

    let getJsonAsync (url: string) (accessToken: string) =
        task {
            let! result = getJsonResultAsync url accessToken

            return
                match result with
                | Ok body -> body
                | Error error -> raise (StravaApiException(error))
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

    let refreshTokenResultAsync clientId clientSecret refreshToken =
        task {
            let! bodyResult =
                Http.postFormResultAsync
                    $"{baseUrl}/oauth/token"
                    [ "client_id", clientId
                      "client_secret", clientSecret
                      "refresh_token", refreshToken
                      "grant_type", "refresh_token" ]

            return bodyResult |> Result.bind (Json.deserializeResult<TokenResponse> "token response")
        }

    let refreshTokenAsync clientId clientSecret refreshToken =
        task {
            let! result = refreshTokenResultAsync clientId clientSecret refreshToken

            return
                match result with
                | Ok value -> value
                | Error error -> raise (StravaApiException(error))
        }

    let getAthleteResultAsync accessToken =
        task {
            let! bodyResult = Http.getJsonResultAsync $"{baseUrl}/api/v3/athlete" accessToken
            return bodyResult |> Result.bind (Json.deserializeResult<Athlete> "athlete")
        }

    let getAthleteAsync accessToken =
        task {
            let! result = getAthleteResultAsync accessToken

            return
                match result with
                | Ok value -> value
                | Error error -> raise (StravaApiException(error))
        }

    let getStatsResultAsync accessToken athleteId =
        task {
            let! bodyResult = Http.getJsonResultAsync $"{baseUrl}/api/v3/athletes/{athleteId}/stats" accessToken
            return bodyResult |> Result.bind (Json.deserializeResult<AthleteStats> "athlete stats")
        }

    let getStatsAsync accessToken athleteId =
        task {
            let! result = getStatsResultAsync accessToken athleteId

            return
                match result with
                | Ok value -> value
                | Error error -> raise (StravaApiException(error))
        }

    let getActivitiesPageResultAsync accessToken page =
        task {
            let! bodyResult =
                Http.getJsonResultAsync $"{baseUrl}/api/v3/athlete/activities?per_page=200&page={page}" accessToken

            return bodyResult |> Result.bind (Json.deserializeResult<Activity array> $"activities page {page}")
        }

    let getActivitiesPageAsync accessToken page =
        task {
            let! result = getActivitiesPageResultAsync accessToken page

            return
                match result with
                | Ok value -> value
                | Error error -> raise (StravaApiException(error))
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
        { refreshToken: string -> string -> string -> Result<TokenResponse, string>
          getAthlete: string -> Result<Athlete, string>
          getStats: string -> int64 -> Result<AthleteStats, string>
          getActivitiesPage: string -> int -> Result<Activity array, string> }

    type internal ValidTokenResponse =
        { accessToken: string
          refreshToken: string }

    let internal liveDependencies =
        { refreshToken =
            fun clientId clientSecret refreshToken ->
                Strava.refreshTokenResultAsync clientId clientSecret refreshToken
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getAthlete =
            fun accessToken ->
                Strava.getAthleteResultAsync accessToken
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getStats =
            fun accessToken athleteId ->
                Strava.getStatsResultAsync accessToken athleteId
                |> Async.AwaitTask
                |> Async.RunSynchronously
          getActivitiesPage =
            fun accessToken page ->
                Strava.getActivitiesPageResultAsync accessToken page
                |> Async.AwaitTask
                |> Async.RunSynchronously }

    let private requireNonBlank error value =
        value
        |> Result.require (String.IsNullOrWhiteSpace >> not) error

    let private validateTokenResponse (tokenResponse: TokenResponse) =
        requireNonBlank "Token refresh response did not include an access_token" tokenResponse.access_token
        |> Result.bind (fun accessToken ->
            requireNonBlank "Token refresh response did not include a refresh_token" tokenResponse.refresh_token
            |> Result.map (fun refreshToken ->
                { accessToken = accessToken
                  refreshToken = refreshToken }))

    let private fetchAllActivities (getActivitiesPage: string -> int -> Result<Activity array, string>) (accessToken: string) =
        Seq.initInfinite ((+) 1)
        |> Seq.map (fun page -> getActivitiesPage accessToken page)
        |> Seq.scan
            (fun state next ->
                match state with
                | Error error -> Error error
                | Ok (true, activities) ->
                    match next with
                    | Error error -> Error error
                    | Ok pageActivities when pageActivities.Length = 0 -> Ok(false, activities)
                    | Ok pageActivities -> Ok(true, List.append activities (List.ofArray pageActivities))
                | Ok (false, activities) -> Ok(false, activities))
            (Ok(true, []))
        |> Seq.skip 1
        |> Seq.tryFind (function
            | Error _ -> true
            | Ok(continuePaging, _) -> not continuePaging)
        |> function
            | Some (Error error) -> Error error
            | Some (Ok (_, activities)) -> Ok activities
            | None -> Ok []

    let internal fetchNormalizedJsonResult (dependencies: FetchDependencies) clientId clientSecret refreshToken =
        dependencies.refreshToken clientId clientSecret refreshToken
        |> Result.bind validateTokenResponse
        |> Result.bind (fun (token: ValidTokenResponse) ->
            dependencies.getAthlete token.accessToken
            |> Result.bind (fun athlete ->
                dependencies.getStats token.accessToken athlete.id
                |> Result.bind (fun stats ->
                    fetchAllActivities dependencies.getActivitiesPage token.accessToken
                    |> Result.map (fun activities ->
                        let normalized = Normalize.build athlete stats activities
                        let json = JsonSerializer.Serialize(normalized, Json.options)
                        token.refreshToken, json))))

    let internal fetchNormalizedJson (dependencies: FetchDependencies) clientId clientSecret refreshToken =
        match fetchNormalizedJsonResult dependencies clientId clientSecret refreshToken with
        | Ok value -> value
        | Error error -> raise (StravaApiException(error))

    let fetchNormalizedJsonFromEnv () =
        Env.getRequired "STRAVA_CLIENT_ID"
        |> Result.bind (fun clientId ->
            Env.getRequired "STRAVA_CLIENT_SECRET"
            |> Result.bind (fun clientSecret ->
                Env.getRequired "STRAVA_REFRESH_TOKEN"
                |> Result.bind (fun refreshToken ->
                    fetchNormalizedJsonResult liveDependencies clientId clientSecret refreshToken)))
        |> function
            | Ok value -> value
            | Error error -> raise (StravaApiException(error))
