namespace FhirServer.Infrastructure.Http.Routes

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Application
open FhirServer.Infrastructure.Http
open FhirServer.Infrastructure.Capability

/// Bulk $export / $import / $load.
module BulkRoutes =

    let map (app: WebApplication) =
        let startExport (ctx: HttpContext) =
            task {
                let types =
                    match ctx.Request.Query.TryGetValue("_type") with
                    | true, values when values.Count > 0 -> Some(string values.[0])
                    | _ -> None
                    |> BulkExport.parseTypes

                let job = BulkExport.start types
                let statusUrl = $"{HttpRequestHelpers.baseUrl ctx}/$export-status/{job.Id}"
                ctx.Response.StatusCode <- StatusCodes.Status202Accepted
                ctx.Response.Headers.ContentLocation <- statusUrl
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync($"""{{"statusUrl":"{statusUrl}","jobId":"{job.Id}"}}""")
            }

        app.MapGet("/$export", Func<HttpContext, Task<unit>>(startExport))
        |> OpenApiDocs.documentBulkAccepted
            [| OpenApiDocs.TagBulk |]
            "Kick off $export (GET)"
            "Starts an async bulk export job. Optional `_type` (comma-separated). Returns 202 + Content-Location."
        |> ignore

        app.MapPost("/$export", Func<HttpContext, Task<unit>>(startExport))
        |> OpenApiDocs.documentBulkAccepted
            [| OpenApiDocs.TagBulk |]
            "Kick off $export (POST)"
            "Same as GET /$export."
        |> ignore

        app.MapGet(
            "/$export-status/{jobId}",
            Func<HttpContext, string, Task<unit>>(fun ctx jobId ->
                task {
                    match BulkExport.tryGet jobId with
                    | None -> return! Responses.fromError (NotFound("BulkExport", jobId)) ctx
                    | Some job ->
                        let status = BulkExport.statusResource (HttpRequestHelpers.baseUrl ctx) job
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsync(FhirJson.toJson status)
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagBulk |]
            "Bulk export status"
            "Poll job status; includes output file URLs when complete."
        |> ignore

        app.MapGet(
            "/$export-files/{jobId}/{resourceType}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx jobId resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    match BulkExport.tryGet jobId with
                    | None -> return! Responses.fromError (NotFound("BulkExport", jobId)) ctx
                    | Some job when not (List.contains resourceType job.ResourceTypes) ->
                        return!
                            Responses.fromError
                                (BadRequest $"Resource type '{resourceType}' is not part of export job '{jobId}'.")
                                ctx
                    | Some _ when not (ResourceCatalog.isSupported resourceType) ->
                        return! Responses.fromError (UnsupportedType resourceType) ctx
                    | Some _ ->
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "application/fhir+ndjson"
                        let mutable after: string option = None
                        let mutable cont = true

                        while cont do
                            let page = store.ListByTypePage(resourceType, after, 500)

                            match page with
                            | [] -> cont <- false
                            | rows ->
                                for resource in rows do
                                    do! ctx.Response.WriteAsync(FhirJson.toJson resource + "\n")

                                after <- rows |> List.tryLast |> Option.map FhirJson.id
                })
        )
        |> OpenApiDocs.documentNdjson
            [| OpenApiDocs.TagBulk |]
            "Bulk export NDJSON file"
            "Download NDJSON for one resource type from a completed export job."
        |> ignore

        let importNdjson (ctx: HttpContext) =
            task {
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                let! body = HttpRequestHelpers.readBody ctx
                let result = BulkImport.importNdjson store body
                return! Responses.fromResult result ctx
            }

        app.MapPost("/$import", Func<HttpContext, Task<unit>>(importNdjson))
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagBulk |]
            "$import"
            "Synchronous NDJSON import (create/update). Body: application/fhir+ndjson or text lines."
        |> ignore

        app.MapPost("/$load", Func<HttpContext, Task<unit>>(importNdjson))
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagBulk |] "$load" "Alias of /$import — synchronous NDJSON load."
        |> ignore
