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
    let private jobTtl = TimeSpan.FromHours(2.0)

    let private defaultTypes =
        [ "Patient"; "Observation"; "Encounter"; "Condition" ]
        |> List.filter ResourceCatalog.isSupported

    let private evictExpired () =
        let cutoff = DateTimeOffset.UtcNow - jobTtl

        for kv in jobs do
            if kv.Value.RequestedAt < cutoff then
                jobs.TryRemove(kv.Key) |> ignore

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
        evictExpired ()
        let id = Guid.NewGuid().ToString("N")

        let job = {
            Id = id
            ResourceTypes = resourceTypes
            RequestedAt = DateTimeOffset.UtcNow
        }

        jobs[id] <- job
        job

    let tryGet (jobId: string) =
        evictExpired ()

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
            "requiresAccessToken", FhirJson.jBool true
            "output", output
            "error", JsonArray()
        ]

    /// Stream NDJSON lines via callback (keyset pages — no full ListByType).
    let streamFileNdjson
        (store: IResourceStore)
        (jobId: string)
        (resourceType: string)
        (writeLine: string -> unit)
        : FhirResult<unit> =
        match tryGet jobId with
        | None -> Error(NotFound("BulkExport", jobId))
        | Some job when not (List.contains resourceType job.ResourceTypes) ->
            Error(BadRequest $"Resource type '{resourceType}' is not part of export job '{jobId}'.")
        | Some _ ->
            if not (ResourceCatalog.isSupported resourceType) then
                Error(UnsupportedType resourceType)
            else
                let mutable after: string option = None
                let mutable cont = true

                while cont do
                    let page = store.ListByTypePage(resourceType, after, 500)

                    match page with
                    | [] -> cont <- false
                    | rows ->
                        for resource in rows do
                            writeLine (FhirJson.toJson resource)

                        after <- rows |> List.tryLast |> Option.map FhirJson.id

                Ok()

    [<Obsolete("Use streamFileNdjson")>]
    let fileNdjson (store: IResourceStore) (jobId: string) (resourceType: string) : FhirResult<string> =
        let sb = System.Text.StringBuilder()

        streamFileNdjson store jobId resourceType (fun line -> sb.AppendLine(line) |> ignore)
        |> Result.map (fun () -> sb.ToString())
