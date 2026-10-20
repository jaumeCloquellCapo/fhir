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

/// FHIR CRUD + type search.
module CrudRoutes =

    let map (app: WebApplication) =
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

                match HttpRequestHelpers.queryMapForType ctx resourceType with
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
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx
                    let profiles = HttpRequestHelpers.validateProfileUrls ctx

                    let result =
                        ResourceHandlers.validateRaw store (Some resourceType) body profiles

                    return! Responses.fromResult result ctx
                })
        )
        |> OpenApiDocs.documentFhirValidate
            [| OpenApiDocs.TagValidate |]
            "Validate (type)"
            "Validate a resource of the given type without persisting. Optional `profile` / `_profile` query."
        |> ignore

        app.MapPost(
            "/{resourceType}",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                    let! body = HttpRequestHelpers.readBody ctx

                    let result =
                        Codec.tryDeserialize (HttpRequestHelpers.requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            Auth.ensureResourceAccess ctx resource
                            |> Result.bind (fun () ->
                                ResourceHandlers.conditionalCreate
                                    store
                                    resourceType
                                    resource
                                    (HttpRequestHelpers.header ctx "If-None-Exist")))

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
                    let! body = HttpRequestHelpers.readBody ctx

                    let result =
                        Codec.tryDeserialize (HttpRequestHelpers.requestFormat ctx) body
                        |> Result.bind (fun resource ->
                            Auth.ensurePatientIdAccess ctx resourceType id
                            |> Result.bind (fun () -> Auth.ensureResourceAccess ctx resource)
                            |> Result.bind (fun () ->
                                ResourceHandlers.update store resourceType id resource (HttpRequestHelpers.header ctx "If-Match")))

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
                    let! body = HttpRequestHelpers.readBody ctx

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
                            ResourceHandlers.delete store resourceType id (HttpRequestHelpers.header ctx "If-Match")
                            |> fun result -> Responses.fromDelete result ctx)
        )
        |> OpenApiDocs.documentFhirDelete
            [| OpenApiDocs.TagCrud |]
            "Delete"
            "Soft-delete via history. Returns 200 + deleted resource body."
        |> ignore
