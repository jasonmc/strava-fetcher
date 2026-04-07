namespace StravaFetcher.Tests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open StravaFetcher
open Xunit

module TestData =
    let rideTotals =
        { count = 1
          distance = 1609.34
          moving_time = 3600.0
          elapsed_time = 3600.0
          elevation_gain = 100.0
          achievement_count = Some 0 }

    let athlete = { id = 42L }

    let stats =
        { biggest_ride_distance = 10000.0
          biggest_climb_elevation_gain = 987.6
          recent_ride_totals = rideTotals
          ytd_ride_totals = rideTotals
          all_ride_totals = rideTotals }

    let activity startDate distance movingTime elevation sportType activityType =
        { start_date = DateTimeOffset.Parse(startDate)
          distance = distance
          moving_time = movingTime
          total_elevation_gain = elevation
          sport_type = sportType
          ``type`` = activityType }

    let requiredString (element: JsonElement) (propertyName: string) =
        match element.GetProperty(propertyName).GetString() with
        | null -> failwith $"Expected property '%s{propertyName}' to be a string"
        | value -> value

module NormalizeTests =
    open TestData

    let private parseNormalized athlete stats activities =
        let normalized = Normalize.build athlete stats activities
        JsonDocument.Parse(JsonSerializer.Serialize(normalized, Json.options))

    [<Fact>]
    let ``normalize filters and aggregates cycling rides`` () =
        let activities =
            [ activity "2025-12-29T10:00:00Z" 1609.34 3600 100.0 "Ride" (Some "Ride")
              activity "2026-01-03T10:00:00Z" 3218.68 1800 50.0 "VirtualRide" None
              activity "2026-01-05T10:00:00Z" 8046.7 7200 400.0 "GravelRide" None
              activity "2026-01-06T10:00:00Z" 5000.0 1500 30.0 "Run" None ]

        use document = parseNormalized athlete stats activities
        let root = document.RootElement

        Assert.Equal(42L, root.GetProperty("athlete_id").GetInt64())
        Assert.Equal(6.21, root.GetProperty("biggest_ride_distance").GetDouble(), 2)

        let weeklyMiles =
            root.GetProperty("weekly_ride_miles").EnumerateArray() |> Seq.toArray

        Assert.Equal(2, weeklyMiles.Length)
        Assert.Equal("2025-12-29", requiredString weeklyMiles[0] "week_start")
        Assert.Equal(3.0, weeklyMiles[0].GetProperty("miles").GetDouble(), 2)
        Assert.Equal("2026-01-05", requiredString weeklyMiles[1] "week_start")
        Assert.Equal(5.0, weeklyMiles[1].GetProperty("miles").GetDouble(), 2)

        let weeklyHours =
            root.GetProperty("weekly_ride_hours").EnumerateArray() |> Seq.toArray

        Assert.Equal(1.5, weeklyHours[0].GetProperty("hours").GetDouble(), 2)
        Assert.Equal(2.0, weeklyHours[1].GetProperty("hours").GetDouble(), 2)

        let annualTotals =
            root.GetProperty("annual_ride_totals").EnumerateArray() |> Seq.toArray

        Assert.Equal(2, annualTotals.Length)
        Assert.Equal(2025, annualTotals[0].GetProperty("year").GetInt32())
        Assert.Equal(1, annualTotals[0].GetProperty("rides").GetInt32())
        Assert.Equal(1.0, annualTotals[0].GetProperty("miles").GetDouble(), 2)
        Assert.Equal(2026, annualTotals[1].GetProperty("year").GetInt32())
        Assert.Equal(2, annualTotals[1].GetProperty("rides").GetInt32())
        Assert.Equal(7.0, annualTotals[1].GetProperty("miles").GetDouble(), 2)
        Assert.Equal(2.5, annualTotals[1].GetProperty("hours").GetDouble(), 2)
        Assert.Equal(450.0, annualTotals[1].GetProperty("elevation").GetDouble(), 2)

        let compactActivities =
            root.GetProperty("activities").EnumerateArray() |> Seq.toArray

        Assert.Equal(3, compactActivities.Length)
        Assert.Equal("Ride", requiredString compactActivities[0] "sport_type")
        Assert.DoesNotContain(compactActivities, fun activity -> requiredString activity "sport_type" = "Run")

    [<Fact>]
    let ``normalize returns empty aggregates for no cycling activities`` () =
        let activities = [ activity "2026-01-06T10:00:00Z" 5000.0 1500 30.0 "Run" None ]

        use document = parseNormalized athlete stats activities
        let root = document.RootElement

        Assert.Empty(root.GetProperty("weekly_ride_miles").EnumerateArray())
        Assert.Empty(root.GetProperty("weekly_ride_hours").EnumerateArray())
        Assert.Empty(root.GetProperty("annual_ride_totals").EnumerateArray())
        Assert.Empty(root.GetProperty("activities").EnumerateArray())

    [<Fact>]
    let ``normalize uses legacy type ride fallback`` () =
        let activities =
            [ activity "2026-01-07T10:00:00Z" 1609.34 1200 20.0 "Workout" (Some "Ride") ]

        use document = parseNormalized athlete stats activities
        let root = document.RootElement

        let compactActivities =
            root.GetProperty("activities").EnumerateArray() |> Seq.toArray

        Assert.Single(compactActivities) |> ignore
        Assert.Equal("Workout", requiredString compactActivities[0] "sport_type")

    [<Fact>]
    let ``normalize splits weeks on monday boundary`` () =
        let activities =
            [ activity "2026-01-11T10:00:00Z" 1609.34 1800 10.0 "Ride" None
              activity "2026-01-12T10:00:00Z" 3218.68 3600 20.0 "Ride" None ]

        use document = parseNormalized athlete stats activities

        let weeklyMiles =
            document.RootElement.GetProperty("weekly_ride_miles").EnumerateArray()
            |> Seq.toArray

        Assert.Equal(2, weeklyMiles.Length)
        Assert.Equal("2026-01-05", requiredString weeklyMiles[0] "week_start")
        Assert.Equal("2026-01-12", requiredString weeklyMiles[1] "week_start")

    [<Fact>]
    let ``normalize rounds annual totals across year boundary`` () =
        let activities =
            [ activity "2025-12-31T10:00:00Z" 1000.0 3599 12.345 "Ride" None
              activity "2026-01-01T10:00:00Z" 2000.0 3661 67.891 "Ride" None ]

        use document = parseNormalized athlete stats activities

        let annualTotals =
            document.RootElement.GetProperty("annual_ride_totals").EnumerateArray()
            |> Seq.toArray

        Assert.Equal(2, annualTotals.Length)
        Assert.Equal(0.62, annualTotals[0].GetProperty("miles").GetDouble(), 2)
        Assert.Equal(1.0, annualTotals[0].GetProperty("hours").GetDouble(), 2)
        Assert.Equal(12.35, annualTotals[0].GetProperty("elevation").GetDouble(), 2)
        Assert.Equal(1.24, annualTotals[1].GetProperty("miles").GetDouble(), 2)
        Assert.Equal(1.02, annualTotals[1].GetProperty("hours").GetDouble(), 2)
        Assert.Equal(67.89, annualTotals[1].GetProperty("elevation").GetDouble(), 2)

