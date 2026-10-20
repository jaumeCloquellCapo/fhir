namespace FhirServer.Infrastructure.Http

open System
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Services

module Codec =

    let FhirJsonMediaType = "application/fhir+json"

    type FhirFormat = | Json

    let negotiateFormat (_accept: string option) (_contentType: string option) = Json

    let serialize (_format: FhirFormat) (resource: FhirResource) = FhirJson.toJson resource

    let mediaType =
        function
        | Json -> FhirJsonMediaType

    let tryDeserialize (_format: FhirFormat) (body: string) : FhirResult<FhirResource> =
        match FhirJson.parse body with
        | Ok resource -> Ok resource
        | Error msg -> Error(BadRequest msg)

    let toOperationOutcome (error: FhirError) : FhirResource =
        match error with
        | ValidationFailed issues -> ResourceValidator.toOperationOutcome issues
        | NotFound(resourceType, id) ->
            FhirJson.operationOutcome [ "error", "not-found", $"Resource {resourceType}/{id} was not found." ]
        | Gone(resourceType, id, vid) ->
            let diagnostics =
                match vid with
                | Some v -> $"Resource {resourceType}/{id} (version {v}) not found"
                | None -> $"Resource {resourceType}/{id} not found"

            let oo = FhirJson.operationOutcome [ "fatal", "deleted", diagnostics ]
            oo["id"] <- "deleted"
            oo
        | VersionNotFound(resourceType, id, vid) ->
            FhirJson.operationOutcome [
                "error", "not-found", $"Version {resourceType}/{id}/_history/{vid} was not found."
            ]
        | BadRequest message -> FhirJson.operationOutcome [ "error", "invalid", message ]
        | Forbidden message -> FhirJson.operationOutcome [ "error", "forbidden", message ]
        | UnsupportedType resourceType ->
            FhirJson.operationOutcome [
                "error", "not-supported", $"Resource type '{resourceType}' is not supported."
            ]
        | Conflict message -> FhirJson.operationOutcome [ "error", "conflict", message ]
        | PreconditionFailed message -> FhirJson.operationOutcome [ "error", "conflict", message ]

    let parsePrefer (header: string option) =
        match header with
        | Some value when value.Contains("return=minimal", StringComparison.OrdinalIgnoreCase) -> ReturnMinimal
        | Some value when value.Contains("return=OperationOutcome", StringComparison.OrdinalIgnoreCase) -> ReturnOutcome
        | _ -> ReturnRepresentation
