namespace FhirServer.Infrastructure.Http

open System
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

    let private isPublicPath (path: string) =
        path = "/health"
        || path = "/"
        || path = "/.well-known/smart-configuration"
        || path = "/auth/token"
        || path = "/auth/authorize"
        || path = "/auth/register"
        || (path <> null
            && (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/_hooks/", StringComparison.OrdinalIgnoreCase)))

    let private validateBasic (config: IConfiguration) (header: string) =
        let user = config.["Auth:Username"]
        let pass = config.["Auth:Password"]

        if String.IsNullOrWhiteSpace user then
            false
        elif not (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) then
            false
        else
            try
                let encoded = header.Substring(6).Trim()
                let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                let parts = decoded.Split(':', 2)
                parts.Length = 2 && parts.[0] = user && parts.[1] = pass
            with _ ->
                false

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

    /// Require Basic and/or SMART Bearer when Auth:Enabled=true.
    /// Basic = full access. Bearer scopes enforced per SMART v2 interaction (c/r/u/d/s).
    let useAuth (app: WebApplication) =
        let enabled = app.Configuration.GetValue<bool>("Auth:Enabled", false)

        if not enabled then
            app
        else
            app.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    task {
                        let path = ctx.Request.Path.Value

                        if isPublicPath path then
                            return! next.Invoke(ctx)
                        else
                            let header = ctx.Request.Headers.Authorization.ToString()

                            if validateBasic app.Configuration header then
                                return! next.Invoke(ctx)
                            else
                                match tryBearerRecord header with
                                | None -> return! unauthorized ctx "Bearer, Basic realm=\"FhirServer\""
                                | Some record ->
                                    let interaction, resourceType =
                                        SmartScopes.resolveInteraction ctx.Request.Method path

                                    if SmartScopes.hasUserClinicalScopes record.Scope && record.FhirUser.IsNone then
                                        return! forbidden ctx "user/ scopes require fhirUser on the access token"
                                    elif
                                        SmartScopes.hasPatientClinicalScopes record.Scope
                                        && record.PatientId.IsNone
                                    then
                                        return! forbidden ctx "patient/ scopes require patient context on the access token"
                                    elif not (SmartScopes.allows record.Scope resourceType interaction) then
                                        let needed = SmartScopes.describeNeeded resourceType interaction

                                        return!
                                            forbidden
                                                ctx
                                                $"Missing SMART scope for {needed} (token scope: {record.Scope})"
                                    else
                                        ctx.Items[SmartAuth.AuthContextItemKey] <- SmartAuth.toAuthContext record
                                        return! next.Invoke(ctx)
                    })
            )
            |> ignore

            app

    [<Obsolete("Use useAuth")>]
    let useBasicAuth = useAuth
