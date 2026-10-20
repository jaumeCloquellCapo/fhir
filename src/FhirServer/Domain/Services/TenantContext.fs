namespace FhirServer.Domain.Services

open System
open System.Threading

/// ORGBAC-style access: unrestricted (admin / no org on token) or scoped to one Organization id.
[<RequireQualifiedAccess>]
type TenantAccess =
    | Unrestricted
    | Organization of orgId: string

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
        | TenantAccess.Organization orgId -> Some orgId

    /// Value stored on new envelopes (empty string when unrestricted).
    let writeTenantId () =
        match get () with
        | TenantAccess.Unrestricted -> ""
        | TenantAccess.Organization orgId -> orgId
