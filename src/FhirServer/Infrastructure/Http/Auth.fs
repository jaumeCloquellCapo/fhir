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

    let private isWriteMethod (method: string) =
        method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
        || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)

    let private resourceTypeFromPath (path: string) =
        let parts =
            path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

        match parts with
        | [||] -> None
        | parts when parts.[0].StartsWith("$", StringComparison.Ordinal) -> None
        | parts when parts.[0].Equals("metadata", StringComparison.OrdinalIgnoreCase) -> None
        | parts when ResourceCatalog.isSupported parts.[0] -> Some parts.[0]
        | _ -> None

    /// Require Basic and/or SMART Bearer when Auth:Enabled=true.
    /// Basic = full access. Bearer scopes enforced for system/*.read|write.
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
                                    let action =
                                        if isWriteMethod ctx.Request.Method then
                                            SmartAuth.ScopeAction.Write
                                        else
                                            SmartAuth.ScopeAction.Read

                                    let resourceType = resourceTypeFromPath path

                                    if SmartAuth.allowsSystemScope record.Scope resourceType action then
                                        return! next.Invoke(ctx)
                                    else
                                        let needed =
                                            match action with
                                            | SmartAuth.ScopeAction.Read -> "read"
                                            | SmartAuth.ScopeAction.Write -> "write"

                                        let target =
                                            resourceType |> Option.defaultValue "*"

                                        return!
                                            forbidden
                                                ctx
                                                $"Missing SMART scope system/{target}.{needed} (token scope: {record.Scope})"
                    })
            )
            |> ignore

            app

    [<Obsolete("Use useAuth")>]
    let useBasicAuth = useAuth
