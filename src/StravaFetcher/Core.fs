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

    let teeError onError result =
        match result with
        | Ok _ -> result
        | Error error ->
            onError error
            result

    type Builder() =
        member _.Bind(result, binder) = bind binder result
        member _.Return(value) = Ok value
        member _.ReturnFrom(result) = result

    let builder = Builder()

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
        JsonSerializerOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)

    let deserializeResult<'T> context (content: string) =
        try
            match JsonSerializer.Deserialize<'T>(content, options) with
            | null -> Error($"Decoded JSON for {context} was null")
            | value -> Ok value
        with ex ->
            Error($"Failed to decode {context} JSON: {ex.Message}")

    let deserializeRequired<'T> context (content: string) =
        match deserializeResult<'T> context content with
        | Ok value -> value
        | Error error -> raise (StravaApiException(error))

    let serialize value =
        JsonSerializer.Serialize(value, options)

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

        if
            not (String.IsNullOrWhiteSpace(mediaType))
            && not (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        then
            Error($"Expected JSON response but received content-type '{mediaType}' with body: {trimBody body}")
        else
            Ok()

    let ensureJsonResponse (response: HttpResponseMessage) (body: string) =
        match ensureJsonResponseResult response body with
        | Ok() -> ()
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
            Ok()

    let ensureSuccess (response: HttpResponseMessage) (body: string) =
        match ensureSuccessResult response body with
        | Ok() -> ()
        | Error error -> raise (StravaApiException(error))

    let private unwrap result =
        result
        |> Result.teeError (StravaApiException >> raise)
        |> function
            | Ok value -> value
            | Error _ -> failwith "unreachable"

    let private sendResultAsync (requestFactory: unit -> HttpRequestMessage) =
        task {
            use request = requestFactory ()
            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()

            return
                Result.builder {
                    do! ensureSuccessResult response body
                    do! ensureJsonResponseResult response body
                    return body
                }
        }

    let postFormResultAsync (url: string) (pairs: (string * string) list) =
        sendResultAsync (fun () ->
            let request = new HttpRequestMessage(HttpMethod.Post, url)

            request.Content <-
                new FormUrlEncodedContent(pairs |> Seq.map (fun (k, v) -> KeyValuePair<string, string>(k, v)))

            request)

    let postFormAsync (url: string) (pairs: (string * string) list) =
        task {
            let! bodyResult = postFormResultAsync url pairs
            return unwrap bodyResult
        }

    let getJsonResultAsync (url: string) (accessToken: string) =
        sendResultAsync (fun () ->
            let request = new HttpRequestMessage(HttpMethod.Get, url)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
            request)

    let getJsonAsync (url: string) (accessToken: string) =
        task {
            let! bodyResult = getJsonResultAsync url accessToken
            return unwrap bodyResult
        }

type TokenResponse =
    { access_token: string
      refresh_token: string }

type Athlete = { id: int64 }

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

    let private unwrap result =
        result
        |> Result.teeError (StravaApiException >> raise)
        |> function
            | Ok value -> value
            | Error _ -> failwith "unreachable"

    let private decodeResultAsync<'T> context bodyResultTask =
        task {
            let! bodyResult = bodyResultTask
            return bodyResult |> Result.bind (Json.deserializeResult<'T> context)
        }

    let private getDecodedResultAsync<'T> context path accessToken =
        Http.getJsonResultAsync $"{baseUrl}{path}" accessToken
        |> decodeResultAsync<'T> context

    let private getDecodedAsync<'T> context path accessToken =
        task {
            let! valueResult = getDecodedResultAsync<'T> context path accessToken
            return unwrap valueResult
        }

    let refreshTokenResultAsync clientId clientSecret refreshToken =
        Http.postFormResultAsync
            $"{baseUrl}/oauth/token"
            [ "client_id", clientId
              "client_secret", clientSecret
              "refresh_token", refreshToken
              "grant_type", "refresh_token" ]
        |> decodeResultAsync<TokenResponse> "token response"

    let refreshTokenAsync clientId clientSecret refreshToken =
        task {
            let! result = refreshTokenResultAsync clientId clientSecret refreshToken
            return unwrap result
        }

    let getAthleteResultAsync accessToken =
        getDecodedResultAsync<Athlete> "athlete" "/api/v3/athlete" accessToken

    let getAthleteAsync accessToken =
        getDecodedAsync<Athlete> "athlete" "/api/v3/athlete" accessToken

    let getStatsResultAsync accessToken athleteId =
        getDecodedResultAsync<AthleteStats> "athlete stats" $"/api/v3/athletes/{athleteId}/stats" accessToken

    let getStatsAsync accessToken athleteId =
        getDecodedAsync<AthleteStats> "athlete stats" $"/api/v3/athletes/{athleteId}/stats" accessToken

    let getActivitiesPageResultAsync accessToken page =
        getDecodedResultAsync<Activity array>
            $"activities page {page}"
            $"/api/v3/athlete/activities?per_page=200&page={page}"
            accessToken

    let getActivitiesPageAsync accessToken page =
        task {
            let! result = getActivitiesPageResultAsync accessToken page
            return unwrap result
        }

module Normalize =
    let private metersToMiles meters = meters * 0.000621371

    let private secondsToHours seconds = float seconds / 3600.0

    let private isCycling (activity: Activity) =
        let knownCycling =
            set
                [ "Ride"
                  "VirtualRide"
                  "EBikeRide"
                  "MountainBikeRide"
                  "GravelRide"
                  "Handcycle"
                  "Velomobile"
                  "EMountainBikeRide" ]

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
            activities |> List.filter isCycling |> List.sortBy (fun a -> a.start_date)

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
        let inline run task =
            task |> Async.AwaitTask |> Async.RunSynchronously

        { refreshToken =
            fun clientId clientSecret refreshToken ->
                Strava.refreshTokenResultAsync clientId clientSecret refreshToken |> run
          getAthlete = fun accessToken -> Strava.getAthleteResultAsync accessToken |> run
          getStats = fun accessToken athleteId -> Strava.getStatsResultAsync accessToken athleteId |> run
          getActivitiesPage = fun accessToken page -> Strava.getActivitiesPageResultAsync accessToken page |> run }

    let private requireNonBlank error value =
        value |> Result.require (String.IsNullOrWhiteSpace >> not) error

    let private validateTokenResponse (tokenResponse: TokenResponse) =
        Result.builder {
            let! accessToken =
                requireNonBlank "Token refresh response did not include an access_token" tokenResponse.access_token

            let! refreshToken =
                requireNonBlank "Token refresh response did not include a refresh_token" tokenResponse.refresh_token

            return
                { accessToken = accessToken
                  refreshToken = refreshToken }
        }

    let private fetchAllActivities
        (getActivitiesPage: string -> int -> Result<Activity array, string>)
        (accessToken: string)
        =
        Seq.initInfinite ((+) 1)
        |> Seq.map (fun page -> getActivitiesPage accessToken page)
        |> Seq.scan
            (fun state next ->
                match state with
                | Error error -> Error error
                | Ok(true, activities) ->
                    match next with
                    | Error error -> Error error
                    | Ok pageActivities when pageActivities.Length = 0 -> Ok(false, activities)
                    | Ok pageActivities -> Ok(true, List.append activities (List.ofArray pageActivities))
                | Ok(false, activities) -> Ok(false, activities))
            (Ok(true, []))
        |> Seq.skip 1
        |> Seq.tryFind (function
            | Error _ -> true
            | Ok(continuePaging, _) -> not continuePaging)
        |> function
            | Some(Error error) -> Error error
            | Some(Ok(_, activities)) -> Ok activities
            | None -> Ok []

    let internal fetchNormalizedJsonResult (dependencies: FetchDependencies) clientId clientSecret refreshToken =
        Result.builder {
            let! token =
                dependencies.refreshToken clientId clientSecret refreshToken
                |> Result.bind validateTokenResponse

            let! athlete = dependencies.getAthlete token.accessToken
            let! stats = dependencies.getStats token.accessToken athlete.id
            let! activities = fetchAllActivities dependencies.getActivitiesPage token.accessToken
            let normalized = Normalize.build athlete stats activities
            return token.refreshToken, Json.serialize normalized
        }

    let internal fetchNormalizedJson (dependencies: FetchDependencies) clientId clientSecret refreshToken =
        match fetchNormalizedJsonResult dependencies clientId clientSecret refreshToken with
        | Ok value -> value
        | Error error -> raise (StravaApiException(error))

    let fetchNormalizedJsonFromEnv () =
        Result.builder {
            let! clientId = Env.getRequired "STRAVA_CLIENT_ID"
            let! clientSecret = Env.getRequired "STRAVA_CLIENT_SECRET"
            let! refreshToken = Env.getRequired "STRAVA_REFRESH_TOKEN"
            return! fetchNormalizedJsonResult liveDependencies clientId clientSecret refreshToken
        }
        |> function
            | Ok value -> value
            | Error error -> raise (StravaApiException(error))
