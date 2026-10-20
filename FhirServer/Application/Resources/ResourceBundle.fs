namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// FHIR Bundle transaction/batch execution.
module ResourceBundle =

    let executeBundle (store: IResourceStore) (bundle: FhirResource) : FhirResult<FhirResource> =
        match FhirJson.propString bundle "type" with
        | None -> Error(BadRequest "Bundle.type is required.")
        | Some bundleType when bundleType <> "transaction" && bundleType <> "batch" ->
            Error(BadRequest "Only Bundle types transaction and batch are supported.")
        | Some bundleType ->
            let isTransaction = bundleType = "transaction"

            let entries =
                match bundle["entry"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun n ->
                        match n with
                        | :? JsonObject as o -> Some o
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            let run () =
                let response =
                    FhirJson.emptyBundle
                        (if isTransaction then
                             "transaction-response"
                         else
                             "batch-response")
                        0

                response.Remove("total") |> ignore

                let urnMap = ResourceHelpers.assignBundleUrns entries

                if not urnMap.IsEmpty then
                    for entry in entries do
                        match entry["resource"] with
                        | :? JsonObject as resource -> ResourceHelpers.rewriteUrnReferences resource urnMap
                        | _ -> ()

                for entry in entries do
                    let req =
                        match entry["request"] with
                        | :? JsonObject as r -> r
                        | _ -> null

                    if isNull req then
                        failwith "Bundle entry.request.method and url are required."

                    let method = FhirJson.propString req "method" |> Option.defaultValue ""
                    let url = FhirJson.propString req "url" |> Option.defaultValue ""

                    if String.IsNullOrWhiteSpace method || String.IsNullOrWhiteSpace url then
                        failwith "Bundle entry.request.method and url are required."

                    let outEntry = JsonObject()
                    let outResp = JsonObject()

                    try
                        let path = url.TrimStart('/')

                        match method.ToUpperInvariant() with
                        | "POST" ->
                            let typeAndQuery = path.Split('?', 2)
                            let resourceType = typeAndQuery.[0]

                            let resource =
                                match entry["resource"] with
                                | :? JsonObject as r -> r
                                | _ -> failwith "POST entry requires resource"

                            let ifNoneExist =
                                if typeAndQuery.Length > 1 then
                                    Some typeAndQuery.[1]
                                else
                                    None

                            let existingBefore =
                                match ifNoneExist with
                                | Some criteria ->
                                    store.Search(resourceType, ResourceHelpers.parseCriteriaQuery criteria |> Map.add "_count" [ "1" ])
                                    |> fst
                                    |> List.tryHead
                                | None -> None

                            match ResourceCrud.conditionalCreate store resourceType resource ifNoneExist with
                            | Ok created ->
                                let httpStatus =
                                    match existingBefore with
                                    | Some existing when FhirJson.id existing = FhirJson.id created -> "200"
                                    | _ -> "201"

                                outEntry["resource"] <- FhirJson.deepClone created
                                outResp["status"] <- httpStatus
                                outResp["location"] <- $"{resourceType}/{FhirJson.id created}"
                                outResp["etag"] <- FhirJson.versionId created
                            | Error err -> failwith (string err)
                        | "PUT" ->
                            let typeAndQuery = path.Split('?', 2)

                            let resource =
                                match entry["resource"] with
                                | :? JsonObject as r -> r
                                | _ -> failwith "PUT entry requires resource"

                            let result =
                                if typeAndQuery.Length > 1 then
                                    ResourceCrud.conditionalUpdate store typeAndQuery.[0] typeAndQuery.[1] resource None
                                else
                                    let parts = typeAndQuery.[0].Split('/')

                                    if parts.Length < 2 then
                                        Error(BadRequest "PUT url must be Type/id or Type?ResourceSearch.search")
                                    else
                                        ResourceCrud.update store parts.[0] parts.[1] resource None

                            match result with
                            | Ok updated ->
                                outEntry["resource"] <- FhirJson.deepClone updated
                                outResp["status"] <- "200"
                                outResp["etag"] <- FhirJson.versionId updated
                                outResp["location"] <- $"{FhirJson.resourceType updated}/{FhirJson.id updated}"
                            | Error err -> failwith (string err)
                        | "DELETE" ->
                            let parts = path.Split('/')

                            if parts.Length < 2 then
                                failwith "DELETE url must be Type/id"

                            match ResourceCrud.delete store parts.[0] parts.[1] None with
                            | Ok deleted ->
                                outEntry["resource"] <- FhirJson.deepClone deleted
                                outResp["status"] <- "200"
                                outResp["etag"] <- FhirJson.versionId deleted
                            | Error err -> failwith (string err)
                        | "GET" ->
                            let parts = path.Split('?').[0].Split('/')

                            match parts with
                            | [| resourceType; id |] ->
                                match ResourceCrud.read store resourceType id with
                                | Ok resource ->
                                    outEntry["resource"] <- FhirJson.deepClone resource
                                    outResp["status"] <- "200"
                                | Error err -> failwith (string err)
                            | [| resourceType |] ->
                                match ResourceSearch.search store resourceType Map.empty with
                                | Ok searchBundle ->
                                    outEntry["resource"] <- FhirJson.deepClone searchBundle
                                    outResp["status"] <- "200"
                                | Error err -> failwith (string err)
                            | _ -> failwith $"Unsupported GET url '{url}'"
                        | other -> failwith $"Unsupported HTTP verb '{other}'"
                    with ex ->
                        if isTransaction then
                            raise ex
                        else
                            outResp["status"] <- "400"
                            outEntry["resource"] <- FhirJson.operationOutcome [ "error", "processing", ex.Message ]

                    outEntry["response"] <- outResp
                    FhirJson.addBundleEntry response outEntry |> ignore

                response

            try
                if isTransaction then
                    Ok(store.RunInTransaction run)
                else
                    Ok(run ())
            with ex ->
                Error(BadRequest $"Bundle execution failed: {ex.Message}")
