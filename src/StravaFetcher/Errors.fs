namespace StravaFetcher

open System

type StravaApiException(message: string) =
    inherit Exception(message)

type FetchError =
    | MissingEnv of string
    | HttpError of statusCode: int * reasonPhrase: string * url: string * bodyPreview: string
    | UnexpectedContentType of mediaType: string * bodyPreview: string
    | JsonDecodeError of context: string * message: string
    | JsonDecodedNull of context: string
    | MissingTokenField of string
    | Message of string

module FetchError =
    let render =
        function
        | MissingEnv name -> $"Missing required environment variable: {name}"
        | HttpError(statusCode, reasonPhrase, url, bodyPreview) ->
            $"HTTP {statusCode} ({reasonPhrase}) from {url}: {bodyPreview}"
        | UnexpectedContentType(mediaType, bodyPreview) ->
            $"Expected JSON response but received content-type '{mediaType}' with body: {bodyPreview}"
        | JsonDecodeError(context, message) -> $"Failed to decode {context} JSON: {message}"
        | JsonDecodedNull context -> $"Decoded JSON for {context} was null"
        | MissingTokenField fieldName -> $"Token refresh response did not include a {fieldName}"
        | Message message -> message