module FailureTests =
    open TestData

    let private jsonResponse statusCode reasonPhrase mediaType body (url: string) =
        let response = new HttpResponseMessage(statusCode)
        response.ReasonPhrase <- reasonPhrase
        response.RequestMessage <- new HttpRequestMessage(HttpMethod.Get, url)
        response.Content <- new StringContent(body)
        response.Content.Headers.ContentType <- MediaTypeHeaderValue(mediaType)
        response

    [<Fact>]
    let ``missing required env var fails clearly`` () =
        let name = "STRAVA_FETCHER_TEST_REQUIRED_ENV"
        let original = Environment.GetEnvironmentVariable(name)

        try
            Environment.SetEnvironmentVariable(name, null)
            let ex = Assert.Throws<StravaApiException>(fun () -> Env.require name |> ignore)
            Assert.Equal($"Missing required environment variable: {name}", ex.Message)
        finally
            Environment.SetEnvironmentVariable(name, original)

    [<Fact>]
    let ``ensureJsonResponse rejects html`` () =
        use response =
            jsonResponse HttpStatusCode.OK "OK" "text/html" "<html><body>nope</body></html>" "https://example.invalid"

        match Http.ensureJsonResponseResult response "<html><body>nope</body></html>" with
        | Ok() -> failwith "expected Error for html response"
        | Error(UnexpectedContentType(mediaType, bodyPreview)) ->
            Assert.Equal("text/html", mediaType)
            Assert.Contains("<html><body>nope</body></html>", bodyPreview)
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``ensureSuccess includes status and url`` () =
        use response =
            jsonResponse
                HttpStatusCode.BadRequest
                "Bad Request"
                "application/json"
                """{"message":"bad"}"""
                "https://example.invalid/fail"

        match Http.ensureSuccessResult response """{"message":"bad"}""" with
        | Ok() -> failwith "expected Error for bad response"
        | Error(HttpError(statusCode, reasonPhrase, url, bodyPreview)) ->
            Assert.Equal(400, statusCode)
            Assert.Equal("Bad Request", reasonPhrase)
            Assert.Equal("https://example.invalid/fail", url)
            Assert.Contains("bad", bodyPreview)
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``deserializeRequired fails on malformed json`` () =
        match Json.deserializeResult<TokenResponse> "token response" "{not-json}" with
        | Ok _ -> failwith "expected Error for malformed json"
        | Error(JsonDecodeError("token response", message)) -> Assert.Contains("invalid", message.ToLowerInvariant())
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``deserializeRequired fails on null json`` () =
        match Json.deserializeResult<TokenResponse> "token response" "null" with
        | Ok _ -> failwith "expected Error for null json"
        | Error(JsonDecodedNull "token response") -> ()
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``fetchNormalizedJson preserves rotated token and stops paginating on empty page`` () =
        let seenPages = ResizeArray<int>()

        let expectAccessToken accessToken =
            if accessToken <> "access-123" then
                failwith $"unexpected access token %s{accessToken}"

        let dependencies =
            { App.liveDependencies with
                refreshToken =
                    fun _ _ _ ->
                        Ok
                            { access_token = "access-123"
                              refresh_token = "refresh-456" }
                getAthlete =
                    fun accessToken ->
                        expectAccessToken accessToken
                        Ok athlete
                getStats =
                    fun accessToken athleteId ->
                        expectAccessToken accessToken

                        if athleteId <> 42L then
                            failwith $"unexpected athlete id %d{athleteId}"

                        Ok stats
                getActivitiesPage =
                    fun accessToken page ->
                        expectAccessToken accessToken
                        seenPages.Add(page)

                        match page with
                        | 1 ->
                            Ok
                                [| activity "2026-01-01T10:00:00Z" 1609.34 3600 10.0 "Ride" None
                                   activity "2026-01-02T10:00:00Z" 5000.0 1200 10.0 "Run" None |]
                        | 2 -> Ok [| activity "2026-01-03T10:00:00Z" 3218.68 1800 20.0 "Ride" None |]
                        | 3 -> Ok [||]
                        | _ -> failwith $"unexpected page %d{page}" }

        let latestRefreshToken, json =
            match App.fetchNormalizedJsonResult dependencies "client-id" "client-secret" "old-refresh-token" with
            | Ok value -> value
            | Error error -> failwith (FetchError.render error)

        Assert.Equal("refresh-456", latestRefreshToken)
        Assert.Equal<int[]>([| 1; 2; 3 |], seenPages |> Seq.toArray)

        use document = JsonDocument.Parse(json)

        let activities =
            document.RootElement.GetProperty("activities").EnumerateArray() |> Seq.toArray

        Assert.Equal(2, activities.Length)

    [<Fact>]
    let ``fetchNormalizedJson fails on missing rotated refresh token`` () =
        let dependencies =
            { App.liveDependencies with
                refreshToken =
                    fun _ _ _ ->
                        Ok
                            { access_token = "access-123"
                              refresh_token = "" } }

        let result =
            App.fetchNormalizedJsonResult dependencies "client-id" "client-secret" "old-refresh-token"

        match result with
        | Ok _ -> failwith "expected Error for missing rotated refresh token"
        | Error(MissingTokenField "refresh_token") -> ()
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``fetchNormalizedJsonResult fails on missing access token`` () =
        let dependencies =
            { App.liveDependencies with
                refreshToken =
                    fun _ _ _ ->
                        Ok
                            { access_token = ""
                              refresh_token = "refresh-456" } }

        let result =
            App.fetchNormalizedJsonResult dependencies "client-id" "client-secret" "old-refresh-token"

        match result with
        | Ok _ -> failwith "expected Error for missing access token"
        | Error(MissingTokenField "access_token") -> ()
        | Error error -> failwith $"unexpected error: {FetchError.render error}"

    [<Fact>]
    let ``getRequired returns error instead of throwing`` () =
        let name = "STRAVA_FETCHER_TEST_REQUIRED_ENV_RESULT"
        let original = Environment.GetEnvironmentVariable(name)

        try
            Environment.SetEnvironmentVariable(name, null)

            match Env.getRequired name with
            | Ok _ -> failwith "expected Error for missing env var"
            | Error(MissingEnv value) -> Assert.Equal(name, value)
            | Error error -> failwith $"unexpected error: {FetchError.render error}"
        finally
            Environment.SetEnvironmentVariable(name, original)

    [<Fact>]
    let ``fetchNormalizedJsonResult returns page fetch error`` () =
        let dependencies =
            { App.liveDependencies with
                refreshToken =
                    fun _ _ _ ->
                        Ok
                            { access_token = "access-123"
                              refresh_token = "refresh-456" }
                getAthlete = fun _ -> Ok athlete
                getStats = fun _ _ -> Ok stats
                getActivitiesPage =
                    fun _ page ->
                        match page with
                        | 1 -> Ok [| activity "2026-01-01T10:00:00Z" 1609.34 3600 10.0 "Ride" None |]
                        | 2 -> Error(Message "boom page 2")
                        | _ -> Ok [||] }

        match App.fetchNormalizedJsonResult dependencies "client-id" "client-secret" "old-refresh-token" with
        | Ok _ -> failwith "expected Error for page fetch failure"
        | Error(Message "boom page 2") -> ()
        | Error error -> failwith $"unexpected error: {FetchError.render error}"
