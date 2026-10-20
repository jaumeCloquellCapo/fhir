namespace FhirServer.Infrastructure.Http

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration

module Auth =

    let private unauthorized (ctx: HttpContext) =
        task {
            ctx.Response.StatusCode <- StatusCodes.Status401Unauthorized
            ctx.Response.Headers.WWWAuthenticate <- "Basic realm=\"FhirServer\""
            do! ctx.Response.WriteAsync("Unauthorized")
        }

    let useBasicAuth (app: WebApplication) =
        let user = app.Configuration.["Auth:Username"]
        let pass = app.Configuration.["Auth:Password"]
        let enabled = app.Configuration.GetValue<bool>("Auth:Enabled", false)

        if not enabled || String.IsNullOrWhiteSpace user then
            app
        else
            app.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    task {
                        let path = ctx.Request.Path.Value

                        let isPublic =
                            path = "/health"
                            || path = "/"
                            || (path <> null
                                && (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)
                                    || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)))

                        if isPublic then
                            return! next.Invoke(ctx)
                        else
                            let header = ctx.Request.Headers.Authorization.ToString()

                            if not (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) then
                                return! unauthorized ctx
                            else
                                try
                                    let encoded = header.Substring(6).Trim()
                                    let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                                    let parts = decoded.Split(':', 2)

                                    if parts.Length = 2 && parts.[0] = user && parts.[1] = pass then
                                        return! next.Invoke(ctx)
                                    else
                                        return! unauthorized ctx
                                with _ ->
                                    return! unauthorized ctx
                    })
            )
            |> ignore

            app
