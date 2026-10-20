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

            ResourceValidator.ensureValid store (Some resourceType) body
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
        |> Result.bind (fun () -> ResourceValidator.ensureValid store (Some resourceType) body)
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

    let validateRaw
        (store: IResourceStore)
        (resourceType: string option)
        (body: string)
        (forcedProfiles: string list)
        : FhirResult<FhirResource> =
        let schemaAndType =
            match resourceType with
            | Some rt ->
                ResourceHelpers.ensureKnownResourceType rt
                |> Result.map (fun () ->
                    ResourceValidator.validateJsonWithProfiles (Some rt) body forcedProfiles)
            | None -> Ok(ResourceValidator.validateJsonWithProfiles None body forcedProfiles)

        schemaAndType
        |> Result.map (fun schemaIssues ->
            let refIssues = ReferenceValidation.collectIssuesFromJson store body

            let questionnaireIssues =
                try
                    match System.Text.Json.Nodes.JsonNode.Parse(body) with
                    | :? System.Text.Json.Nodes.JsonObject as obj ->
                        QuestionnaireValidation.collectIssues store obj
                    | _ -> []
                with _ ->
                    []

            ResourceValidator.toOperationOutcome (schemaIssues @ refIssues @ questionnaireIssues))

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
