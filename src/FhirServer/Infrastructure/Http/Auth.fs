namespace FhirServer.Infrastructure.Http

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open FhirServer.Application.Smart

module Auth =

    let private unauthorized (ctx: HttpContext) (scheme: string) =
        task {
            ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
            ctx.Response.Headers.WWWAuthenticate <- scheme
            do! ctx.Response.WriteAsync("Unauthorized")
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

    let private validateBearer (header: string) =
        if not (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) then
            false
        else
            let token = header.Substring(7).Trim()
            SmartAuth.tryValidateBearer token |> Option.isSome

    /// Require Basic and/or SMART Bearer when Auth:Enabled=true.
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

                            if validateBasic app.Configuration header || validateBearer header then
                                return! next.Invoke(ctx)
                            else
                                return! unauthorized ctx "Bearer, Basic realm=\"FhirServer\""
                    })
            )
            |> ignore

            app

    [<Obsolete("Use useAuth")>]
    let useBasicAuth = useAuth
