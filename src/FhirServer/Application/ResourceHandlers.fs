namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

module ResourceHandlers =

    let private ensureKnownResourceType (resourceType: string) : FhirResult<unit> =
        if ResourceCatalog.isSupported resourceType then
            Ok()
        else
            Error(UnsupportedType resourceType)

    let private firstQuery (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.bind List.tryHead

    let private parseCountOffset (query: Map<string, string list>) =
        let count =
            firstQuery query "_count"
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n > 0 -> Some(min n 200)
                | _ -> None)
            |> Option.defaultValue 50

        let offset =
            firstQuery query "_offset"
            |> Option.orElse (firstQuery query "_getpagesoffset")
            |> Option.bind (fun s ->
                match Int32.TryParse s with
                | true, n when n >= 0 -> Some n
                | _ -> None)
            |> Option.defaultValue 0

        count, offset

    let private parseSince (query: Map<string, string list>) =
        firstQuery query "_since"
        |> Option.bind (fun s ->
            match DateTimeOffset.TryParse s with
            | true, dto -> Some dto
            | _ -> None)

    let private normalizeVersionToken (token: string) =
        let t = token.Trim()

        if t.StartsWith("W/", StringComparison.OrdinalIgnoreCase) then
            t.Substring(2).Trim().Trim('"')
        else
            t.Trim('"')

    let private checkIfMatch (existing: FhirResource option) (ifMatch: string option) =
        match ifMatch with
        | None -> Ok()
        | Some expected ->
            let normalized = normalizeVersionToken expected

            match existing with
            | None -> Error(PreconditionFailed "If-Match provided but resource does not exist.")
            | Some resource when FhirJson.versionId resource <> normalized ->
                Error(PreconditionFailed $"Version mismatch. Current version is '{FhirJson.versionId resource}'.")
            | Some _ -> Ok()

    let private toSearchBundle
        (resourceType: string)
        (matches: FhirResource list)
        (includes: FhirResource list)
        (total: int)
        =
        let bundle = FhirJson.emptyBundle "searchset" total

        for resource in matches do
            let entry =
                FhirJson.mkObject [
                    "fullUrl", FhirJson.jStr $"{resourceType}/{FhirJson.id resource}"
                    "resource", FhirJson.deepClone resource
                    "search", FhirJson.mkObject [ "mode", FhirJson.jStr "match" ]
                ]

            ignore (FhirJson.addBundleEntry bundle entry)

        for resource in includes do
            let rt = FhirJson.resourceType resource

            let entry =
                FhirJson.mkObject [
                    "fullUrl", FhirJson.jStr $"{rt}/{FhirJson.id resource}"
                    "resource", FhirJson.deepClone resource
                    "search", FhirJson.mkObject [ "mode", FhirJson.jStr "include" ]
                ]

            ignore (FhirJson.addBundleEntry bundle entry)

        bundle

    let private rewriteUrnReferences (node: JsonNode) (urnMap: Map<string, string>) =
        let rec walk (n: JsonNode) =
            match n with
            | :? JsonObject as o ->
                let snapshot =
                    o
                    |> Seq.map (fun (kv: System.Collections.Generic.KeyValuePair<string, JsonNode>) -> kv.Key, kv.Value)
                    |> Seq.toList

                for key, child in snapshot do
                    if
                        key = "reference"
                        && not (isNull child)
                        && child.GetValueKind() = JsonValueKind.String
                    then
                        let value = child.GetValue<string>()

                        match urnMap |> Map.tryFind value with
                        | Some resolved -> o[key] <- resolved
                        | None -> ()
                    elif not (isNull child) then
                        walk child
            | :? JsonArray as arr ->
                for child in arr do
                    if not (isNull child) then
                        walk child
            | _ -> ()

        walk node

    let private assignBundleUrns (entries: JsonObject list) =
        entries
        |> List.fold
            (fun (urnMap: Map<string, string>) entry ->
                match FhirJson.propString entry "fullUrl" with
                | Some fullUrl when fullUrl.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ->
                    match entry["resource"] with
                    | :? JsonObject as resource ->
                        let rt = FhirJson.resourceType resource

                        let id =
                            let current = FhirJson.id resource

                            if String.IsNullOrWhiteSpace current then
                                Guid.NewGuid().ToString("N")
                            else
                                current

                        ignore (FhirJson.setId resource id)
                        urnMap.Add(fullUrl, $"{rt}/{id}")
                    | _ -> urnMap
                | _ -> urnMap)
            Map.empty

    let private toHistoryBundle (entries: HistoryEntry list) (total: int) =
        let bundle = FhirJson.emptyBundle "history" total

        for entry in entries do
            let method, url, status =
                match entry.Status with
                | Created -> "POST", entry.ResourceType, "201"
                | Updated
                | Recreated -> "PUT", $"{entry.ResourceType}/{entry.Id}", "200"
                | Deleted -> "DELETE", $"{entry.ResourceType}/{entry.Id}", "204"

            let entryObj =
                FhirJson.mkObject [
                    "fullUrl", FhirJson.jStr $"{entry.ResourceType}/{entry.Id}"
                    "request", FhirJson.mkObject [ "method", FhirJson.jStr method; "url", FhirJson.jStr url ]
                    "response",
                    FhirJson.mkObject [
                        "status", FhirJson.jStr status
                        "etag", FhirJson.jStr (string entry.Txid)
                        "lastModified", FhirJson.jStr (entry.Timestamp.ToString("o"))
                    ]
                ]

            match entry.Resource with
            | Some resource -> entryObj["resource"] <- FhirJson.deepClone resource
            | None -> ()

            ignore (FhirJson.addBundleEntry bundle entryObj)

        bundle

    let read (store: IResourceStore) (resourceType: string) (id: string) : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            match store.Get(resourceType, id) with
            | Some resource -> Ok resource
            | None when store.WasDeleted(resourceType, id) -> Error(Gone(resourceType, id, None))
            | None -> Error(NotFound(resourceType, id)))

    let vread (store: IResourceStore) (resourceType: string) (id: string) (vid: string) : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            match Int64.TryParse vid with
            | false, _ -> Error(BadRequest $"Invalid version id '{vid}'.")
            | true, txid ->
                match store.GetVersion(resourceType, id, txid) with
                | Some entry ->
                    match entry.Status, entry.Resource with
                    | Deleted, _ -> Error(Gone(resourceType, id, Some vid))
                    | _, Some resource -> Ok resource
                    | _, None -> Error(VersionNotFound(resourceType, id, vid))
                | None -> Error(VersionNotFound(resourceType, id, vid)))

    let create (store: IResourceStore) (resourceType: string) (body: FhirResource) : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () -> ResourceValidator.ensureValid (Some resourceType) body)
        |> Result.bind (fun () ->
            let id =
                let current = FhirJson.id body

                if String.IsNullOrWhiteSpace current then
                    Guid.NewGuid().ToString("N")
                else
                    current

            match store.Get(resourceType, id) with
            | Some _ -> Error(Conflict $"Resource {resourceType}/{id} already exists.")
            | None ->
                body["resourceType"] <- resourceType
                FhirJson.setId body id |> ignore
                let created = store.Upsert body
                SubscriptionNotifier.notifyChange store "create" created
                Ok created)

    let update
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        (body: FhirResource)
        (ifMatch: string option)
        : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () -> ResourceValidator.ensureValid (Some resourceType) body)
        |> Result.bind (fun () ->
            let existing = store.Get(resourceType, id)

            checkIfMatch existing ifMatch
            |> Result.bind (fun () ->
                body["resourceType"] <- resourceType
                FhirJson.setId body id |> ignore
                let updated = store.Upsert body
                SubscriptionNotifier.notifyChange store "update" updated
                Ok updated))

    let validateRaw (resourceType: string option) (body: string) : FhirResult<FhirResource> =
        match resourceType with
        | Some rt ->
            ensureKnownResourceType rt
            |> Result.map (fun () ->
                ResourceValidator.validateJson (Some rt) body
                |> ResourceValidator.toOperationOutcome)
        | None -> Ok(ResourceValidator.validateJson None body |> ResourceValidator.toOperationOutcome)

    let delete
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        (ifMatch: string option)
        : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            let existing = store.Get(resourceType, id)

            checkIfMatch existing ifMatch
            |> Result.bind (fun () ->
                match store.Delete(resourceType, id) with
                | Some deleted ->
                    SubscriptionNotifier.notifyChange store "delete" deleted
                    Ok deleted
                | None when store.WasDeleted(resourceType, id) -> Error(Gone(resourceType, id, None))
                | None -> Error(NotFound(resourceType, id))))

    let private parseCriteriaQuery (criteria: string) =
        criteria.Split('&')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| k; v |] -> Some(Uri.UnescapeDataString k, [ Uri.UnescapeDataString v ])
            | _ -> None)
        |> Map.ofArray

    let search
        (store: IResourceStore)
        (resourceType: string)
        (query: Map<string, string list>)
        : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            let includeTokens = query |> Map.tryFind "_include" |> Option.defaultValue []
            let revIncludeTokens = query |> Map.tryFind "_revinclude" |> Option.defaultValue []

            let rec searchList (rt: string, q: Map<string, string list>) =
                if not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let expanded =
                        q
                        |> Map.add "_count" [ "10000" ]
                        |> SearchEngine.expandChainedQuery searchList
                        |> SearchEngine.expandHasQuery searchList

                    store.Search(rt, expanded) |> fst

            let filterQuery =
                query
                |> Map.remove "_include"
                |> Map.remove "_revinclude"
                |> SearchEngine.expandChainedQuery searchList
                |> SearchEngine.expandHasQuery searchList

            let page, total = store.Search(resourceType, filterQuery)
            let includes =
                SearchEngine.resolveIncludes (fun (rt, id) -> store.Get(rt, id)) resourceType includeTokens page

            let revincludes =
                SearchEngine.resolveRevincludes searchList resourceType revIncludeTokens page

            Ok(toSearchBundle resourceType page (includes @ revincludes) total))

    let patientEverything (store: IResourceStore) (id: string) : FhirResult<FhirResource> =
        ensureKnownResourceType "Patient"
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
                            store.Search(rt, query |> Map.add "_count" [ "10000" ]) |> fst
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

    let dump (store: IResourceStore) (resourceType: string) : FhirResult<FhirResource list> =
        ensureKnownResourceType resourceType
        |> Result.map (fun () -> store.ListByType resourceType)

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

    let history (store: IResourceStore) (filter: HistoryFilter) : FhirResult<FhirResource> =
        match filter.ResourceType with
        | Some rt ->
            ensureKnownResourceType rt
            |> Result.bind (fun () ->
                let entries, total = store.ListHistory filter
                Ok(toHistoryBundle entries total))
        | None ->
            let entries, total = store.ListHistory filter
            Ok(toHistoryBundle entries total)

    let historyFromQuery
        (store: IResourceStore)
        (resourceType: string option)
        (id: string option)
        (query: Map<string, string list>)
        =
        let count, offset = parseCountOffset query

        history store {
            ResourceType = resourceType
            Id = id
            Since = parseSince query
            Count = count
            Offset = offset
        }

    let conditionalCreate
        (store: IResourceStore)
        (resourceType: string)
        (body: FhirResource)
        (ifNoneExist: string option)
        =
        match ifNoneExist with
        | None -> create store resourceType body
        | Some criteria ->
            let query = parseCriteriaQuery criteria |> Map.add "_count" [ "1" ]

            match store.Search(resourceType, query) with
            | existing :: _, _ -> Ok existing
            | [], _ -> create store resourceType body

    /// Conditional update: exactly one match → update; zero → create; multiple → conflict.
    let conditionalUpdate
        (store: IResourceStore)
        (resourceType: string)
        (criteria: string)
        (body: FhirResource)
        (ifMatch: string option)
        =
        let query = parseCriteriaQuery criteria |> Map.add "_count" [ "2" ]

        match store.Search(resourceType, query) with
        | [ existing ], 1 -> update store resourceType (FhirJson.id existing) body ifMatch
        | [], 0 -> create store resourceType body
        | _ -> Error(Conflict $"Conditional update matched multiple {resourceType} resources.")

    let observationLastN (store: IResourceStore) (query: Map<string, string list>) : FhirResult<FhirResource> =
        ensureKnownResourceType "Observation"
        |> Result.bind (fun () ->
            let maxN =
                QueryParams.first query "max"
                |> Option.bind (fun s ->
                    match Int32.TryParse s with
                    | true, n when n > 0 -> Some(min n 100)
                    | _ -> None)
                |> Option.defaultValue 1

            let rec chainSearch (rt: string, q: Map<string, string list>) =
                if not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let expanded =
                        q
                        |> Map.add "_count" [ "10000" ]
                        |> SearchEngine.expandChainedQuery chainSearch
                        |> SearchEngine.expandHasQuery chainSearch

                    store.Search(rt, expanded) |> fst

            let filterQuery =
                query
                |> Map.remove "max"
                |> Map.remove "_count"
                |> Map.remove "_offset"
                |> SearchEngine.expandChainedQuery chainSearch
                |> SearchEngine.expandHasQuery chainSearch

            let all =
                store.Search("Observation", filterQuery |> Map.add "_count" [ "10000" ])
                |> fst
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)

            let codeKey (resource: FhirResource) =
                match resource["code"] with
                | :? JsonObject as code ->
                    match code["coding"] with
                    | :? JsonArray as arr when arr.Count > 0 ->
                        match arr.[0] with
                        | :? JsonObject as c ->
                            let system = FhirJson.propString c "system" |> Option.defaultValue ""
                            let code = FhirJson.propString c "code" |> Option.defaultValue ""
                            $"{system}|{code}"
                        | _ -> FhirJson.id resource
                    | _ -> FhirJson.id resource
                | _ -> FhirJson.id resource

            let selected =
                all
                |> List.groupBy codeKey
                |> List.collect (fun (_, group) -> group |> List.truncate maxN)
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)

            Ok(toSearchBundle "Observation" selected [] selected.Length))

    let applyJsonPatch (store: IResourceStore) (resourceType: string) (id: string) (patchJson: string) =
        read store resourceType id
        |> Result.bind (fun current ->
            try
                let node = FhirJson.deepClone current
                let patchDoc = JsonSerializer.Deserialize<JsonArray>(patchJson)

                if isNull patchDoc then
                    Error(BadRequest "JSON Patch body must be an array.")
                else
                    for opNode in patchDoc do
                        let op = opNode["op"].GetValue<string>()
                        let path = opNode["path"].GetValue<string>()
                        let field = path.TrimStart('/')

                        match op with
                        | "replace"
                        | "add" -> node[field] <- opNode["value"].DeepClone()
                        | "remove" -> node.Remove(field) |> ignore
                        | other -> failwith $"Unsupported patch op '{other}'"

                    FhirJson.setId node id |> ignore
                    node["resourceType"] <- resourceType
                    let patched = store.Upsert node
                    SubscriptionNotifier.notifyChange store "patch" patched
                    Ok patched
            with ex ->
                Error(BadRequest $"JSON Patch failed: {ex.Message}"))

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

                let urnMap = assignBundleUrns entries

                if not urnMap.IsEmpty then
                    for entry in entries do
                        match entry["resource"] with
                        | :? JsonObject as resource -> rewriteUrnReferences resource urnMap
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
                                    store.Search(resourceType, parseCriteriaQuery criteria |> Map.add "_count" [ "1" ])
                                    |> fst
                                    |> List.tryHead
                                | None -> None

                            match conditionalCreate store resourceType resource ifNoneExist with
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
                                    conditionalUpdate store typeAndQuery.[0] typeAndQuery.[1] resource None
                                else
                                    let parts = typeAndQuery.[0].Split('/')

                                    if parts.Length < 2 then
                                        Error(BadRequest "PUT url must be Type/id or Type?search")
                                    else
                                        update store parts.[0] parts.[1] resource None

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

                            match delete store parts.[0] parts.[1] None with
                            | Ok deleted ->
                                outEntry["resource"] <- FhirJson.deepClone deleted
                                outResp["status"] <- "200"
                                outResp["etag"] <- FhirJson.versionId deleted
                            | Error err -> failwith (string err)
                        | "GET" ->
                            let parts = path.Split('?').[0].Split('/')

                            match parts with
                            | [| resourceType; id |] ->
                                match read store resourceType id with
                                | Ok resource ->
                                    outEntry["resource"] <- FhirJson.deepClone resource
                                    outResp["status"] <- "200"
                                | Error err -> failwith (string err)
                            | [| resourceType |] ->
                                match search store resourceType Map.empty with
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
