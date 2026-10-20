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

/// System/type/instance history, vread, root Bundle POST.
module HistoryBundleRoutes =

    let map (app: WebApplication) =
        app.MapGet(
            "/_history",
            Func<HttpContext, Task<unit>>(fun ctx ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                // System history: patient compartment tokens are not allowed to browse all types.
                match Auth.tryGetAuthContext ctx with
                | Some auth when auth.CompartmentPatientId.IsSome ->
                    Responses.fromError (Forbidden "System history requires system/ scopes") ctx
                | _ ->
                    ResourceHandlers.historyFromQuery store None None (HttpRequestHelpers.queryMap ctx)
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
                    let! body = HttpRequestHelpers.readBody ctx

                    let result =
                        Codec.tryDeserialize (HttpRequestHelpers.requestFormat ctx) body
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

                match HttpRequestHelpers.queryMapForType ctx resourceType with
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
                    ResourceHandlers.historyFromQuery store (Some resourceType) (Some id) (HttpRequestHelpers.queryMap ctx)
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagHistory |]
            "Instance history"
            "History Bundle for one resource id."
        |> ignore
