namespace FhirServer.Infrastructure.Http

open System
open System.IO
open Microsoft.AspNetCore.Http

/// Shared HTTP request parsing for route modules (composition root).
module HttpRequestHelpers =

    /// Default max body (32 MiB). Bulk import can raise per-endpoint later.
    let maxRequestBodyBytes = 32L * 1024L * 1024L

    let readBody (ctx: HttpContext) =
        task {
            // Oversized bodies are rejected by Kestrel (MaxRequestBodySize) before handlers run.
            use reader = new StreamReader(ctx.Request.Body)
            return! reader.ReadToEndAsync()
        }

    let queryMap (ctx: HttpContext) =
        ctx.Request.Query
        |> Seq.groupBy (fun kv -> kv.Key)
        |> Seq.map (fun (key, values) -> key, values |> Seq.collect (fun kv -> kv.Value.ToArray()) |> Seq.toList)
        |> Map.ofSeq

    let queryMapForType (ctx: HttpContext) (resourceType: string) =
        Auth.bindCompartmentQuery ctx resourceType (queryMap ctx)

    let parseForm (body: string) =
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| k; v |] -> Some(Uri.UnescapeDataString(k), Uri.UnescapeDataString(v))
            | _ -> None)
        |> Map.ofArray

    let queryOrForm (ctx: HttpContext) (form: Map<string, string>) (name: string) =
        match ctx.Request.Query.TryGetValue name with
        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace v.[0]) -> Some v.[0]
        | _ -> Map.tryFind name form

    let header (ctx: HttpContext) name =
        if ctx.Request.Headers.ContainsKey(name) then
            let value = string ctx.Request.Headers.[name]
            if String.IsNullOrWhiteSpace value then None else Some value
        else
            None

    let baseUrl (ctx: HttpContext) =
        $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}"

    let requestFormat (ctx: HttpContext) =
        let contentType =
            if String.IsNullOrWhiteSpace ctx.Request.ContentType then
                None
            else
                Some ctx.Request.ContentType

        Codec.negotiateFormat None contentType

    /// `$validate` profile URLs from `profile` or `_profile` query (repeatable).
    let validateProfileUrls (ctx: HttpContext) =
        let q = queryMap ctx

        (q |> Map.tryFind "profile" |> Option.defaultValue [])
        @ (q |> Map.tryFind "_profile" |> Option.defaultValue [])
        |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        |> List.distinct
