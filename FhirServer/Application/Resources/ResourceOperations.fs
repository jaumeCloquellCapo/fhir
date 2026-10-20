namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// Clinical operations: $everything, $document, dump.
module ResourceOperations =

    let patientEverything (store: IResourceStore) (id: string) : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType "Patient"
        |> Result.bind (fun () ->
            match store.Get("Patient", id) with
            | None when store.WasDeleted("Patient", id) -> Error(Gone("Patient", id, None))
            | None -> Error(NotFound("Patient", id))
            | Some patient ->
                let relatedTypes = [
                    "Observation"
                    "Encounter"
                    "Condition"
                    "AllergyIntolerance"
                    "MedicationRequest"
                    "Procedure"
                    "DiagnosticReport"
                    "Immunization"
                    "CarePlan"
                    "DocumentReference"
                ]

                let query = Map.ofList [ "patient", [ id ] ]

                let related =
                    relatedTypes
                    |> List.collect (fun rt ->
                        if ResourceCatalog.isSupported rt then
                            store.Search(rt, query |> Map.add "_count" [ string ResourceHelpers.internalFetchCap ]) |> fst
                        else
                            [])

                let bundle = FhirJson.emptyBundle "searchset" (1 + related.Length)

                ignore (
                    FhirJson.addBundleEntry
                        bundle
                        (FhirJson.mkObject [
                            "fullUrl", FhirJson.jStr $"Patient/{id}"
                            "resource", FhirJson.deepClone patient
                            "search", FhirJson.mkObject [ "mode", FhirJson.jStr "match" ]
                        ])
                )

                for resource in related do
                    let rt = FhirJson.resourceType resource

                    ignore (
                        FhirJson.addBundleEntry
                            bundle
                            (FhirJson.mkObject [
                                "fullUrl", FhirJson.jStr $"{rt}/{FhirJson.id resource}"
                                "resource", FhirJson.deepClone resource
                                "search", FhirJson.mkObject [ "mode", FhirJson.jStr "match" ]
                            ])
                    )

                Ok bundle)

    /// Prefer AppHost streaming via ListByTypePage; kept for callers that need a full list (tests).
    let dump (store: IResourceStore) (resourceType: string) : FhirResult<FhirResource list> =
        ResourceHelpers.ensureKnownResourceType resourceType
        |> Result.map (fun () ->
            let acc = ResizeArray<FhirResource>()
            let mutable after: string option = None
            let mutable cont = true

            while cont do
                let page = store.ListByTypePage(resourceType, after, 500)

                match page with
                | [] -> cont <- false
                | rows ->
                    acc.AddRange(rows)
                    after <- rows |> List.tryLast |> Option.map FhirJson.id

            acc |> Seq.toList)

    /// FHIR Document Bundle for a Patient (Composition + clinical resources).
    let patientDocument (store: IResourceStore) (id: string) : FhirResult<FhirResource> =
        patientEverything store id
        |> Result.map (fun everything ->
            let now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")

            let resources =
                match everything["entry"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun n ->
                        match n with
                        | :? JsonObject as entry ->
                            match entry["resource"] with
                            | :? JsonObject as r -> Some r
                            | _ -> None
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            let sectionEntries = JsonArray()

            for resource in resources do
                let rt = FhirJson.resourceType resource
                let rid = FhirJson.id resource

                sectionEntries.Add(FhirJson.mkObject [ "reference", FhirJson.jStr $"{rt}/{rid}" ])

            let compositionId = Guid.NewGuid().ToString("N")

            let composition =
                FhirJson.mkObject [
                    "resourceType", FhirJson.jStr "Composition"
                    "id", FhirJson.jStr compositionId
                    "status", FhirJson.jStr "final"
                    "type",
                    FhirJson.mkObject [
                        "coding",
                        JsonArray(
                            FhirJson.mkObject [
                                "system", FhirJson.jStr "http://loinc.org"
                                "code", FhirJson.jStr "34133-9"
                                "display", FhirJson.jStr "Summary of episode note"
                            ]
                        )
                    ]
                    "subject", FhirJson.mkObject [ "reference", FhirJson.jStr $"Patient/{id}" ]
                    "date", FhirJson.jStr now
                    "author", JsonArray(FhirJson.mkObject [ "display", FhirJson.jStr "FhirServer" ])
                    "title", FhirJson.jStr $"Clinical document for Patient/{id}"
                    "section",
                    JsonArray(FhirJson.mkObject [ "title", FhirJson.jStr "Patient summary"; "entry", sectionEntries ])
                ]

            let bundle = FhirJson.emptyBundle "document" (1 + resources.Length)
            bundle.Remove("total") |> ignore

            ignore (
                FhirJson.addBundleEntry
                    bundle
                    (FhirJson.mkObject [
                        "fullUrl", FhirJson.jStr $"Composition/{compositionId}"
                        "resource", composition
                    ])
            )

            for resource in resources do
                let rt = FhirJson.resourceType resource

                ignore (
                    FhirJson.addBundleEntry
                        bundle
                        (FhirJson.mkObject [
                            "fullUrl", FhirJson.jStr $"{rt}/{FhirJson.id resource}"
                            "resource", FhirJson.deepClone resource
                        ])
                )

            bundle)
