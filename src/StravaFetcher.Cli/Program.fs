open System
open System.Text
open StravaFetcher

[<EntryPoint>]
let main _ =
    try
        let latestRefreshToken, json = App.fetchNormalizedJsonFromEnv ()
        eprintfn $"Latest STRAVA_REFRESH_TOKEN=%s{latestRefreshToken}"
        Console.OutputEncoding <- Encoding.UTF8
        printfn "%s" json
        0
    with
    | :? StravaApiException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        eprintfn "Unhandled error: %s" ex.Message
        1
