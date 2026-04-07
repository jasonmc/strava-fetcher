namespace StravaFetcher

open System
open System.Threading.Tasks
open FsToolkit.ErrorHandling

module App =
    type internal FetchDependencies =
        { refreshToken: string -> string -> string -> Result<TokenResponse, FetchError>
          getAthlete: string -> Result<Athlete, FetchError>
          getStats: string -> int64 -> Result<AthleteStats, FetchError>
          getActivitiesPage: string -> int -> Result<Activity array, FetchError> }

    type internal ValidTokenResponse =
        { accessToken: string
          refreshToken: string }

    let private runTaskResult<'T> (task: Task<Result<'T, FetchError>>) =
        task |> Async.AwaitTask |> Async.RunSynchronously

    let internal liveDependencies =
        { refreshToken =
            fun clientId clientSecret refreshToken ->
                Strava.refreshTokenResultAsync clientId clientSecret refreshToken
                |> runTaskResult
          getAthlete = fun accessToken -> Strava.getAthleteResultAsync accessToken |> runTaskResult
          getStats = fun accessToken athleteId -> Strava.getStatsResultAsync accessToken athleteId |> runTaskResult
          getActivitiesPage =
            fun accessToken page -> Strava.getActivitiesPageResultAsync accessToken page |> runTaskResult }

    let private requireNonBlank error value =
        value |> ResultEx.require (String.IsNullOrWhiteSpace >> not) error

    let private validateTokenResponse (tokenResponse: TokenResponse) =
        result {
            let! accessToken = requireNonBlank (MissingTokenField "access_token") tokenResponse.access_token
            let! refreshToken = requireNonBlank (MissingTokenField "refresh_token") tokenResponse.refresh_token

            return
                { accessToken = accessToken
                  refreshToken = refreshToken }
        }

    let private fetchAllActivities
        (getActivitiesPage: string -> int -> Result<Activity array, FetchError>)
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
        result {
            let! tokenResponse = dependencies.refreshToken clientId clientSecret refreshToken
            let! token = validateTokenResponse tokenResponse
            let! athlete = dependencies.getAthlete token.accessToken
            let! stats = dependencies.getStats token.accessToken athlete.id
            let! activities = fetchAllActivities dependencies.getActivitiesPage token.accessToken
            let normalized = Normalize.build athlete stats activities
            return token.refreshToken, Json.serialize normalized
        }

    let internal fetchNormalizedJson (dependencies: FetchDependencies) clientId clientSecret refreshToken =
        match fetchNormalizedJsonResult dependencies clientId clientSecret refreshToken with
        | Ok value -> value
        | Error error -> raise (StravaApiException(FetchError.render error))

    let fetchNormalizedJsonFromEnv () =
        result {
            let! clientId = Env.getRequired "STRAVA_CLIENT_ID"
            let! clientSecret = Env.getRequired "STRAVA_CLIENT_SECRET"
            let! refreshToken = Env.getRequired "STRAVA_REFRESH_TOKEN"
            return! fetchNormalizedJsonResult liveDependencies clientId clientSecret refreshToken
        }
        |> function
            | Ok value -> value
            | Error error -> raise (StravaApiException(FetchError.render error))
