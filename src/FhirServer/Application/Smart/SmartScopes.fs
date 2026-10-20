namespace FhirServer.Application.Smart

open System
open FhirServer.Domain

/// SMART v2 scope parsing and HTTP → interaction mapping.
module SmartScopes =

    [<RequireQualifiedAccess>]
    type ScopeLevel =
        | System
        | User
        | Patient

    [<RequireQualifiedAccess>]
    type Interaction =
        | Create
        | Read
        | Update
        | Delete
        | Search

    [<Flags>]
    type Perms =
        | None = 0
        | Create = 1
        | Read = 2
        | Update = 4
        | Delete = 8
        | Search = 16

    type ResourceScope = {
        Level: ScopeLevel
        Resource: string
        Perms: Perms
    }

    let private allPerms =
        Perms.Create ||| Perms.Read ||| Perms.Update ||| Perms.Delete ||| Perms.Search

    let parsePermPart (permPart: string) : Perms =
        match permPart.ToLowerInvariant() with
        | ""
        | "*"
        | "cruds" -> allPerms
        | "read" -> Perms.Read ||| Perms.Search
        | "write"
        | "w" -> Perms.Create ||| Perms.Update ||| Perms.Delete
        | other ->
            let mutable p = Perms.None

            for ch in other do
                match Char.ToLowerInvariant ch with
                | 'c' -> p <- p ||| Perms.Create
                | 'r' -> p <- p ||| Perms.Read
                | 'u' -> p <- p ||| Perms.Update
                | 'd' -> p <- p ||| Perms.Delete
                | 's' -> p <- p ||| Perms.Search
                | _ -> ()

            p

    let private parseLevelPrefix (raw: string) =
        if raw.StartsWith("system/", StringComparison.Ordinal) then
            Some(ScopeLevel.System, raw.Substring("system/".Length))
        elif raw.StartsWith("user/", StringComparison.Ordinal) then
            Some(ScopeLevel.User, raw.Substring("user/".Length))
        elif raw.StartsWith("patient/", StringComparison.Ordinal) then
            Some(ScopeLevel.Patient, raw.Substring("patient/".Length))
        else
            None

    /// Parse clinical SMART scopes (`patient/*.rs`, `user/Observation.c`, `system/*.cruds`, …).
    let parseResourceScopes (scope: string) : ResourceScope list =
        scope.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun raw ->
            let s = raw.Trim()

            match parseLevelPrefix s with
            | None -> None
            | Some(level, rest) ->
                let resourcePart, permPart =
                    match rest.LastIndexOf('.') with
                    | -1 -> rest, ""
                    | i -> rest.Substring(0, i), rest.Substring(i + 1)

                let perms = parsePermPart permPart

                if perms = Perms.None && resourcePart <> "*" then
                    None
                elif String.IsNullOrWhiteSpace resourcePart then
                    None
                else
                    let perms' =
                        if perms = Perms.None && resourcePart = "*" then
                            allPerms
                        else
                            perms

                    Some {
                        Level = level
                        Resource = resourcePart
                        Perms = perms'
                    })
        |> Array.toList

    let interactionPerm (interaction: Interaction) =
        match interaction with
        | Interaction.Create -> Perms.Create
        | Interaction.Read -> Perms.Read
        | Interaction.Update -> Perms.Update
        | Interaction.Delete -> Perms.Delete
        | Interaction.Search -> Perms.Search

    let private matchesResource (resourcePart: string) (resourceType: string option) =
        resourcePart = "*"
        || (match resourceType with
            | Some rt -> resourcePart.Equals(rt, StringComparison.OrdinalIgnoreCase)
            | None -> resourcePart = "*")

    let allows
        (scope: string)
        (resourceType: string option)
        (interaction: Interaction)
        : bool =
        let needed = interactionPerm interaction
        let scopes = parseResourceScopes scope

        scopes
        |> List.exists (fun s ->
            matchesResource s.Resource resourceType
            && (s.Perms &&& needed) = needed)

    /// True when any patient/ or user/ clinical scope is present.
    let hasPatientOrUserClinicalScopes (scope: string) =
        parseResourceScopes scope
        |> List.exists (fun s ->
            match s.Level with
            | ScopeLevel.Patient
            | ScopeLevel.User -> true
            | ScopeLevel.System -> false)

    let hasPatientClinicalScopes (scope: string) =
        parseResourceScopes scope
        |> List.exists (fun s -> s.Level = ScopeLevel.Patient)

    let hasUserClinicalScopes (scope: string) =
        parseResourceScopes scope
        |> List.exists (fun s -> s.Level = ScopeLevel.User)

    let hasOnlySystemClinicalScopes (scope: string) =
        let clinical = parseResourceScopes scope

        clinical <> []
        && clinical |> List.forall (fun s -> s.Level = ScopeLevel.System)

    let needsPatientLaunch (scope: string) =
        hasPatientClinicalScopes scope
        || scope.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
           |> Array.exists (fun s ->
               s.Equals("launch/patient", StringComparison.OrdinalIgnoreCase)
               || s.Equals("launch", StringComparison.OrdinalIgnoreCase))

    let needsFhirUser (scope: string) = hasUserClinicalScopes scope

    /// Strip `/Organization/{id}/fhir` prefix so scope checks see the inner FHIR path.
    let private fhirPathParts (path: string) =
        let parts =
            path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

        if
            parts.Length >= 3
            && parts.[0].Equals("Organization", StringComparison.OrdinalIgnoreCase)
            && parts.[2].Equals("fhir", StringComparison.OrdinalIgnoreCase)
        then
            parts.[3..]
        else
            parts

    /// Map HTTP method + path segments to a FHIR interaction for scope checks.
    let resolveInteraction (method: string) (path: string) : Interaction * string option =
        let parts = fhirPathParts path

        let resourceType =
            match parts with
            | [||] -> None
            | parts when parts.[0].StartsWith("$", StringComparison.Ordinal) -> None
            | parts when parts.[0].Equals("metadata", StringComparison.OrdinalIgnoreCase) -> None
            | parts when ResourceCatalog.isSupported parts.[0] -> Some parts.[0]
            | _ -> None

        let m = method.ToUpperInvariant()

        let interaction =
            match m, parts with
            | "DELETE", _ -> Interaction.Delete
            | "PUT", _
            | "PATCH", _ -> Interaction.Update
            | "POST", [| rt; "_search" |] when ResourceCatalog.isSupported rt -> Interaction.Search
            | "POST", [| rt |] when ResourceCatalog.isSupported rt -> Interaction.Create
            | "POST", [| rt; id; op |] when
                ResourceCatalog.isSupported rt
                && not (id.StartsWith("$", StringComparison.Ordinal))
                && op.StartsWith("$", StringComparison.Ordinal)
                ->
                Interaction.Read
            | "POST", _ ->
                // Bundle, $ops, import, etc.
                Interaction.Update
            | "GET", [| rt |] when ResourceCatalog.isSupported rt -> Interaction.Search
            | "GET", [| rt; "_history" |] when ResourceCatalog.isSupported rt -> Interaction.Read
            | "GET", [| rt; id |] when ResourceCatalog.isSupported rt && not (id.StartsWith("$", StringComparison.Ordinal)) ->
                Interaction.Read
            | "GET", [| rt; id; "_history" |]
            | "GET", [| rt; id; "_history"; _ |] when ResourceCatalog.isSupported rt -> Interaction.Read
            | "GET", [| rt; op |] when ResourceCatalog.isSupported rt && op.StartsWith("$", StringComparison.Ordinal) ->
                if op.Equals("$dump", StringComparison.OrdinalIgnoreCase) then
                    Interaction.Search
                else
                    Interaction.Read
            | "GET", [| rt; id; op |] when
                ResourceCatalog.isSupported rt
                && op.StartsWith("$", StringComparison.Ordinal)
                ->
                Interaction.Read
            | "GET", _ -> Interaction.Read
            | _ -> Interaction.Read

        interaction, resourceType

    let describeNeeded (resourceType: string option) (interaction: Interaction) =
        let target = resourceType |> Option.defaultValue "*"
        let perm =
            match interaction with
            | Interaction.Create -> "c"
            | Interaction.Read -> "r"
            | Interaction.Update -> "u"
            | Interaction.Delete -> "d"
            | Interaction.Search -> "s"

        $"{target}.{perm}"
