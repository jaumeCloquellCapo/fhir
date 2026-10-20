namespace FhirServer.Domain.Services

open System

/// Shared FHIR search/history query parameter parsing.
module QueryParams =

    let first (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.bind List.tryHead

    let values (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.defaultValue []

    let parseCountOffset (query: Map<string, string list>) =
        let count =
            first query "_count"
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n > 0 -> Some(min n 200)
                | _ -> None)
            |> Option.defaultValue 50

        let offset =
            first query "_offset"
            |> Option.orElse (first query "_getpagesoffset")
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n >= 0 -> Some n
                | _ -> None)
            |> Option.defaultValue 0

        count, offset

    let parseSince (query: Map<string, string list>) =
        first query "_since"
        |> Option.bind (fun s ->
            match DateTimeOffset.TryParse s with
            | true, dto -> Some dto
            | _ -> None)
