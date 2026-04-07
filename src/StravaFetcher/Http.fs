namespace StravaFetcher

open System
open System.Net.Http
open FsHttp
open FsToolkit.ErrorHandling

module Http =
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
            Error(UnexpectedContentType(mediaType, trimBody body))
        else
            Ok()

    let ensureJsonResponse (response: HttpResponseMessage) (body: string) =
        match ensureJsonResponseResult response body with
        | Ok() -> ()
        | Error error -> raise (StravaApiException(FetchError.render error))

    let ensureSuccessResult (response: HttpResponseMessage) (body: string) =
        if not response.IsSuccessStatusCode then
            let requestUri =
                match response.RequestMessage with
                | null -> "<unknown-url>"
                | request ->
                    match request.RequestUri with
                    | null -> "<unknown-url>"
                    | uri -> uri.ToString()

            let reasonPhrase =
                response.ReasonPhrase |> Option.ofObj |> Option.defaultValue "<unknown-reason>"

            Error(HttpError(int response.StatusCode, reasonPhrase, requestUri, trimBody body))
        else
            Ok()

    let ensureSuccess (response: HttpResponseMessage) (body: string) =
        match ensureSuccessResult response body with
        | Ok() -> ()
        | Error error -> raise (StravaApiException(FetchError.render error))

    let private unwrap result =
        result
        |> ResultEx.teeError (FetchError.render >> StravaApiException >> raise)
        |> function
            | Ok value -> value
            | Error _ -> failwith "unreachable"

    let private sendResultAsync request =
        task {
            let! response = Request.sendAsync request
            let! body = Response.toTextAsync response
            use responseMessage = Response.asOriginalHttpResponseMessage response

            return
                result {
                    do! ensureSuccessResult responseMessage body
                    do! ensureJsonResponseResult responseMessage body
                    return body
                }
        }

    let postFormResultAsync (url: string) (pairs: (string * string) list) =
        http {
            POST url
            Accept "application/json"
            body
            formUrlEncoded pairs
        }
        |> sendResultAsync

    let postFormAsync (url: string) (pairs: (string * string) list) =
        task {
            let! bodyResult = postFormResultAsync url pairs
            return unwrap bodyResult
        }

    let getJsonResultAsync (url: string) (accessToken: string) =
        http {
            GET url
            Accept "application/json"
            AuthorizationBearer accessToken
        }
        |> sendResultAsync

    let getJsonAsync (url: string) (accessToken: string) =
        task {
            let! bodyResult = getJsonResultAsync url accessToken
            return unwrap bodyResult
        }
