namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// Shared helpers for resource use-case modules.
module ResourceHelpers =

    let ensureKnownResourceType (resourceType: string) : FhirResult<unit> =
        if ResourceCatalog.isSupported resourceType then
            Ok()
        else
            Error(UnsupportedType resourceType)

    let firstQuery (query: Map<string, string list>) key =
        query |> Map.tryFind key |> Option.bind List.tryHead

    let parseCountOffset (query: Map<string, string list>) =
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

    let parseSince (query: Map<string, string list>) =
        firstQuery query "_since"
        |> Option.bind (fun s ->
            match DateTimeOffset.TryParse s with
            | true, dto -> Some dto
            | _ -> None)

    let normalizeVersionToken (token: string) =
        let t = token.Trim()

        if t.StartsWith("W/", StringComparison.OrdinalIgnoreCase) then
            t.Substring(2).Trim().Trim('"')
        else
            t.Trim('"')

    let checkIfMatch (existing: FhirResource option) (ifMatch: string option) =
        match ifMatch with
        | None -> Ok()
        | Some expected ->
            let normalized = normalizeVersionToken expected

            match existing with
            | None -> Error(PreconditionFailed "If-Match provided but resource does not exist.")
            | Some resource when FhirJson.versionId resource <> normalized ->
                Error(PreconditionFailed $"Version mismatch. Current version is '{FhirJson.versionId resource}'.")
            | Some _ -> Ok()

    let toSearchBundle
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

    let rewriteUrnReferences (node: JsonNode) (urnMap: Map<string, string>) =
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

    let assignBundleUrns (entries: JsonObject list) =
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

    let toHistoryBundle (entries: HistoryEntry list) (total: int) =
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
    let tryUpsert (store: IResourceStore) (body: FhirResource) : FhirResult<FhirResource> =
        try
            Ok(store.Upsert body)
        with
        | TenantStampException msg -> Error(BadRequest msg)
        | TenantMutationForbiddenException msg -> Error(Forbidden msg)
        | DuplicateResourceException(rt, id) -> Error(Conflict $"Resource {rt}/{id} already exists.")

    let tryCreate (store: IResourceStore) (body: FhirResource) : FhirResult<FhirResource> =
        try
            Ok(store.Create body)
        with
        | TenantStampException msg -> Error(BadRequest msg)
        | TenantMutationForbiddenException msg -> Error(Forbidden msg)
        | DuplicateResourceException(rt, id) -> Error(Conflict $"Resource {rt}/{id} already exists.")

    /// Cap for internal chain/_has/_filter expansion and compartment gathers (not client `_count`).
    let internalFetchCap = 500

    let tryDelete
        (store: IResourceStore)
        (resourceType: string)
        (id: string)
        : FhirResult<FhirResource option> =
        try
            Ok(store.Delete(resourceType, id))
        with TenantMutationForbiddenException msg ->
            Error(Forbidden msg)
    let prepareTenantMetaForWrite
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
    let parseCriteriaQuery (criteria: string) =
        criteria.Split('&')
        |> Array.choose (fun part ->
            match part.Split('=', 2) with
            | [| k; v |] -> Some(Uri.UnescapeDataString k, [ Uri.UnescapeDataString v ])
            | _ -> None)
        |> Map.ofArray
