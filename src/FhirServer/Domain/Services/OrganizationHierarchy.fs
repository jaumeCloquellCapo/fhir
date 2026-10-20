namespace FhirServer.Domain.Services

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain

exception TenantStampException of message: string
exception TenantMutationForbiddenException of message: string

/// Parse Organization.partOf and Aidbox tenant meta extensions from FHIR JSON.
module OrganizationHierarchy =

    let tryPartOfOrgId (resource: JsonObject) : string option =
        match resource["partOf"] with
        | :? JsonObject as partOf ->
            match partOf["reference"] with
            | n when not (isNull n) && n.GetValueKind() = JsonValueKind.String ->
                let raw = n.GetValue<string>()

                if String.IsNullOrWhiteSpace raw then
                    None
                else
                    Some(TenantContext.normalizeOrgId raw)
            | _ -> None
        | _ -> None

    let private findExtension (resource: JsonObject) (url: string) : JsonObject option =
        match resource["meta"] with
        | :? JsonObject as meta ->
            match meta["extension"] with
            | :? JsonArray as arr ->
                arr
                |> Seq.tryPick (fun node ->
                    match node with
                    | :? JsonObject as ext ->
                        match ext["url"] with
                        | u when not (isNull u) && u.GetValueKind() = JsonValueKind.String
                                 && u.GetValue<string>() = url ->
                            Some ext
                        | _ -> None
                    | _ -> None)
            | _ -> None
        | _ -> None

    let tryResourceMode (resource: JsonObject) : TenantMode option =
        match findExtension resource TenantMode.ExtensionUrl with
        | Some ext ->
            match ext["valueString"] with
            | v when not (isNull v) && v.GetValueKind() = JsonValueKind.String ->
                TenantMode.tryParse (v.GetValue<string>())
            | _ -> None
        | None -> None

    /// Remove Aidbox tenant extensions so FHIR schema validation (ext-1) does not reject them.
    let stripTenantExtensions (resource: JsonObject) =
        match resource["meta"] with
        | :? JsonObject as meta ->
            match meta["extension"] with
            | :? JsonArray as arr ->
                let kept =
                    arr
                    |> Seq.filter (fun node ->
                        match node with
                        | :? JsonObject as ext ->
                            match ext["url"] with
                            | u when not (isNull u) && u.GetValueKind() = JsonValueKind.String ->
                                let url = u.GetValue<string>()

                                url <> TenantMode.ExtensionUrl
                                && url <> TenantMode.OrganizationExtensionUrl
                            | _ -> true
                        | _ -> true)
                    |> Seq.toArray

                arr.Clear()

                for node in kept do
                    arr.Add(node.DeepClone())

                if arr.Count = 0 then
                    meta.Remove("extension") |> ignore

                if meta.Count = 0 then
                    resource.Remove("meta") |> ignore
            | _ -> ()
        | _ -> ()

        resource

    let tryTenantOrganizationId (resource: JsonObject) : string option =
        match findExtension resource TenantMode.OrganizationExtensionUrl with
        | Some ext ->
            match ext["valueReference"] with
            | :? JsonObject as pref ->
                match pref["reference"] with
                | n when not (isNull n) && n.GetValueKind() = JsonValueKind.String ->
                    Some(TenantContext.normalizeOrgId (n.GetValue<string>()))
                | _ -> None
            | _ -> None
        | None -> None

    /// Forbid mutating shared (non-owner) or system-shared resources under an org context.
    let ensureMutationAllowed (resource: JsonObject) : Result<unit, string> =
        match TenantContext.tryOrganizationId () with
        | None -> Ok()
        | Some orgId ->
            let mode = tryResourceMode resource |> Option.defaultValue TenantMode.Owned

            match mode with
            | TenantMode.SystemShared ->
                Error "system-shared resources are read-only on organization-scoped APIs"
            | TenantMode.Shared ->
                match tryTenantOrganizationId resource with
                | Some owner when owner = orgId -> Ok()
                | Some _ ->
                    Error "shared resources can only be updated or deleted from their owning organization API"
                | None -> Ok()
            | TenantMode.Owned -> Ok()

    let ensureTenantModeExtension (resource: JsonObject) (mode: TenantMode) =
        match mode with
        | TenantMode.Owned -> resource
        | _ ->
            let meta =
                match resource["meta"] with
                | :? JsonObject as m -> m
                | _ ->
                    let m = JsonObject()
                    resource["meta"] <- m
                    m

            let extArr =
                match meta["extension"] with
                | :? JsonArray as a -> a
                | _ ->
                    let a = JsonArray()
                    meta["extension"] <- a
                    a

            let url = TenantMode.ExtensionUrl

            match findExtension resource url with
            | Some ext ->
                ext["valueString"] <- FhirJson.jStr (TenantMode.toDb mode)
            | None ->
                extArr.Add(
                    FhirJson.mkObject [
                        "url", FhirJson.jStr url
                        "valueString", FhirJson.jStr (TenantMode.toDb mode)
                    ]
                )

            resource

    let ensureTenantOrganizationExtension (resource: JsonObject) (orgId: string) =
        let meta =
            match resource["meta"] with
            | :? JsonObject as m -> m
            | _ ->
                let m = JsonObject()
                resource["meta"] <- m
                m

        let extArr =
            match meta["extension"] with
            | :? JsonArray as a -> a
            | _ ->
                let a = JsonArray()
                meta["extension"] <- a
                a

        let url = TenantMode.OrganizationExtensionUrl
        let existing =
            extArr
            |> Seq.tryFind (fun node ->
                match node with
                | :? JsonObject as ext ->
                    match ext["url"] with
                    | u when not (isNull u) && u.GetValueKind() = JsonValueKind.String ->
                        u.GetValue<string>() = url
                    | _ -> false
                | _ -> false)

        match existing with
        | Some(:? JsonObject as ext) ->
            ext["valueReference"] <-
                FhirJson.mkObject [ "reference", FhirJson.jStr $"Organization/{orgId}" ]
        | _ ->
            extArr.Add(
                FhirJson.mkObject [
                    "url", FhirJson.jStr url
                    "valueReference", FhirJson.mkObject [ "reference", FhirJson.jStr $"Organization/{orgId}" ]
                ]
            )

        resource

    /// Resolve create stamp: mode (from arg or meta) + validation against tenant context.
    /// Re-applies Aidbox tenant meta extensions after schema validation has run.
    let resolveCreateStamp (resource: JsonObject) (requestedMode: TenantMode option) : Result<TenantMode * string, string> =
        let mode =
            requestedMode
            |> Option.orElseWith (fun () -> tryResourceMode resource)
            |> Option.defaultValue TenantMode.Owned

        let orgId = TenantContext.tryOrganizationId ()

        match mode, orgId with
        | TenantMode.SystemShared, Some _ ->
            Error "system-shared resources must be created on the root FHIR API (no organization binding)."
        | TenantMode.SystemShared, None ->
            ensureTenantModeExtension resource TenantMode.SystemShared |> ignore
            Ok(TenantMode.SystemShared, "")
        | TenantMode.Shared, None ->
            Error "shared resources require an organization context (use /Organization/{id}/fhir/...)."
        | _, _ ->
            let tenantId = TenantContext.writeTenantId mode

            match mode, tenantId with
            | TenantMode.Shared, "" -> Error "shared resources require a non-empty organization id."
            | _ ->
                match orgId with
                | Some oid -> ensureTenantOrganizationExtension resource oid |> ignore
                | None -> ()

                ensureTenantModeExtension resource mode |> ignore
                Ok(mode, tenantId)
