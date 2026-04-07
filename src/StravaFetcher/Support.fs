namespace StravaFetcher

open System
open System.Text.Json

module ResultEx =
    let require predicate error value =
        if predicate value then Ok value else Error error

    let teeError onError result =
        match result with
        | Ok _ -> result
        | Error error ->
            onError error
            result

module Env =
    let getRequired name =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> Error(MissingEnv name)
        | value -> Ok value

    let require name =
        match getRequired name with
        | Ok value -> value
        | Error error -> raise (StravaApiException(FetchError.render error))

module Json =
    let options =
        JsonSerializerOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)

    let deserializeResult<'T> context (content: string) =
        try
            match JsonSerializer.Deserialize<'T>(content, options) with
            | null -> Error(JsonDecodedNull context)
            | value -> Ok value
        with ex ->
            Error(JsonDecodeError(context, ex.Message))

    let deserializeRequired<'T> context (content: string) =
        match deserializeResult<'T> context content with
        | Ok value -> value
        | Error error -> raise (StravaApiException(FetchError.render error))

    let serialize value =
        JsonSerializer.Serialize(value, options)
