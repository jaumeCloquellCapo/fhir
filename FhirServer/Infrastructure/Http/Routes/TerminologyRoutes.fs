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
open FhirServer.Application.Terminology

/// ValueSet/CodeSystem terminology operations.
module TerminologyRoutes =

    let map (app: WebApplication) =
        let parseOptionalBody (ctx: HttpContext) (body: string) =
            if String.IsNullOrWhiteSpace body then
                Ok None
            else
                Codec.tryDeserialize (HttpRequestHelpers.requestFormat ctx) body |> Result.map Some

        app.MapGet(
            "/ValueSet/$expand",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.expand store None (HttpRequestHelpers.queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "ValueSet $expand" "Expand a ValueSet by url."
        |> ignore

        app.MapPost(
            "/ValueSet/$expand",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.expand store None (HttpRequestHelpers.queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/{id}/$expand",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.expand store (Some id) (HttpRequestHelpers.queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "ValueSet/{id}/$expand" "Expand a stored ValueSet."
        |> ignore

        app.MapPost(
            "/ValueSet/{id}/$expand",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.expand store (Some id) (HttpRequestHelpers.queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/CodeSystem/$lookup",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    return! Responses.fromResult (TerminologyOps.lookup store (HttpRequestHelpers.queryMap ctx) None) ctx
                })
        )
        |> OpenApiDocs.documentFhir [| OpenApiDocs.TagOperations |] "CodeSystem $lookup" "Lookup a code in a CodeSystem."
        |> ignore

        app.MapPost(
            "/CodeSystem/$lookup",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return! Responses.fromResult (TerminologyOps.lookup store (HttpRequestHelpers.queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/$validate-code",
            Func<HttpContext, Task<unit>>(fun ctx ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    return!
                        Responses.fromResult (TerminologyOps.validateCode store None (HttpRequestHelpers.queryMap ctx) None) ctx
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
                    let! body = HttpRequestHelpers.readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult (TerminologyOps.validateCode store None (HttpRequestHelpers.queryMap ctx) resource) ctx
                })
        )
        |> ignore

        app.MapGet(
            "/ValueSet/{id}/$validate-code",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    return!
                        Responses.fromResult (TerminologyOps.validateCode store (Some id) (HttpRequestHelpers.queryMap ctx) None) ctx
                })
        )
        |> ignore

        app.MapPost(
            "/ValueSet/{id}/$validate-code",
            Func<HttpContext, string, Task<unit>>(fun ctx id ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx

                    match parseOptionalBody ctx body with
                    | Error err -> return! Responses.fromError err ctx
                    | Ok resource ->
                        return!
                            Responses.fromResult
                                (TerminologyOps.validateCode store (Some id) (HttpRequestHelpers.queryMap ctx) resource)
                                ctx
                })
        )
        |> ignore
