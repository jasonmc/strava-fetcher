namespace StravaFetcher

open FsToolkit.ErrorHandling

module Strava =
    let private baseUrl = "https://www.strava.com"

    let private unwrap result =
        result
        |> ResultEx.teeError (FetchError.render >> StravaApiException >> raise)
        |> function
            | Ok value -> value
            | Error _ -> failwith "unreachable"

    let private decodeResultAsync<'T> context bodyResultTask =
        task {
            let! bodyResult = bodyResultTask

            return
                result {
                    let! body = bodyResult
                    return! Json.deserializeResult<'T> context body
                }
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
