namespace FhirServer.Domain.Services

open System
open System.Threading

/// ORGBAC resource ownership mode (envelope column + Aidbox-compatible meta extension).
[<RequireQualifiedAccess>]
type TenantMode =
    | Owned
    | Shared
    | SystemShared

module TenantMode =
    [<Literal>]
    let ExtensionUrl = "https://aidbox.app/tenant-resource-mode"

    [<Literal>]
    let OrganizationExtensionUrl = "https://aidbox.app/tenant-organization-id"

    let toDb =
        function
        | TenantMode.Owned -> "owned"
        | TenantMode.Shared -> "shared"
        | TenantMode.SystemShared -> "system-shared"

    let ofDb (value: string) =
        match value with
        | "shared" -> TenantMode.Shared
        | "system-shared" -> TenantMode.SystemShared
        | _ -> TenantMode.Owned

    let tryParse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "owned"
        | "" -> Some TenantMode.Owned
        | "shared" -> Some TenantMode.Shared
        | "system-shared" -> Some TenantMode.SystemShared
        | _ -> None

/// ORGBAC access: unrestricted admin, or scoped to one Organization (exact or hierarchical).
[<RequireQualifiedAccess>]
type TenantAccess =
    | Unrestricted
    /// hierarchical=true → OrgBAC parent/child + shared visibility (org-scoped API).
    | Organization of orgId: string * hierarchical: bool

/// Request-scoped tenant for envelope SQL filtering.
/// Prefers an HTTP-aware resolver (HttpContext.Items); falls back to AsyncLocal for non-HTTP paths.
module TenantContext =

    [<Literal>]
    let HttpItemKey = "FhirServer.TenantAccess"

    let private current = AsyncLocal<TenantAccess option>()

    let mutable private httpResolve: (unit -> TenantAccess option) = fun () -> None

    /// Called once from AppHost after DI is built (IHttpContextAccessor → Items).
    let configureHttpResolver (resolve: unit -> TenantAccess option) = httpResolve <- resolve

    let normalizeOrgId (value: string) =
        let v = value.Trim()

        if v.StartsWith("Organization/", StringComparison.OrdinalIgnoreCase) then
            v.Substring("Organization/".Length)
        else
            v

    let get () =
        match httpResolve () with
        | Some access -> access
        | None ->
            match current.Value with
            | Some access -> access
            | None -> TenantAccess.Unrestricted

    let set (access: TenantAccess) = current.Value <- Some access

    let clear () = current.Value <- None

    /// Organization id when scoped; None when unrestricted.
    let tryOrganizationId () =
        match get () with
        | TenantAccess.Unrestricted -> None
        | TenantAccess.Organization(orgId, _) -> Some orgId

    /// True when the request uses hierarchical OrgBAC visibility (org-scoped API).
    let isHierarchical () =
        match get () with
        | TenantAccess.Organization(_, hierarchical) -> hierarchical
        | TenantAccess.Unrestricted -> false

    /// Value stored on new envelopes (empty string when unrestricted or system-shared writer).
    let writeTenantId (mode: TenantMode) =
        match mode, get () with
        | TenantMode.SystemShared, _ -> ""
        | _, TenantAccess.Unrestricted -> ""
        | _, TenantAccess.Organization(orgId, _) -> orgId
