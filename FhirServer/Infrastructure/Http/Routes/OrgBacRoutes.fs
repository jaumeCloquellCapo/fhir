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

/// Aidbox-style /Organization/{orgId}/fhir/* compartment API.
module OrgBacRoutes =

    let map (app: WebApplication) =
        // Aidbox-style hierarchical OrgBAC API: /Organization/{orgId}/fhir/...
        let org = app.MapGroup("/Organization/{orgId}/fhir")

        org.MapPost(
            "/",
            Func<HttpContext, string, Task<unit>>(fun ctx _orgId ->
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
            [| OpenApiDocs.TagOrgBac |]
            "OrgBAC Bundle"
            "Execute a Bundle under an organization compartment (hierarchical visibility)."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}/_history/{vid}",
            Func<HttpContext, string, string, string, string, Task<unit>>(fun ctx _orgId resourceType id vid ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC VRead"
            "Read a historical version within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}/_history",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match Auth.ensurePatientIdAccess ctx resourceType id with
                | Error err -> Responses.fromError err ctx
                | Ok() ->
                    ResourceHandlers.historyFromQuery store (Some resourceType) (Some id) (HttpRequestHelpers.queryMap ctx)
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC instance history"
            "Instance history within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/_history",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match HttpRequestHelpers.queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.historyFromQuery store (Some resourceType) None query
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagHistory |]
            "OrgBAC type history"
            "Type history within an organization compartment."
        |> ignore

        org.MapGet(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Read"
            "Read a resource under /Organization/{orgId}/fhir (parent sees descendants)."
        |> ignore

        org.MapGet(
            "/{resourceType}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
                let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                match HttpRequestHelpers.queryMapForType ctx resourceType with
                | Error err -> Responses.fromError err ctx
                | Ok query ->
                    ResourceHandlers.search store resourceType query
                    |> fun result -> Responses.fromSearch result ctx)
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagSearch |]
            "OrgBAC Search"
            "Search within an organization compartment (hierarchical + shared visibility)."
        |> ignore

        org.MapPost(
            "/{resourceType}/$validate",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagValidate |]
            "OrgBAC Validate"
            "Validate without persisting (organization-scoped URL). Optional `profile` / `_profile` query."
        |> ignore

        org.MapPost(
            "/{resourceType}",
            Func<HttpContext, string, string, Task<unit>>(fun ctx _orgId resourceType ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Create"
            "Create under an organization; stamps tenant_id and optional shared mode."
        |> ignore

        org.MapPut(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Update"
            "Update under an organization compartment."
        |> ignore

        org.MapMethods(
            "/{resourceType}/{id}",
            [ HttpMethods.Patch ],
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Patch"
            "JSON Patch under an organization compartment."
        |> ignore

        org.MapDelete(
            "/{resourceType}/{id}",
            Func<HttpContext, string, string, string, Task<unit>>(fun ctx _orgId resourceType id ->
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
            [| OpenApiDocs.TagOrgBac; OpenApiDocs.TagCrud |]
            "OrgBAC Delete"
            "Soft-delete under an organization compartment."
        |> ignore
