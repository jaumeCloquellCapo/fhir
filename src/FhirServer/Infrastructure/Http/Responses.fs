namespace FhirServer.Infrastructure.Http

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open FhirServer.Domain
open FhirServer.Domain.Types

module Responses =

    let private formatOf (ctx: HttpContext) =
        let accept =
            if ctx.Request.Headers.Accept.Count = 0 then
                None
            else
                Some(string ctx.Request.Headers.Accept)

        let contentType =
            if String.IsNullOrWhiteSpace ctx.Request.ContentType then
                None
            else
                Some ctx.Request.ContentType

        Codec.negotiateFormat accept contentType

    let private writeFhir (status: int) (resource: FhirResource) (ctx: HttpContext) =
        task {
            let format = formatOf ctx
            let payload = Codec.serialize format resource
            ctx.Response.StatusCode <- status
            ctx.Response.ContentType <- Codec.mediaType format

            let versionId = FhirJson.versionId resource

            if not (String.IsNullOrWhiteSpace versionId) then
                ctx.Response.Headers.ETag <- versionId

            match FhirJson.lastUpdated resource with
            | Some ts -> ctx.Response.Headers.LastModified <- ts.UtcDateTime.ToString("R")
            | None -> ()

            do! ctx.Response.WriteAsync(payload)
        }

    let private preferOf (ctx: HttpContext) =
        let prefer =
            if ctx.Request.Headers.ContainsKey("Prefer") then
                Some(string ctx.Request.Headers.["Prefer"])
            else
                None

        Codec.parsePrefer prefer

    let fhirOk (resource: FhirResource) (ctx: HttpContext) =
        match preferOf ctx with
        | ReturnMinimal ->
            task {
                ctx.Response.StatusCode <- StatusCodes.Status200OK
                let versionId = FhirJson.versionId resource

                if not (String.IsNullOrWhiteSpace versionId) then
                    ctx.Response.Headers.ETag <- versionId
            }
        | _ -> writeFhir StatusCodes.Status200OK resource ctx

    let fhirCreated (location: string) (resource: FhirResource) (ctx: HttpContext) =
        task {
            ctx.Response.Headers.Location <- location

            match preferOf ctx with
            | ReturnMinimal ->
                ctx.Response.StatusCode <- StatusCodes.Status201Created
                let versionId = FhirJson.versionId resource

                if not (String.IsNullOrWhiteSpace versionId) then
                    ctx.Response.Headers.ETag <- versionId
            | ReturnOutcome ->
                let outcome =
                    FhirJson.operationOutcome [ "information", "informational", $"Created {location}" ]

                return! writeFhir StatusCodes.Status201Created outcome ctx
            | ReturnRepresentation -> return! writeFhir StatusCodes.Status201Created resource ctx
        }

    let fhirNoContent (ctx: HttpContext) =
        task { ctx.Response.StatusCode <- StatusCodes.Status204NoContent }

    let fromError (error: FhirError) (ctx: HttpContext) =
        let status =
            match error with
            | NotFound _
            | VersionNotFound _ -> StatusCodes.Status404NotFound
            | Gone _ -> StatusCodes.Status410Gone
            | BadRequest _
            | UnsupportedType _ -> StatusCodes.Status400BadRequest
            | Forbidden _ -> StatusCodes.Status403Forbidden
            | Conflict _ -> StatusCodes.Status409Conflict
            | PreconditionFailed _ -> StatusCodes.Status412PreconditionFailed
            | ValidationFailed _ -> StatusCodes.Status422UnprocessableEntity

        writeFhir status (Codec.toOperationOutcome error) ctx

    let fromResult (result: FhirResult<FhirResource>) (ctx: HttpContext) =
        match result with
        | Ok resource -> fhirOk resource ctx
        | Error error -> fromError error ctx

    let fromCreated (resourceType: string) (result: FhirResult<FhirResource>) (ctx: HttpContext) =
        match result with
        | Ok resource ->
            let id = FhirJson.id resource
            let vid = FhirJson.versionId resource
            fhirCreated $"/{resourceType}/{id}/_history/{vid}" resource ctx
        | Error error -> fromError error ctx

    let fromDelete (result: FhirResult<FhirResource>) (ctx: HttpContext) =
        match result with
        | Ok resource -> fhirOk resource ctx
        | Error error -> fromError error ctx

    let fromSearch (result: FhirResult<FhirResource>) (ctx: HttpContext) =
        match result with
        | Ok bundle -> fhirOk bundle ctx
        | Error error -> fromError error ctx
