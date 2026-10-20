namespace FhirServer.Application

open System
open System.Collections.Concurrent
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Types

/// In-memory FHIR Bulk Data Export jobs (MVP — completes synchronously).
module BulkExport =

    type ExportJob = {
        Id: string
        ResourceTypes: string list
        RequestedAt: DateTimeOffset
    }

    let private jobs = ConcurrentDictionary<string, ExportJob>()

    let private defaultTypes =
        [ "Patient"; "Observation"; "Encounter"; "Condition" ]
        |> List.filter ResourceCatalog.isSupported

    let parseTypes (raw: string option) =
        match raw with
        | None
        | Some "" -> defaultTypes
        | Some value ->
            value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList
            |> List.filter ResourceCatalog.isSupported
            |> function
                | [] -> defaultTypes
                | xs -> xs

    let start (resourceTypes: string list) =
        let id = Guid.NewGuid().ToString("N")

        let job = {
            Id = id
            ResourceTypes = resourceTypes
            RequestedAt = DateTimeOffset.UtcNow
        }

        jobs[id] <- job
        job

    let tryGet (jobId: string) =
        match jobs.TryGetValue jobId with
        | true, job -> Some job
        | _ -> None

    let statusResource (baseUrl: string) (job: ExportJob) =
        let output = JsonArray()

        for rt in job.ResourceTypes do
            output.Add(
                FhirJson.mkObject [
                    "type", FhirJson.jStr rt
                    "url", FhirJson.jStr $"{baseUrl.TrimEnd('/')}/$export-files/{job.Id}/{rt}"
                ]
            )

        FhirJson.mkObject [
            "transactionTime", FhirJson.jStr (job.RequestedAt.ToUniversalTime().ToString("o"))
            "request", FhirJson.jStr $"{baseUrl.TrimEnd('/')}/$export"
            "requiresAccessToken", FhirJson.jBool false
            "output", output
            "error", JsonArray()
        ]

    let fileNdjson (store: IResourceStore) (jobId: string) (resourceType: string) : FhirResult<string> =
        match tryGet jobId with
        | None -> Error(NotFound("BulkExport", jobId))
        | Some job when not (List.contains resourceType job.ResourceTypes) ->
            Error(BadRequest $"Resource type '{resourceType}' is not part of export job '{jobId}'.")
        | Some _ ->
            if not (ResourceCatalog.isSupported resourceType) then
                Error(UnsupportedType resourceType)
            else
                let body =
                    store.ListByType resourceType |> List.map FhirJson.toJson |> String.concat "\n"

                Ok(if String.IsNullOrEmpty body then body else body + "\n")
