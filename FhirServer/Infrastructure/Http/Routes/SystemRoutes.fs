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

/// Root, health, version, metadata, system $validate.
module SystemRoutes =

    let map (app: WebApplication) =
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
            "/metadata",
            Func<HttpContext, Task<unit>>(fun ctx -> Responses.fhirOk (Metadata.build (HttpRequestHelpers.baseUrl ctx)) ctx)
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
                    let! body = HttpRequestHelpers.readBody ctx
                    let result = ResourceHandlers.validateRaw None body
                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirValidate
            [| OpenApiDocs.TagValidate |]
            "Validate (system)"
            "Validate a resource body without persisting. Always returns 200 + OperationOutcome."
        |> ignore
