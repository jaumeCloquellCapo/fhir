namespace FhirServer.Infrastructure.Http

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open FhirServer.Application.Smart
open FhirServer.Domain
open FhirServer.Domain.Types

module Auth =

    let private unauthorized (ctx: HttpContext) (scheme: string) =
        task {
            ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
            ctx.Response.Headers.WWWAuthenticate <- scheme
            do! ctx.Response.WriteAsync("Unauthorized")
        }

    let private forbidden (ctx: HttpContext) (message: string) =
        task {
            ctx.Response.StatusCode <- StatusCodes.Status403Forbidden
            ctx.Response.ContentType <- "application/fhir+json"

            let oo =
                FhirJson.operationOutcome [ "error", "forbidden", message ]

            do! ctx.Response.WriteAsync(FhirJson.toJson oo)
        }

    let private fixedTimeEquals (left: string) (right: string) =
        let a = SHA256.HashData(Encoding.UTF8.GetBytes(left))
        let b = SHA256.HashData(Encoding.UTF8.GetBytes(right))
        CryptographicOperations.FixedTimeEquals(a.AsSpan(), b.AsSpan())

    let private isPublicPath (config: IConfiguration) (authEnabled: bool) (path: string) =
        let allowPublicHooks =
            config.GetValue<bool>("Subscriptions:AllowPublicHooks", not authEnabled)

        let enableDcr = config.GetValue<bool>("Smart:EnableDcr", false)

        path = "/health"
        || path = "/"
        || path = "/.well-known/smart-configuration"
        || path = "/auth/token"
        || path = "/auth/authorize"
        || (path = "/auth/register" && enableDcr)
        || (path <> null
            && (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
                || (allowPublicHooks
                    && path.StartsWith("/_hooks/", StringComparison.OrdinalIgnoreCase))))

    let validateBasic (config: IConfiguration) (header: string) =
        let user = config.["Auth:Username"]
        let pass = config.["Auth:Password"]

        if String.IsNullOrWhiteSpace user || String.IsNullOrWhiteSpace pass then
            false
        elif String.IsNullOrWhiteSpace header then
            false
        elif not (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) then
            false
        else
            try
                let encoded = header.Substring(6).Trim()
                let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                let parts = decoded.Split(':', 2)

                parts.Length = 2
                && fixedTimeEquals parts.[0] user
                && fixedTimeEquals parts.[1] pass
            with _ ->
                false

    let private basicScopes (config: IConfiguration) =
        match config.["Auth:BasicScopes"] with
        | null
        | "" -> "system/*.cruds"
        | value -> value.Trim()

    let private basicAuthContext (config: IConfiguration) : SmartAuth.AuthContext =
        let scope = basicScopes config

        {
            ClientId = "basic-auth"
            Scope = scope
            PatientId = None
            FhirUser = None
            CompartmentPatientId = PatientCompartment.tryCompartmentPatientId scope None None
        }

    let private tryBearerRecord (header: string) =
        if not (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) then
            None
        else
            let token = header.Substring(7).Trim()
            SmartAuth.tryValidateBearer token

    let tryGetAuthContext (ctx: HttpContext) : SmartAuth.AuthContext option =
        match ctx.Items.TryGetValue(SmartAuth.AuthContextItemKey) with
        | true, (:? SmartAuth.AuthContext as auth) -> Some auth
        | _ -> None

    /// Apply patient-compartment search binding when the token requires it.
    let bindCompartmentQuery
        (ctx: HttpContext)
        (resourceType: string)
        (query: Map<string, string list>)
        : Result<Map<string, string list>, FhirError> =
        match tryGetAuthContext ctx with
        | Some auth ->
            match auth.CompartmentPatientId with
            | Some patientId ->
                match PatientCompartment.bindSearchQuery patientId resourceType query with
                | Ok q -> Ok q
                | Error msg -> Error(Forbidden msg)
            | None ->
                if SmartScopes.hasUserClinicalScopes auth.Scope && auth.FhirUser.IsNone then
                    Error(Forbidden "user/ scopes require fhirUser on the access token")
                else
                    Ok query
        | None -> Ok query

    let ensureResourceAccess (ctx: HttpContext) (resource: FhirResource) : Result<unit, FhirError> =
        match tryGetAuthContext ctx with
        | Some auth ->
            match auth.CompartmentPatientId with
            | Some patientId when not (PatientCompartment.belongsTo patientId resource) ->
                Error(Forbidden $"Resource is outside patient compartment Patient/{patientId}")
            | Some _ -> Ok()
            | None ->
                if SmartScopes.hasUserClinicalScopes auth.Scope && auth.FhirUser.IsNone then
                    Error(Forbidden "user/ scopes require fhirUser on the access token")
                else
                    Ok()
        | None -> Ok()

    let ensurePatientIdAccess (ctx: HttpContext) (resourceType: string) (id: string) : Result<unit, FhirError> =
        match tryGetAuthContext ctx with
        | Some auth ->
            match auth.CompartmentPatientId with
            | Some patientId when resourceType.Equals("Patient", StringComparison.OrdinalIgnoreCase) ->
                if PatientCompartment.normalizePatientId id = patientId then
                    Ok()
                else
                    Error(Forbidden $"Resource is outside patient compartment Patient/{patientId}")
            | Some _ -> Ok() // non-Patient: checked after load
            | None -> Ok()
        | None -> Ok()

    let private enforceScopes (ctx: HttpContext) (auth: SmartAuth.AuthContext) =
        task {
            let interaction, resourceType =
                SmartScopes.resolveInteraction ctx.Request.Method (ctx.Request.Path.Value)

            if SmartScopes.hasUserClinicalScopes auth.Scope && auth.FhirUser.IsNone then
                return! forbidden ctx "user/ scopes require fhirUser on the access token"
            elif SmartScopes.hasPatientClinicalScopes auth.Scope && auth.PatientId.IsNone then
                return! forbidden ctx "patient/ scopes require patient context on the access token"
            elif not (SmartScopes.allows auth.Scope resourceType interaction) then
                let needed = SmartScopes.describeNeeded resourceType interaction

                return!
                    forbidden
                        ctx
                        $"Missing SMART scope for {needed} (token scope: {auth.Scope})"
            else
                ctx.Items[SmartAuth.AuthContextItemKey] <- auth
                return! Task.CompletedTask
        }

    /// Require Basic and/or SMART Bearer when Auth:Enabled=true.
    /// Basic uses Auth:BasicScopes (default system/*.cruds) — same scope checks as Bearer.
    let useAuth (app: WebApplication) =
        let enabled = app.Configuration.GetValue<bool>("Auth:Enabled", true)

        if not enabled then
            app
        else
            app.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    task {
                        let path = ctx.Request.Path.Value
                        let config = app.Configuration

                        if isPublicPath config true path then
                            return! next.Invoke(ctx)
                        else
                            let header = ctx.Request.Headers.Authorization.ToString()

                            if validateBasic config header then
                                let auth = basicAuthContext config
                                do! enforceScopes ctx auth

                                if ctx.Response.HasStarted || ctx.Response.StatusCode >= 400 then
                                    return ()
                                else
                                    return! next.Invoke(ctx)
                            else
                                match tryBearerRecord header with
                                | None -> return! unauthorized ctx "Bearer, Basic realm=\"FhirServer\""
                                | Some record ->
                                    let auth = SmartAuth.toAuthContext record
                                    do! enforceScopes ctx auth

                                    if ctx.Response.HasStarted || ctx.Response.StatusCode >= 400 then
                                        return ()
                                    else
                                        return! next.Invoke(ctx)
                    })
            )
            |> ignore

            app

    [<Obsolete("Use useAuth")>]
    let useBasicAuth = useAuth
