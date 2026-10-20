namespace FhirServer

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FhirServer.Infrastructure.Capability
open FhirServer.Domain
open FhirServer.Domain.Services
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

    /// Default max body (32 MiB). Bulk import can raise per-endpoint later.
    let private maxRequestBodyBytes = 32L * 1024L * 1024L

    let private readBody (ctx: HttpContext) =
        task {
            // Oversized bodies are rejected by Kestrel (MaxRequestBodySize) before handlers run.
            use reader = new StreamReader(ctx.Request.Body)
            return! reader.ReadToEndAsync()
        }

    let private queryMap (ctx: HttpContext) =
        ctx.Request.Query
        |> Seq.groupBy (fun kv -> kv.Key)
        |> Seq.map (fun (key, values) -> key, values |> Seq.collect (fun kv -> kv.Value.ToArray()) |> Seq.toList)
        |> Map.ofSeq

    let private queryMapForType (ctx: HttpContext) (resourceType: string) =
        Auth.bindCompartmentQuery ctx resourceType (queryMap ctx)

    let private parseForm (body: string) =
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| k; v |] -> Some(Uri.UnescapeDataString(k), Uri.UnescapeDataString(v))
            | _ -> None)
        |> Map.ofArray

    let private queryOrForm (ctx: HttpContext) (form: Map<string, string>) (name: string) =
        match ctx.Request.Query.TryGetValue name with
        | true, v when v.Count > 0 && not (String.IsNullOrWhiteSpace v.[0]) -> Some v.[0]
        | _ -> Map.tryFind name form

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
            | "" ->
                invalidOp
                    "ConnectionStrings:Fhir is required. Set it in .env.dev / .env (ConnectionStrings__Fhir) or configuration."
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

        let oauthErrorJson (ctx: HttpContext) (status: int) (error: string) (description: string option) =
            task {
                ctx.Response.StatusCode <- status
                ctx.Response.ContentType <- "application/json"

                let props =
                    match description with
                    | Some d -> [ "error", FhirJson.jStr error; "error_description", FhirJson.jStr d ]
                    | None -> [ "error", FhirJson.jStr error ]

                do! ctx.Response.WriteAsync(FhirJson.toJson (FhirJson.mkObject props))
            }

        let appendQuery (redirectUri: string) (pairs: (string * string) list) =
            let sep = if redirectUri.Contains('?') then "&" else "?"

            let encoded =
                pairs
                |> List.map (fun (k, v) -> $"{Uri.EscapeDataString k}={Uri.EscapeDataString v}")
                |> String.concat "&"

            redirectUri + sep + encoded

        let redirectOAuth
            (ctx: HttpContext)
            (redirectUri: string)
            (state: string option)
            (pairs: (string * string) list)
            =
            task {
                let all =
                    match state with
                    | Some s -> pairs @ [ "state", s ]
                    | None -> pairs

                ctx.Response.Redirect(appendQuery redirectUri all, false)
            }

        let authorizeHtml (qs: string) (message: string) =
            $"""<!DOCTYPE html><html><head><meta charset="utf-8"/><title>SMART Authorize</title></head>
<body>
<h1>SMART standalone launch</h1>
<p>{System.Net.WebUtility.HtmlEncode message}</p>
<form method="post" action="/auth/authorize">
{qs}
<label>Patient id <input name="patient" required /></label><br/>
<label>fhirUser (optional) <input name="fhir_user" placeholder="Practitioner/abc" /></label><br/>
<button type="submit">Authorize</button>
</form>
</body></html>"""

        let hiddenFields (pairs: (string * string) list) =
            pairs
            |> List.map (fun (k, v) ->
                $"""<input type="hidden" name="{System.Net.WebUtility.HtmlEncode k}" value="{System.Net.WebUtility.HtmlEncode v}" />""")
            |> String.concat "\n"

        let handleAuthorize (ctx: HttpContext) (form: Map<string, string>) =
            task {
                let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                let get name = queryOrForm ctx form name

                let responseType = get "response_type" |> Option.defaultValue ""
                let clientId = get "client_id" |> Option.defaultValue ""
                let redirectUri = get "redirect_uri" |> Option.defaultValue ""
                let scopeReq = get "scope"
                let state = get "state"
                let codeChallenge = get "code_challenge" |> Option.defaultValue ""
                let challengeMethod = get "code_challenge_method" |> Option.defaultValue ""
                let patientParam = get "patient"
                let fhirUserParam = get "fhir_user" |> Option.orElse (get "fhirUser")

                let failRedirect error desc =
                    if String.IsNullOrWhiteSpace redirectUri then
                        oauthErrorJson ctx StatusCodes.Status400BadRequest error (Some desc)
                    else
                        redirectOAuth ctx redirectUri state [ "error", error; "error_description", desc ]

                let showPatientForm (scope: string) =
                    task {
                        let baseFields = [
                            "response_type", responseType
                            "client_id", clientId
                            "redirect_uri", redirectUri
                            "scope", scope
                            "code_challenge", codeChallenge
                            "code_challenge_method", "S256"
                        ]

                        let withState =
                            match state with
                            | Some s when not (String.IsNullOrWhiteSpace s) -> baseFields @ [ "state", s ]
                            | _ -> baseFields

                        let withUser =
                            match fhirUserParam with
                            | Some fu when not (String.IsNullOrWhiteSpace fu) ->
                                withState @ [ "fhir_user", fu ]
                            | _ -> withState

                        ctx.Response.ContentType <- "text/html; charset=utf-8"
                        do! ctx.Response.WriteAsync(authorizeHtml (hiddenFields withUser) "Enter the Patient id for this launch.")
                    }

                let authEnabled = config.GetValue<bool>("Auth:Enabled", true)

                let requireAuthorizeLogin =
                    config.GetValue<bool>("Smart:RequireAuthorizeLogin", authEnabled)

                if requireAuthorizeLogin then
                    let header = ctx.Request.Headers.Authorization.ToString()

                    if not (Auth.validateBasic config header) then
                        ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
                        ctx.Response.Headers.WWWAuthenticate <- "Basic realm=\"FhirServer Authorize\""
                        do! ctx.Response.WriteAsync("Authorize requires HTTP Basic (operator login)")
                        return ()

                match SmartClients.tryFind config clientId with
                | None -> return! failRedirect "invalid_client" "Unknown client_id"
                | Some client when not (SmartClients.allowsGrant client "authorization_code") ->
                    return! failRedirect "unauthorized_client" "authorization_code not allowed"
                | Some client when
                    String.IsNullOrWhiteSpace redirectUri
                    || not (SmartClients.allowsRedirect client redirectUri)
                    ->
                    return!
                        oauthErrorJson
                            ctx
                            StatusCodes.Status400BadRequest
                            "invalid_request"
                            (Some "redirect_uri not registered")
                | Some _ when responseType <> "code" ->
                    return! failRedirect "unsupported_response_type" "response_type must be code"
                | Some _ when
                    String.IsNullOrWhiteSpace codeChallenge
                    || not (challengeMethod.Equals("S256", StringComparison.OrdinalIgnoreCase))
                    ->
                    return! failRedirect "invalid_request" "PKCE S256 code_challenge required"
                | Some client ->
                    let scope = SmartClients.intersectScopes client scopeReq

                    if String.IsNullOrWhiteSpace scope then
                        return! failRedirect "invalid_scope" "No overlapping scopes"
                    elif
                        (SmartScopes.needsPatientLaunch scope || SmartScopes.needsFhirUser scope)
                        && patientParam.IsNone
                        && HttpMethods.IsGet ctx.Request.Method
                    then
                        return! showPatientForm scope
                    elif SmartScopes.needsPatientLaunch scope && patientParam.IsNone then
                        return! failRedirect "invalid_request" "patient context required"
                    elif SmartScopes.needsFhirUser scope && fhirUserParam.IsNone && patientParam.IsNone then
                        return! failRedirect "invalid_request" "fhirUser required for user/ scopes"
                    else
                        match patientParam with
                        | Some rawPid when SmartScopes.needsPatientLaunch scope ->
                            let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                            let pid = PatientCompartment.normalizePatientId rawPid

                            match store.Get("Patient", pid) with
                            | None ->
                                return! failRedirect "invalid_request" $"Unknown patient '{pid}'"
                            | Some _ ->
                                let fhirUser =
                                    match fhirUserParam with
                                    | Some fu when not (String.IsNullOrWhiteSpace fu) -> Some fu
                                    | _ when SmartScopes.hasUserClinicalScopes scope ->
                                        Some($"Patient/{pid}")
                                    | _ -> None

                                let code =
                                    SmartAuth.createAuthCode
                                        config
                                        client.ClientId
                                        redirectUri
                                        scope
                                        codeChallenge
                                        (Some pid)
                                        fhirUser

                                return! redirectOAuth ctx redirectUri state [ "code", code ]
                        | _ ->
                            let fhirUser =
                                match fhirUserParam with
                                | Some fu when not (String.IsNullOrWhiteSpace fu) -> Some fu
                                | _ -> None

                            let code =
                                SmartAuth.createAuthCode
                                    config
                                    client.ClientId
                                    redirectUri
                                    scope
                                    codeChallenge
                                    patientParam
                                    fhirUser

                            return! redirectOAuth ctx redirectUri state [ "code", code ]
            }

        app.MapMethods(
            "/auth/authorize",
            [ HttpMethods.Get; HttpMethods.Post ],
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let! form =
                        task {
                            if HttpMethods.IsPost ctx.Request.Method then
                                let! body = readBody ctx
                                return parseForm body
                            else
                                return Map.empty
                        }

                    return! handleAuthorize ctx form
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "OAuth authorize"
            "SMART authorization_code + PKCE (S256). When Auth:Enabled, requires HTTP Basic (Smart:RequireAuthorizeLogin). patient= must exist; otherwise an HTML form is shown."
        |> ignore

        app.MapPost(
            "/auth/token",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                    let! body = readBody ctx
                    let form = parseForm body

                    let authHeader =
                        let h = ctx.Request.Headers.Authorization.ToString()
                        if String.IsNullOrWhiteSpace h then None else Some h

                    match SmartAuth.parseClientCredentials authHeader form with
                    | None -> return! oauthErrorJson ctx StatusCodes.Status401Unauthorized "invalid_client" None
                    | Some(id, secret) ->
                        match SmartClients.validate config id secret with
                        | None -> return! oauthErrorJson ctx StatusCodes.Status401Unauthorized "invalid_client" None
                        | Some client ->
                            let grant =
                                form |> Map.tryFind "grant_type" |> Option.defaultValue "client_credentials"

                            match grant with
                            | "client_credentials" ->
                                match SmartAuth.issueClientCredentials config client (Map.tryFind "scope" form) with
                                | Error e ->
                                    return! oauthErrorJson ctx StatusCodes.Status400BadRequest e None
                                | Ok doc ->
                                    ctx.Response.ContentType <- "application/json"
                                    do! ctx.Response.WriteAsync(FhirJson.toJson doc)
                            | "authorization_code" ->
                                let code = form |> Map.tryFind "code" |> Option.defaultValue ""
                                let redirectUri = form |> Map.tryFind "redirect_uri" |> Option.defaultValue ""
                                let verifier = form |> Map.tryFind "code_verifier" |> Option.defaultValue ""

                                match SmartAuth.exchangeAuthorizationCode config client code redirectUri verifier with
                                | Error e ->
                                    return! oauthErrorJson ctx StatusCodes.Status400BadRequest e None
                                | Ok doc ->
                                    ctx.Response.ContentType <- "application/json"
                                    do! ctx.Response.WriteAsync(FhirJson.toJson doc)
                            | _ ->
                                return!
                                    oauthErrorJson
                                        ctx
                                        StatusCodes.Status400BadRequest
                                        "unsupported_grant_type"
                                        None
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "OAuth token"
            "SMART token endpoint: client_credentials (system scopes) and authorization_code + PKCE."
        |> ignore

        app.MapPost(
            "/auth/register",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()

                    if not (SmartClients.isDcrEnabled config) then
                        ctx.Response.StatusCode <- StatusCodes.Status404NotFound
                        do! ctx.Response.WriteAsync("Dynamic client registration is disabled")
                    else
                        let! body = readBody ctx

                        match FhirJson.parse body with
                        | Error msg ->
                            return!
                                oauthErrorJson
                                    ctx
                                    StatusCodes.Status400BadRequest
                                    "invalid_client_metadata"
                                    (Some msg)
                        | Ok doc ->
                            match SmartClients.register doc with
                            | Error msg ->
                                return!
                                    oauthErrorJson
                                        ctx
                                        StatusCodes.Status400BadRequest
                                        "invalid_client_metadata"
                                        (Some msg)
                            | Ok created ->
                                ctx.Response.StatusCode <- StatusCodes.Status201Created
                                ctx.Response.ContentType <- "application/json"
                                do! ctx.Response.WriteAsync(FhirJson.toJson created)
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "OAuth register"
            "Dynamic client registration (RFC 7591-style). Disabled unless Smart:EnableDcr=true. Scopes limited to patient/user/openid (no system/*). In-memory only."
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

                // System history: patient compartment tokens are not allowed to browse all types.
                match Auth.tryGetAuthContext ctx with
                | Some auth when auth.CompartmentPatientId.IsSome ->
                    Responses.fromError (Forbidden "System history requires system/ scopes") ctx
                | _ ->
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

                match queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.historyFromQuery store (Some resourceType) None query
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

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.vread store resourceType id vid
                    |> Result.bind (fun resource ->
                        Auth.ensureResourceAccess ctx resource |> Result.map (fun () -> resource))
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

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
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

                        match queryMapForType ctx "Observation" with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok query ->
                            return! Responses.fromSearch (ResourceHandlers.observationLastN store query) ctx
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
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok() ->
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
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok() ->
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

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.read store resourceType id
                    |> Result.bind (fun resource ->
                        Auth.ensureResourceAccess ctx resource |> Result.map (fun () -> resource))
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

                match queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.search store resourceType query
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
                            Auth.ensureResourceAccess ctx resource
                            |> Result.bind (fun () ->
                                ResourceHandlers.conditionalCreate
                                    store
                                    resourceType
                                    resource
                                    (header ctx "If-None-Exist")))

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
                            Auth.ensurePatientIdAccess ctx resourceType id
                            |> Result.bind (fun () -> Auth.ensureResourceAccess ctx resource)
                            |> Result.bind (fun () ->
                                ResourceHandlers.update store resourceType id resource (header ctx "If-Match")))

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

                    let result =
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> Error err
                        | Ok() ->
                            match ResourceHandlers.read store resourceType id with
                            | Error err -> Error err
                            | Ok existing ->
                                match Auth.ensureResourceAccess ctx existing with
                                | Error err -> Error err
                                | Ok() -> ResourceHandlers.applyJsonPatch store resourceType id body

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

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromDelete (Error err) ctx
                | Ok() ->
                    match ResourceHandlers.read store resourceType id with
                    | Error err -> Responses.fromDelete (Error err) ctx
                    | Ok existing ->
                        match Auth.ensureResourceAccess ctx existing with
                        | Error err -> Responses.fromDelete (Error err) ctx
                        | Ok() ->
                            ResourceHandlers.delete store resourceType id (header ctx "If-Match")
                            |> fun result -> Responses.fromDelete result ctx)
        )
        |> OpenApiDocs.documentFhirDelete
            [| OpenApiDocs.TagCrud |]
            "Delete"
            "Soft-delete via history. Returns 200 + deleted resource body."
        |> ignore

        // Aidbox-style hierarchical OrgBAC API: /Organization/{orgId}/fhir/...
        let org = app.MapGroup("/Organization/{orgId}/fhir")

        org.MapPost(
            "/",
            Func<HttpContext, string, Task<unit>>(fun ctx _orgId ->
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
            [| OpenApiDocs.TagOrgBac |]
            "OrgBAC Bundle"
            "Execute a Bundle under an organization compartment (hierarchical visibility)."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}/_history/{vid}",
            Func<HttpContext, string, string, string, string, Task<unit>>(fun ctx _orgId resourceType id vid ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.vread store resourceType id vid
                    |> Result.bind (fun resource ->
                        Auth.ensureResourceAccess ctx resource |> Result.map (fun () -> resource))
                    |> fun result -> Responses.fromResult result ctx)
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC VRead"
            "Read a historical version within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}/_history",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.historyFromQuery store (Some resourceType) (Some id) (queryMap ctx)
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC instance history"
            "Instance history within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/_history",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.historyFromQuery store (Some resourceType) None query
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC type history"
            "Type history within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.read store resourceType id
                    |> Result.bind (fun resource ->
                        Auth.ensureResourceAccess ctx resource |> Result.map (fun () -> resource))
                    |> fun result -> Responses.fromResult result ctx)
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Read"
            "Read a resource under /Organization/{orgId}/fhir (parent sees descendants)."
        |> ignore

        org.MapGet(
            "/{resourceType}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.search store resourceType query
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagSearch |]
            "OrgBAC Search"
            "Search within an organization compartment (hierarchical + shared visibility)."
        |> ignore

        org.MapPost(
            "/{resourceType}/$validate",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                task {
                    let! body = readBody ctx
                    let result = ResourceHandlers.validateRaw (Some resourceType) body
                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirValidate
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagValidate |]
            "OrgBAC Validate"
            "Validate without persisting (organization-scoped URL)."
        |> ignore

        org.MapPost(
            "/{resourceType}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        Codec.tryDeserialize (requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            Auth.ensureResourceAccess ctx resource
                            |> Result.bind (fun () ->
                                ResourceHandlers.conditionalCreate
                                    store
                                    resourceType
                                    resource
                                    (header ctx "If-None-Exist")))

                    return! Responses.fromCreated resourceType result ctx
                })
        )
        |> OpenApiDocs.documentFhirCreate
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Create"
            "Create under an organization; stamps tenant_id and optional shared mode."
        |> ignore

        org.MapPut(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        Codec.tryDeserialize (requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            Auth.ensurePatientIdAccess ctx resourceType id
                            |> Result.bind (fun () -> Auth.ensureResourceAccess ctx resource)
                            |> Result.bind (fun () ->
                                ResourceHandlers.update store resourceType id resource (header ctx "If-Match")))

                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirUpdate
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Update"
            "Update under an organization compartment."
        |> ignore

        org.MapMethods(
            "/{resourceType}/{id}",
            [ HttpMethods.Patch ],
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = readBody ctx

                    let result =
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> Error err
                        | Ok() ->
                            match ResourceHandlers.read store resourceType id with
                            | Error err -> Error err
                            | Ok existing ->
                                match Auth.ensureResourceAccess ctx existing with
                                | Error err -> Error err
                                | Ok() -> ResourceHandlers.applyJsonPatch store resourceType id body

                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirUpdate
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Patch"
            "JSON Patch under an organization compartment."
        |> ignore

        org.MapDelete(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromDelete (Error err) ctx
                | Ok() ->
                    match ResourceHandlers.read store resourceType id with
                    | Error err -> Responses.fromDelete (Error err) ctx
                    | Ok existing ->
                        match Auth.ensureResourceAccess ctx existing with
                        | Error err -> Responses.fromDelete (Error err) ctx
                        | Ok() ->
                            ResourceHandlers.delete store resourceType id (header ctx "If-Match")
                            |> fun result -> Responses.fromDelete result ctx)
        )
        |> OpenApiDocs.documentFhirDelete
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Delete"
            "Soft-delete under an organization compartment."
        |> ignore

        app

    let create (args: string[]) =
        // Repo root (…/src/FhirServer → …/) then content root after builder exists.
        let startDir = Directory.GetCurrentDirectory()
        DotEnv.loadFromDirectory startDir
        DotEnv.loadFromDirectory (Path.GetFullPath(Path.Combine(startDir, "../..")))

        let builder = WebApplication.CreateBuilder(args)

        DotEnv.loadFromDirectory builder.Environment.ContentRootPath

        builder.Services.Configure<KestrelServerOptions>(fun (options: KestrelServerOptions) ->
            options.Limits.MaxRequestBodySize <- maxRequestBodyBytes)
        |> ignore

        builder.Services.AddHttpContextAccessor() |> ignore

        builder.Services.AddSingleton<IResourceStore>(fun sp ->
            createStore (sp.GetRequiredService<IConfiguration>()))
        |> ignore

        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen(Action<_> OpenApiDocs.configureSwagger) |> ignore

        let app = builder.Build()

        let httpAccessor = app.Services.GetRequiredService<IHttpContextAccessor>()

        TenantContext.configureHttpResolver (fun () ->
            match httpAccessor.HttpContext with
            | null -> None
            | ctx ->
                match ctx.Items.TryGetValue(TenantContext.HttpItemKey) with
                | true, (:? TenantAccess as access) -> Some access
                | _ -> None)

        let subscriptionLog =
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FhirServer.Subscriptions")

        SubscriptionNotifier.configure app.Configuration subscriptionLog

        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore

            app.UseSwaggerUI(fun options ->
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "FhirServer v1")
                options.DocumentTitle <- "FhirServer API"
                options.RoutePrefix <- "swagger")
            |> ignore

        Auth.useTenantScope app |> ignore
        Auth.useAuth app |> ignore
        Auth.useOrgBacPath app |> ignore
        AuditMiddleware.useAudit app |> ignore
        mapRoutes app

/// Marker for Microsoft.AspNetCore.Mvc.Testing WebApplicationFactory.
type Marker = class end
