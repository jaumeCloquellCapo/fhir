namespace FhirServer.Application

open System
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Domain.Types

/// Synchronous NDJSON bulk import (`$import` / `$load`).
module BulkImport =

    type private Counters = {
        mutable Created: int
        mutable Updated: int
        mutable Errors: int
    }

    let private parameter (name: string) (value: JsonNode) =
        FhirJson.mkObject [ "name", FhirJson.jStr name; "valueInteger", value ]

    let private upsertOne (store: IResourceStore) (resource: FhirResource) =
        let resourceType = FhirJson.resourceType resource

        if String.IsNullOrWhiteSpace resourceType then
            Error(BadRequest "Each NDJSON line must include resourceType.")
        elif not (ResourceCatalog.isSupported resourceType) then
            Error(UnsupportedType resourceType)
        else
            ResourceValidator.ensureValid store (Some resourceType) resource
            |> Result.bind (fun () ->
                let id = FhirJson.id resource

                if String.IsNullOrWhiteSpace id then
                    ResourceHandlers.create store resourceType resource
                    |> Result.map (fun created -> true, created)
                else
                    match store.Get(resourceType, id) with
                    | Some _ ->
                        ResourceHandlers.update store resourceType id resource None
                        |> Result.map (fun updated -> false, updated)
                    | None ->
                        ResourceHandlers.create store resourceType resource
                        |> Result.map (fun created -> true, created))

    /// Import NDJSON body. Returns FHIR Parameters with created/updated/errors counts.
    let importNdjson (store: IResourceStore) (ndjson: string) : FhirResult<FhirResource> =
        if String.IsNullOrWhiteSpace ndjson then
            Error(BadRequest "NDJSON body is required.")
        else
            let lines =
                ndjson.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun l -> l.Trim())
                |> Array.filter (fun l -> l.Length > 0 && not (l.StartsWith('#')))

            let counts = { Created = 0; Updated = 0; Errors = 0 }
            let issues = ResizeArray<string * string * string>()

            for line in lines do
                match FhirJson.parse line with
                | Error msg ->
                    counts.Errors <- counts.Errors + 1
                    issues.Add("error", "invalid", msg)
                | Ok resource ->
                    match upsertOne store resource with
                    | Ok(true, _) -> counts.Created <- counts.Created + 1
                    | Ok(false, _) -> counts.Updated <- counts.Updated + 1
                    | Error err ->
                        counts.Errors <- counts.Errors + 1
                        issues.Add("error", "processing", string err)

            let parameters = JsonArray()
            parameters.Add(parameter "created" (FhirJson.jInt counts.Created))
            parameters.Add(parameter "updated" (FhirJson.jInt counts.Updated))
            parameters.Add(parameter "errors" (FhirJson.jInt counts.Errors))
            parameters.Add(parameter "total" (FhirJson.jInt lines.Length))

            if issues.Count > 0 then
                let outcome = FhirJson.operationOutcome (issues |> Seq.toList)

                parameters.Add(FhirJson.mkObject [ "name", FhirJson.jStr "outcome"; "resource", outcome ])

            Ok(FhirJson.mkObject [ "resourceType", FhirJson.jStr "Parameters"; "parameter", parameters ])
