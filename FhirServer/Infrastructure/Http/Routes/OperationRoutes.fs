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

/// $dump, $lastn, $everything, $document.
module OperationRoutes =

    let map (app: WebApplication) =
        app.MapGet(
            "/{resourceType}/$dump",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                    if not (ResourceCatalog.isSupported resourceType) then
                        return! Responses.fromError (UnsupportedType resourceType) ctx
                    else
                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "application/fhir+ndjson"
                        let mutable after: string option = None
                        let mutable cont = true

                        while cont do
                            let page = store.ListByTypePage(resourceType, after, 500)

                            match page with
                            | [] -> cont <- false
                            | rows ->
                                for resource in rows do
                                    do! ctx.Response.WriteAsync(FhirJson.toJson resource + "\n")

                                after <- rows |> List.tryLast |> Option.map FhirJson.id
                })
        )
        |> OpenApiDocs.documentNdjson
            [| OpenApiDocs.TagOperations |]
            "$dump"
            "Stream all current resources of a type as FHIR NDJSON."
        |> ignore

        app.MapGet(
            "/{resourceType}/$lastn",
            Func<HttpContext, string, Task<unit>>(fun ctx resourceType ->
                task {
                    if not (resourceType.Equals("Observation", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$lastn is only supported on Observation.") ctx
                    else
                        let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                        match HttpRequestHelpers.queryMapForType ctx "Observation" with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok query ->
                            return! Responses.fromSearch (ResourceHandlers.observationLastN store query) ctx
                })
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOperations |]
            "$lastn (Observation)"
            "Most recent Observations matching search criteria (Observation only)."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/$everything",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    if not (resourceType.Equals("Patient", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$everything is only supported on Patient.") ctx
                    else
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok() ->
                            let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                            return! Responses.fromSearch (ResourceHandlers.patientEverything store id) ctx
                })
        )
        |> OpenApiDocs.documentFhirSearch
            [| OpenApiDocs.TagOperations |]
            "$everything (Patient)"
            "Patient clinical summary Bundle (Patient only)."
        |> ignore

        app.MapGet(
            "/{resourceType}/{id}/$document",
            Func<HttpContext, string, string, Task<unit>>(fun ctx resourceType id ->
                task {
                    if not (resourceType.Equals("Patient", StringComparison.Ordinal)) then
                        return! Responses.fromError (BadRequest "$document is only supported on Patient.") ctx
                    else
                        match Auth.ensurePatientIdAccess ctx resourceType id with
                        | Error err -> return! Responses.fromError err ctx
                        | Ok() ->
                            let store = ctx.RequestServices.GetRequiredService<IResourceStore>()
                            return! Responses.fromResult (ResourceHandlers.patientDocument store id) ctx
                })
        )
        |> OpenApiDocs.documentFhir
            [| OpenApiDocs.TagOperations |]
            "$document (Patient)"
            "FHIR Document Bundle (Composition + clinical resources) for a Patient."
        |> ignore
