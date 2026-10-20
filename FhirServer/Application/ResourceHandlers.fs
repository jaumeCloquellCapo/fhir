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

    let private tryUpsert (store: IResourceStore) (body: FhirResource) : FhirResult<FhirResource> =
        try
            Ok(store.Upsert body)
        with
        | TenantStampException msg -> Error(BadRequest msg)
        | TenantMutationForbiddenException msg -> Error(Forbidden msg)
        | DuplicateResourceException(rt, id) -> Error(Conflict $"Resource {rt}/{id} already exists.")

    let private tryCreate (store: IResourceStore) (body: FhirResource) : FhirResult<FhirResource> =
        try
            Ok(store.Create body)
        with
        | TenantStampException msg -> Error(BadRequest msg)
        | TenantMutationForbiddenException msg -> Error(Forbidden msg)
        | DuplicateResourceException(rt, id) -> Error(Conflict $"Resource {rt}/{id} already exists.")

    /// Cap for internal chain/_has/_filter expansion and compartment gathers (not client `_count`).
    let private internalFetchCap = 500

    let private tryDelete
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        : FhirResult<FhirResource option> =
        try
            Ok(store.Delete(resourceType, id))
        with TenantMutationForbiddenException msg ->
            Error(Forbidden msg)

    let create (store: IResourceStore) (resourceType: string) (body: FhirResource) : FhirResult<FhirResource> =
        ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            let requestedMode = OrganizationHierarchy.tryResourceMode body
            OrganizationHierarchy.stripTenantExtensions body |> ignore

            ResourceValidator.ensureValid (Some resourceType) body
            |> Result.bind (fun () ->
                match OrganizationHierarchy.resolveCreateStamp body requestedMode with
                | Error msg -> Error(BadRequest msg)
                | Ok _ ->
                    let id =
                        let current = FhirJson.id body

                        if String.IsNullOrWhiteSpace current then
                            Guid.NewGuid().ToString("N")
                        else
                            current

                    body["resourceType"] <- resourceType
                    FhirJson.setId body id |> ignore

                    tryCreate store body
                    |> Result.map (fun created ->
                        SubscriptionNotifier.notifyChange store "create" created
                        created)))

    let private prepareTenantMetaForWrite
        (existing: FhirResource option)
        (body: FhirResource)
        (requestedMode: TenantMode option)
        : FhirResult<unit> =
        match existing with
        | None ->
            match OrganizationHierarchy.resolveCreateStamp body requestedMode with
            | Error msg -> Error(BadRequest msg)
            | Ok _ -> Ok()
        | Some ex ->
            // Mode/tenant ownership are immutable on update — do not trust client meta.
            match OrganizationHierarchy.tryTenantOrganizationId ex with
            | Some oid -> OrganizationHierarchy.ensureTenantOrganizationExtension body oid |> ignore
            | None -> ()

            let mode =
                OrganizationHierarchy.tryResourceMode ex |> Option.defaultValue TenantMode.Owned

            OrganizationHierarchy.ensureTenantModeExtension body mode |> ignore
            Ok()

    let update
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        (body: FhirResource)
        (ifMatch: string option)
        : FhirResult<FhirResource> =
        let requestedMode = OrganizationHierarchy.tryResourceMode body
        OrganizationHierarchy.stripTenantExtensions body |> ignore

        ensureKnownResourceType resourceType
        |> Result.bind (fun () -> ResourceValidator.ensureValid (Some resourceType) body)
        |> Result.bind (fun () ->
            let existing = store.Get(resourceType, id)

            checkIfMatch existing ifMatch
            |> Result.bind (fun () ->
                match existing with
                | Some ex ->
                    match OrganizationHierarchy.ensureMutationAllowed ex with
                    | Error msg -> Error(Forbidden msg)
                    | Ok() -> Ok()
                | None -> Ok())
            |> Result.bind (fun () -> prepareTenantMetaForWrite existing body requestedMode)
            |> Result.bind (fun () ->
                body["resourceType"] <- resourceType
                FhirJson.setId body id |> ignore

                tryUpsert store body
                |> Result.map (fun updated ->
                    SubscriptionNotifier.notifyChange store "update" updated
                    updated)))

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
                match existing with
                | Some ex ->
                    match OrganizationHierarchy.ensureMutationAllowed ex with
                    | Error msg -> Error(Forbidden msg)
                    | Ok() -> Ok()
                | None -> Ok()
                |> Result.bind (fun () ->
                    match tryDelete store resourceType id with
                    | Error err -> Error err
                    | Ok(Some deleted) ->
                        SubscriptionNotifier.notifyChange store "delete" deleted
                        Ok deleted
                    | Ok None when store.WasDeleted(resourceType, id) -> Error(Gone(resourceType, id, None))
                    | Ok None -> Error(NotFound(resourceType, id)))))

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

            let rec searchList (depth: int) (rt: string, q: Map<string, string list>) =
                if depth > 5 || not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let nested = searchList (depth + 1)

                    let expanded =
                        q
                        |> Map.add "_count" [ string internalFetchCap ]
                        |> SearchEngine.expandChainedQuery nested
                        |> SearchEngine.expandHasQuery nested

                    store.Search(rt, expanded) |> fst

            let expandSearch = searchList 0

            let baseQuery =
                query
                |> Map.remove "_include"
                |> Map.remove "_revinclude"

            match SearchFilter.expand baseQuery with
            | SearchFilter.Expansion.Invalid reason -> Error(BadRequest reason)
            | expansion ->
                let runSearch (q: Map<string, string list>) =
                    let filterQuery =
                        q
                        |> SearchEngine.expandChainedQuery expandSearch
                        |> SearchEngine.expandHasQuery expandSearch

                    store.Search(resourceType, filterQuery)

                let page, total =
                    match expansion with
                    | SearchFilter.Expansion.Unchanged q
                    | SearchFilter.Expansion.Single q -> runSearch q
                    | SearchFilter.Expansion.Alternatives alts ->
                        let ids =
                            alts
                            |> List.collect (fun alt ->
                                let q = alt |> Map.add "_count" [ string internalFetchCap ]
                                runSearch q |> fst |> List.map FhirJson.id)
                            |> List.distinct

                        let idQuery =
                            if List.isEmpty ids then
                                baseQuery
                                |> Map.remove "_filter"
                                |> Map.add "_id" [ "__filter_nomatch__" ]
                            else
                                baseQuery
                                |> Map.remove "_filter"
                                |> Map.add "_id" ids

                        runSearch idQuery
                    | SearchFilter.Expansion.Invalid _ -> [], 0

                let getMany rt ids = store.GetMany(rt, ids)

                let includes =
                    SearchEngine.resolveIncludes getMany resourceType includeTokens page

                let revincludes =
                    SearchEngine.resolveRevincludes expandSearch resourceType revIncludeTokens page

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
                            store.Search(rt, query |> Map.add "_count" [ string internalFetchCap ]) |> fst
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
        ensureKnownResourceType resourceType
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

            let rec chainSearch (depth: int) (rt: string, q: Map<string, string list>) =
                if depth > 5 || not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let nested = chainSearch (depth + 1)

                    let expanded =
                        q
                        |> Map.add "_count" [ string internalFetchCap ]
                        |> SearchEngine.expandChainedQuery nested
                        |> SearchEngine.expandHasQuery nested

                    store.Search(rt, expanded) |> fst

            let expandSearch = chainSearch 0

            let filterQuery =
                query
                |> Map.remove "max"
                |> Map.remove "_count"
                |> Map.remove "_offset"
                |> SearchEngine.expandChainedQuery expandSearch
                |> SearchEngine.expandHasQuery expandSearch

            // Fetch a bounded window then group in memory (MVP $lastn).
            let fetchCap = min internalFetchCap (maxN * 50 |> max 50)

            let all =
                store.Search("Observation", filterQuery |> Map.add "_count" [ string fetchCap ])
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
            match OrganizationHierarchy.ensureMutationAllowed current with
            | Error msg -> Error(Forbidden msg)
            | Ok() ->
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

                        tryUpsert store node
                        |> Result.map (fun patched ->
                            SubscriptionNotifier.notifyChange store "patch" patched
                            patched)
                with
                | TenantMutationForbiddenException msg -> Error(Forbidden msg)
                | TenantStampException msg -> Error(BadRequest msg)
                | ex -> Error(BadRequest $"JSON Patch failed: {ex.Message}"))

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
