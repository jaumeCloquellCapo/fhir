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
open FhirServer.Application.Smart
open FhirServer.Domain.Services

/// SMART discovery, authorize, token, DCR.
module SmartAuthRoutes =

    let map (app: WebApplication) =
        app.MapGet(
            "/.well-known/smart-configuration",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                    let doc = SmartAuth.wellKnown (HttpRequestHelpers.baseUrl ctx) config
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
                let get name = HttpRequestHelpers.queryOrForm ctx form name

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

                let requireAuthorizeLogin =
                    config.GetValue<bool>("Smart:RequireAuthorizeLogin", true)

                if requireAuthorizeLogin then
                    let authHeader = ctx.Request.Headers.Authorization.ToString()

                    if not (Auth.validateBasic config authHeader) then
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
                                let! body = HttpRequestHelpers.readBody ctx
                                return HttpRequestHelpers.parseForm body
                            else
                                return Map.empty
                        }

                    return! handleAuthorize ctx form
                })
        )
        |> OpenApiDocs.documentJson
            [| OpenApiDocs.TagSystem |]
            "OAuth authorize"
            "SMART authorization_code + PKCE (S256). When Smart:RequireAuthorizeLogin=true (default), requires HTTP Basic. patient= must exist; otherwise an HTML form is shown."
        |> ignore

        app.MapPost(
            "/auth/token",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let config = ctx.RequestServices.GetRequiredService<IConfiguration>()
                    let! body = HttpRequestHelpers.readBody ctx
                    let form = HttpRequestHelpers.parseForm body

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
                        let! body = HttpRequestHelpers.readBody ctx

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
