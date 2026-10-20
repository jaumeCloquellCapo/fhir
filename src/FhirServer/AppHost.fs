namespace FhirServer

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open FhirServer.Infrastructure.Capability
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Infrastructure.Http
open FhirServer.Domain.Ports
open FhirServer.Application
open FhirServer.Application.Smart
open FhirServer.Application.Subscriptions
open FhirServer.Application.Terminology
open FhirServer.Infrastructure.Persistence
open FhirServer.Infrastructure.Persistence.Postgres

/// Composition root helpers (also used by E2E WebApplicationFactory).
module AppHost =

    let private readBody (ctx: HttpContext) =
        task {
            use reader = new StreamReader(ctx.Request.Body)
            return! reader.ReadToEndAsync()
        }

    let private queryMap (ctx: HttpContext) =
        ctx.Request.Query
        |> Seq.groupBy (fun kv -> kv.Key)
        |> Seq.map (fun (key, values) -> key, values |> Seq.collect (fun kv -> kv.Value.ToArray()) |> Seq.toList)
        |> Map.ofSeq

    let private header (ctx: HttpContext) name =
        if ctx.Request.Headers.ContainsKey(name) then
            let value = string ctx.Request.Headers.[name]
            if String.IsNullOrWhiteSpace value then None else Some value
        else
            None

    let private baseUrl (ctx: HttpContext) =
        $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}"

    let private requestFormat (ctx: HttpContext) =
        let contentType =
            if String.IsNullOrWhiteSpace ctx.Request.ContentType then
                None
            else
                Some ctx.Request.ContentType

        Codec.negotiateFormat None contentType

    let createStore (config: IConfiguration) : IResourceStore =
        let cs =
            match config.["ConnectionStrings:Fhir"] with
            | null
            | "" -> "Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir"
            | value -> value

        EnvelopeResourceStore(PostgresEnvelopeDialect(cs)) :> IResourceStore

    let mapRoutes (app: WebApplication) =
        app.MapGet(
            "/",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    ctx.Response.ContentType <- "text/plain; charset=utf-8"
                    do! ctx.Response.WriteAsync("FhirServer — GET /metadata | GET /health | GET /swagger")
                })
        )
        |> OpenApiDocs.documentText
            [| OpenApiDocs.TagSystem |]
            "Root"
            "Short tip pointing at metadata, health, and Swagger UI."
        |> ignore

        app.MapGet(
            "/health",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"status":"pass","description":"health of FhirServer"}""")
                })
        )
        |> OpenApiDocs.documentJson [| OpenApiDocs.TagSystem |] "Health" "Liveness/health check (`status: pass`)."
        |> ignore

        app.MapGet(
            "/$version",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync("""{"version":"0.2.0","product":"FhirServer"}""")
                })
        )
        |> OpenApiDocs.documentJson [| OpenApiDocs.TagSystem |] "Version" "Build/product version JSON."
        |> ignore

        app.MapGet(
            "/.well-known/smart-configuration",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                    let doc = SmartAuth.wellKnown (baseUrl ctx) config
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(FhirJson.toJson doc)
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "SMART configuration"
            "SMART App Launch discovery document."
        |> ignore

        app.MapGet(
            "/auth/authorize",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    // MVP: authorization_code UX is out of scope; advertise endpoint for discovery.
                    ctx.Response.StatusCode <- StatusCodes.Status501NotImplemented
                    ctx.Response.ContentType <- "application/json"

                    do!
                        ctx.Response.WriteAsync(
                            """{"error":"unsupported_response_type","error_description":"Use grant_type=client_credentials against /auth/token"}"""
                        )
                })
        )
        |> ignore

        app.MapPost(
            "/auth/token",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                    let! body = readBody ctx

                    let form =
                        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.choose (fun part ->
                            match part.Split('=', 2) with
                            | [| k; v |] -> Some(Uri.UnescapeDataString(k), Uri.UnescapeDataString(v))
                            | _ -> None)
                        |> Map.ofArray

                    let authHeader =
                        let h = ctx.Request.Headers.Authorization.ToString()
                        if String.IsNullOrWhiteSpace h then None else Some h

                    match SmartAuth.parseClientCredentials authHeader form with
                    | None ->
                        ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                        do! ctx.Response.WriteAsync("""{"error":"invalid_client"}""")
                    | Some(id, secret) when not (SmartAuth.validateClient config id secret) ->
                        ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                        do! ctx.Response.WriteAsync("""{"error":"invalid_client"}""")
                    | Some _ ->
                        let grant =
                            form |> Map.tryFind "grant_type" |> Option.defaultValue "client_credentials"

                        if grant <> "client_credentials" then
                            ctx.Response.StatusCode <- StatusCodes.Status400BadRequest
                            do! ctx.Response.WriteAsync("""{"error":"unsupported_grant_type"}""")
                        else
                            let tokenDoc = SmartAuth.issueToken config (Map.tryFind "scope" form)

                            ctx.Response.ContentType <- "application/json"
                            do! ctx.Response.WriteAsync(FhirJson.toJson tokenDoc)
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "OAuth token"
            "SMART token endpoint (client_credentials)."
        |> ignore

        app.MapPost(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    let! body = readBody ctx
                    SubscriptionNotifier.captureHook sinkId body
                    ctx.Response.StatusCode <- StatusCodes.Status202Accepted
                })
        )
        |> ignore

        app.MapGet(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    let payloads = SubscriptionNotifier.listCaptured sinkId
                    let arr = JsonArray()

                    for p in payloads do
                        match JsonNode.Parse(p) with
                        | null -> arr.Add(FhirJson.jStr p)
                        | node -> arr.Add(node)

                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(arr.ToJsonString())
                })
        )
        |> ignore

        app.MapDelete(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    SubscriptionNotifier.clearCaptured sinkId
                    ctx.Response.StatusCode <- StatusCodes.Status204NoContent
                })
        )
        |> ignore

        app.MapGet(
            "/metadata",
            Func<HttpContext, Task<unit>>(fun ctx -> Responses.fhirOk (Metadata.build (baseUrl ctx)) ctx)
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagMetadata |]
            "CapabilityStatement"
            "FHIR CapabilityStatement for this server instance."
        |> ignore

        app.MapPost(
            "/$validate",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let! body = readBody ctx
                    let result = ResourceHandlers.validateRaw None body
                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirValidate
            [| OpenApiDocs.TagValidate |]
            "Validate (system)"
            "Validate a resource body without persisting. Always returns 200 + OperationOutcome."
        |> ignore

        let startExport (ctx: HttpContext) =
            task {
                let types =
                    match ctx.Request.Query.TryGetValue("_type") with
                    | true, values when values.Count > 0 -> Some(string values.[0])
                    | _ -> None
                    |> BulkExport.parseTypes

                let job = BulkExport.start types
                let statusUrl = $"{baseUrl ctx}/$export-status/{job.Id}"
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
                        let status = BulkExport.statusResource (baseUrl ctx) job
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

                    match BulkExport.fileNdjson store jobId resourceType with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok body ->
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "application/fhir+ndjson"
                        do! ctx.Response.WriteAsync(body)
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
                let! body = readBody ctx
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

        app.MapGet(
            "/_history",
            Func<HttpContext, Task<unit>>(fun ctx ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.historyFromQuery store None None (queryMap ctx)
                |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagHistory |]
            "System history"
            "History Bundle across all resource types."
        |> ignore

        app.MapPost(
            "/",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        Codec.tryDeserialize (requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            match FhirJson.resourceType resource with
                            | "Bundle" -> ResourceHandlers.executeBundle store resource
                            | _ -> Error(BadRequest "Root POST expects a Bundle."))

                    return! Responses.fromSearch result ctx
                })
        )
        |> OpenApiDocs.documentFhirBundle
            [| OpenApiDocs.TagBundle |]
            "Bundle transaction/batch"
            "Execute a Bundle of type transaction or batch (URN fullUrl resolution)."
        |> ignore

        app.MapGet(
            "/{resourceType}/_history",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.historyFromQuery store (Some resourceType) None (queryMap ctx)
                |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagHistory |]
            "Type history"
            "History Bundle for all instances of a resource type."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/_history/{vid}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx resourceType id vid ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.vread store resourceType id vid
                |> fun result -> Responses.fromResult result ctx)
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagHistory |]
            "VRead"
            "Read a specific historical version of a resource."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/_history",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.historyFromQuery store (Some resourceType) (Some id) (queryMap ctx)
                |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagHistory |]
            "Instance history"
            "History Bundle for one resource id."
        |> ignore

        app.MapGet(
            "/{resourceType}/$dump",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    match ResourceHandlers.dump store resourceType with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resources ->
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "application/fhir+ndjson"

                        for resource in resources do
                            do! ctx.Response.WriteAsync(FhirJson.toJson resource + "\n")
                })
        )
        |> OpenApiDocs.documentNdjson
            [| OpenApiDocs.TagOperations |]
            "$dump"
            "Stream all current resources of a type as FHIR NDJSON."
        |> ignore

        let parseOptionalBody (ctx: HttpContext) (body: string) =
            if String.IsNullOrWhiteSpace body then
                Ok None
            else
                Codec.tryDeserialize (requestFormat ctx) body |> Result.map Some

        app.MapGet(
            "/ValueSet/$expand",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.expand store None (queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "ValueSet $expand" "Expand a ValueSet by url."
        |> ignore

        app.MapPost(
            "/ValueSet/$expand",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.expand store None (queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/{id}/$expand",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.expand store (Some id) (queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "ValueSet/{id}/$expand" "Expand a stored ValueSet."
        |> ignore

        app.MapPost(
            "/ValueSet/{id}/$expand",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.expand store (Some id) (queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/CodeSystem/$lookup",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.lookup store (queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "CodeSystem $lookup" "Lookup a code in a CodeSystem."
        |> ignore

        app.MapPost(
            "/CodeSystem/$lookup",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return! Responses.fromResult (TerminologyOps.lookup store (queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/$validate-code",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    return!
                        Responses.fromResult (TerminologyOps.validateCode store None (queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagOperations |]
            "ValueSet $validate-code"
            "Validate a code against a ValueSet."
        |> ignore

        app.MapPost(
            "/ValueSet/$validate-code",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.validateCode store None (queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/{id}/$validate-code",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    return!
                        Responses.fromResult (TerminologyOps.validateCode store (Some id) (queryMap ctx) None) ctx
                })
        )
        |> ignore

        app.MapPost(
            "/ValueSet/{id}/$validate-code",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult
                                (TerminologyOps.validateCode store (Some id) (queryMap ctx) resource)
                                ctx
                })
        )
        |> ignore

        app.MapGet(
            "/{resourceType}/$lastn",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    if not (resourceType.Equals("Observation", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$lastn is only supported on Observation.") ctx
                    else
                        let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                        return! Responses.fromSearch (ResourceHandlers.observationLastN store (queryMap ctx)) ctx
                })
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOperations |]
            "$lastn (Observation)"
            "Most recent Observations matching search criteria (Observation only)."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/$everything",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    if not (resourceType.Equals("Patient", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$everything is only supported on Patient.") ctx
                    else
                        let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                        return! Responses.fromSearch (ResourceHandlers.patientEverything store id) ctx
                })
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOperations |]
            "$everything (Patient)"
            "Patient clinical summary Bundle (Patient only)."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/$document",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    if not (resourceType.Equals("Patient", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$document is only supported on Patient.") ctx
                    else
                        let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                        return! Responses.fromResult (ResourceHandlers.patientDocument store id) ctx
                })
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagOperations |]
            "$document (Patient)"
            "FHIR Document Bundle (Composition + clinical resources) for a Patient."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.read store resourceType id
                |> fun result -> Responses.fromResult result ctx)
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagCrud |]
            "Read"
            "Read current resource by type and id. Returns 410 if soft-deleted."
        |> ignore

        app.MapGet(
            "/{resourceType}",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.search store resourceType (queryMap ctx)
                |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagSearch |]
            "Search"
            "Search resources of a type. Supports subset of FHIR search params + `_include` / `_revinclude` / chaining."
        |> ignore

        app.MapPost(
            "/{resourceType}/$validate",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    let! body = readBody ctx
                    let result = ResourceHandlers.validateRaw (Some resourceType) body
                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirValidate
            [| OpenApiDocs.TagValidate |]
            "Validate (type)"
            "Validate a resource of the given type without persisting."
        |> ignore

        app.MapPost(
            "/{resourceType}",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        Codec.tryDeserialize (requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            ResourceHandlers.conditionalCreate
                                store
                                resourceType
                                resource
                                (header ctx "If-None-Exist"))

                    return! Responses.fromCreated resourceType result ctx
                })
        )
        |> OpenApiDocs.documentFhirCreate
            [| OpenApiDocs.TagCrud |]
            "Create"
            "Create a resource. Optional If-None-Exist for conditional create. 422 if schema-invalid."
        |> ignore

        app.MapPut(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        Codec.tryDeserialize (requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            ResourceHandlers.update store resourceType id resource (header ctx "If-Match"))

                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirUpdate
            [| OpenApiDocs.TagCrud |]
            "Update"
            "Update (or create) by id. If-Match → 412 on version mismatch."
        |> ignore

        app.MapMethods(
            "/{resourceType}/{id}",
            [ HttpMethods.Patch ],
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx
                    let result = ResourceHandlers.applyJsonPatch store resourceType id body
                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirUpdate
            [| OpenApiDocs.TagCrud |]
            "Patch"
            "JSON Patch (root-field subset) against the current resource."
        |> ignore

        app.MapDelete(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                ResourceHandlers.delete store resourceType id (header ctx "If-Match")
                |> fun result -> Responses.fromDelete result ctx)
        )
        |> OpenApiDocs.documentFhirDelete
            [| OpenApiDocs.TagCrud |]
            "Delete"
            "Soft-delete via history. Returns 200 + deleted resource body."
        |> ignore

        app

    let create (args: string[]) =
        let builder = WebApplication.CreateBuilder(args)

        builder.Services.AddSingleton<IResourceStore>(fun sp -> createStore (sp.GetRequiredService<IConfiguration>()))
        |> ignore

        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen(Action<_> OpenApiDocs.configureSwagger) |> ignore

        let app = builder.Build()

        app.UseSwagger() |> ignore

        app.UseSwaggerUI(fun options ->
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "FhirServer v1")
            options.DocumentTitle <- "FhirServer API"
            options.RoutePrefix <- "swagger")
        |> ignore

        Auth.useAuth app |> ignore
        AuditMiddleware.useAudit app |> ignore
        mapRoutes app

/// Marker for Microsoft.AspNetCore.Mvc.Testing WebApplicationFactory.
type Marker = class end
