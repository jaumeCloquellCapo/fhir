namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// FHIR CRUD: read/vread/create/update/delete/patch + conditional write.
module ResourceCrud =

    let read (store: IResourceStore) (resourceType: string) (id: string) : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            match store.Get(resourceType, id) with
            | Some resource -> Ok resource
            | None when store.WasDeleted(resourceType, id) -> Error(Gone(resourceType, id, None))
            | None -> Error(NotFound(resourceType, id)))

    let vread (store: IResourceStore) (resourceType: string) (id: string) (vid: string) : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType resourceType
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
        ResourceHelpers.ensureKnownResourceType resourceType
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

                    ResourceHelpers.tryCreate store body
                    |> Result.map (fun created ->
                        SubscriptionNotifier.notifyChange store "create" created
                        created)))
    let update
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        (body: FhirResource)
        (ifMatch: string option)
        : FhirResult<FhirResource> =
        let requestedMode = OrganizationHierarchy.tryResourceMode body
        OrganizationHierarchy.stripTenantExtensions body |> ignore

        ResourceHelpers.ensureKnownResourceType resourceType
        |> Result.bind (fun () -> ResourceValidator.ensureValid (Some resourceType) body)
        |> Result.bind (fun () ->
            let existing = store.Get(resourceType, id)

            ResourceHelpers.checkIfMatch existing ifMatch
            |> Result.bind (fun () ->
                match existing with
                | Some ex ->
                    match OrganizationHierarchy.ensureMutationAllowed ex with
                    | Error msg -> Error(Forbidden msg)
                    | Ok() -> Ok()
                | None -> Ok())
            |> Result.bind (fun () -> ResourceHelpers.prepareTenantMetaForWrite existing body requestedMode)
            |> Result.bind (fun () ->
                body["resourceType"] <- resourceType
                FhirJson.setId body id |> ignore

                ResourceHelpers.tryUpsert store body
                |> Result.map (fun updated ->
                    SubscriptionNotifier.notifyChange store "update" updated
                    updated)))

    let validateRaw (resourceType: string option) (body: string) : FhirResult<FhirResource> =
        match resourceType with
        | Some rt ->
            ResourceHelpers.ensureKnownResourceType rt
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
        ResourceHelpers.ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            let existing = store.Get(resourceType, id)

            ResourceHelpers.checkIfMatch existing ifMatch
            |> Result.bind (fun () ->
                match existing with
                | Some ex ->
                    match OrganizationHierarchy.ensureMutationAllowed ex with
                    | Error msg -> Error(Forbidden msg)
                    | Ok() -> Ok()
                | None -> Ok()
                |> Result.bind (fun () ->
                    match ResourceHelpers.tryDelete store resourceType id with
                    | Error err -> Error err
                    | Ok(Some deleted) ->
                        SubscriptionNotifier.notifyChange store "delete" deleted
                        Ok deleted
                    | Ok None when store.WasDeleted(resourceType, id) -> Error(Gone(resourceType, id, None))
                    | Ok None -> Error(NotFound(resourceType, id)))))
    let conditionalCreate
        (store: IResourceStore)
        (resourceType: string)
        (body: FhirResource)
        (ifNoneExist: string option)
        =
        match ifNoneExist with
        | None -> create store resourceType body
        | Some criteria ->
            let query = ResourceHelpers.parseCriteriaQuery criteria |> Map.add "_count" [ "1" ]

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
        let query = ResourceHelpers.parseCriteriaQuery criteria |> Map.add "_count" [ "2" ]

        match store.Search(resourceType, query) with
        | [ existing ], 1 -> update store resourceType (FhirJson.id existing) body ifMatch
        | [], 0 -> create store resourceType body
        | _ -> Error(Conflict $"Conditional update matched multiple {resourceType} resources.")

    let observationLastN (store: IResourceStore) (query: Map<string, string list>) : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType "Observation"
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
                        |> Map.add "_count" [ string ResourceHelpers.internalFetchCap ]
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
            let fetchCap = min ResourceHelpers.internalFetchCap (maxN * 50 |> max 50)

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

            Ok(ResourceHelpers.toSearchBundle "Observation" selected [] selected.Length))

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

                        ResourceHelpers.tryUpsert store node
                        |> Result.map (fun patched ->
                            SubscriptionNotifier.notifyChange store "patch" patched
                            patched)
                with
                | TenantMutationForbiddenException msg -> Error(Forbidden msg)
                | TenantStampException msg -> Error(BadRequest msg)
                | ex -> Error(BadRequest $"JSON Patch failed: {ex.Message}"))
