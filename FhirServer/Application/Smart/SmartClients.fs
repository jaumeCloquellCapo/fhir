namespace FhirServer.Application.Smart

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.Configuration
open FhirServer.Domain
open FhirServer.Domain.Services

/// Multi-client registry: config Clients[] (+ legacy keys) and in-memory dynamic registration.
module SmartClients =

    type ClientRecord = {
        ClientId: string
        ClientSecret: string
        AllowedScopes: string
        GrantTypes: Set<string>
        RedirectUris: Set<string>
        ClientName: string option
        /// When set, issued tokens are ORGBAC-scoped to this Organization id.
        OrganizationId: string option
        Dynamic: bool
    }

    let private dynamicClients = ConcurrentDictionary<string, ClientRecord>(StringComparer.Ordinal)

    /// Scopes DCR may grant — never system/* (open registration must not mint admin tokens).
    let private isAllowedDcrScopeToken (token: string) =
        let t = token.Trim()

        t.Equals("openid", StringComparison.Ordinal)
        || t.Equals("fhirUser", StringComparison.Ordinal)
        || t.Equals("launch/patient", StringComparison.Ordinal)
        || t.Equals("offline_access", StringComparison.Ordinal)
        || t.StartsWith("patient/", StringComparison.Ordinal)
        || t.StartsWith("user/", StringComparison.Ordinal)

    let private fixedTimeEquals (left: string) (right: string) =
        let a = SHA256.HashData(Encoding.UTF8.GetBytes(left))
        let b = SHA256.HashData(Encoding.UTF8.GetBytes(right))
        CryptographicOperations.FixedTimeEquals(a.AsSpan(), b.AsSpan())

    let private configString (section: IConfiguration) (key: string) (fallback: string) =
        match section.[key] with
        | null
        | "" -> fallback
        | value -> value

    let isDcrEnabled (config: IConfiguration) =
        config.GetValue<bool>("Smart:EnableDcr", false)

    let private splitSpace (value: string) =
        value.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)

    let private splitGrantTypes (value: string) =
        splitSpace value
        |> Array.map (fun s -> s.ToLowerInvariant())
        |> Set.ofArray

    let private splitRedirects (values: string seq) =
        values
        |> Seq.choose (fun s ->
            if String.IsNullOrWhiteSpace s then None else Some(s.Trim()))
        |> Set.ofSeq

    let private readRedirectUris (section: IConfigurationSection) =
        section.GetSection("RedirectUris").GetChildren()
        |> Seq.choose (fun c ->
            match c.Value with
            | null
            | "" -> None
            | v -> Some v)
        |> splitRedirects

    let private fromSection (section: IConfigurationSection) (dynamic: bool) =
        let id = configString section "ClientId" ""

        if String.IsNullOrWhiteSpace id then
            None
        else
            let grantsRaw = configString section "GrantTypes" "client_credentials authorization_code"
            let scopes = configString section "AllowedScopes" (configString section "Scopes" "system/*.cruds")

            let secret = configString section "ClientSecret" ""

            let organizationId =
                match configString section "OrganizationId" "" with
                | "" -> None
                | raw -> Some(TenantContext.normalizeOrgId raw)

            if String.IsNullOrWhiteSpace secret then
                None
            else
                Some {
                    ClientId = id
                    ClientSecret = secret
                    AllowedScopes = scopes
                    GrantTypes = splitGrantTypes grantsRaw
                    RedirectUris = readRedirectUris section
                    ClientName = None
                    OrganizationId = organizationId
                    Dynamic = dynamic
                }

    let private legacyClient (config: IConfiguration) =
        let id = config.["Smart:ClientId"]
        let secret = config.["Smart:ClientSecret"]
        let scopes = config.["Smart:Scopes"]

        if String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace secret then
            None
        else
            Some {
                ClientId = id
                ClientSecret = secret
                AllowedScopes =
                    if String.IsNullOrWhiteSpace scopes then
                        "system/*.read system/*.write openid fhirUser"
                    else
                        scopes
                GrantTypes = set [ "client_credentials"; "authorization_code" ]
                RedirectUris = Set.empty
                ClientName = Some "legacy"
                OrganizationId =
                    match config.["Smart:OrganizationId"] with
                    | null
                    | "" -> None
                    | raw -> Some(TenantContext.normalizeOrgId raw)
                Dynamic = false
            }

    let private configClients (config: IConfiguration) =
        let fromArray =
            config.GetSection("Smart:Clients").GetChildren()
            |> Seq.choose (fun s -> fromSection s false)
            |> Seq.toList

        match fromArray with
        | [] ->
            match legacyClient config with
            | Some c -> [ c ]
            | None -> []
        | xs -> xs

    let tryFind (config: IConfiguration) (clientId: string) =
        match dynamicClients.TryGetValue clientId with
        | true, c -> Some c
        | _ -> configClients config |> List.tryFind (fun c -> c.ClientId = clientId)

    let validate (config: IConfiguration) (clientId: string) (secret: string) =
        match tryFind config clientId with
        | Some c when fixedTimeEquals c.ClientSecret secret -> Some c
        | _ -> None

    let allowsGrant (client: ClientRecord) (grant: string) =
        client.GrantTypes.Contains(grant.ToLowerInvariant())

    let allowsRedirect (client: ClientRecord) (redirectUri: string) =
        client.RedirectUris.Contains redirectUri

    /// Intersect requested scopes with the client's allow-list (exact token match).
    let intersectScopes (client: ClientRecord) (requested: string option) =
        let allowed =
            splitSpace client.AllowedScopes
            |> Set.ofArray

        match requested with
        | None -> client.AllowedScopes.Trim()
        | Some s when String.IsNullOrWhiteSpace s -> client.AllowedScopes.Trim()
        | Some req ->
            splitSpace req
            |> Array.filter allowed.Contains
            |> String.concat " "

    let allSupportedScopes (config: IConfiguration) =
        let fromConfig =
            configClients config
            |> List.collect (fun c -> splitSpace c.AllowedScopes |> Array.toList)

        let fromDynamic =
            dynamicClients.Values
            |> Seq.collect (fun c -> splitSpace c.AllowedScopes)
            |> Seq.toList

        (fromConfig @ fromDynamic)
        |> List.distinct
        |> fun xs ->
            if xs.IsEmpty then
                [ "system/*.cruds"; "openid"; "fhirUser"; "launch/patient" ]
            else
                xs

    let private newId () =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private newSecret () =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private sanitizeDcrScope (requested: string) =
        splitSpace requested
        |> Array.filter isAllowedDcrScopeToken
        |> String.concat " "

    /// RFC 7591-style dynamic client registration (in-memory; lost on restart).
    /// Only patient/user/openid scopes — never system/*. Requires Smart:EnableDcr=true at the HTTP layer.
    let register (body: JsonObject) =
        let clientName = FhirJson.propString body "client_name"

        let redirectUris =
            match body["redirect_uris"] with
            | :? JsonArray as arr ->
                arr
                |> Seq.choose (fun n ->
                    match n with
                    | null -> None
                    | n when n.GetValueKind() = System.Text.Json.JsonValueKind.String -> Some(n.GetValue<string>())
                    | _ -> None)
                |> splitRedirects
            | _ -> Set.empty

        let grantTypes =
            match body["grant_types"] with
            | :? JsonArray as arr ->
                arr
                |> Seq.choose (fun n ->
                    match n with
                    | null -> None
                    | n when n.GetValueKind() = System.Text.Json.JsonValueKind.String ->
                        Some(n.GetValue<string>().ToLowerInvariant())
                    | _ -> None)
                |> Set.ofSeq
            | _ -> set [ "authorization_code" ]

        let grantTypes =
            if grantTypes.IsEmpty then
                set [ "authorization_code" ]
            else
                grantTypes

        // DCR must not issue client_credentials (system-style) clients.
        let grantTypes = grantTypes |> Set.remove "client_credentials"

        let scope =
            match FhirJson.propString body "scope" with
            | Some s when not (String.IsNullOrWhiteSpace s) -> sanitizeDcrScope s
            | _ -> "openid fhirUser launch/patient patient/*.rs"

        if grantTypes.IsEmpty then
            Error "grant_types must include authorization_code (client_credentials not allowed for DCR)"
        elif grantTypes.Contains "authorization_code" && redirectUris.IsEmpty then
            Error "redirect_uris required for authorization_code"
        elif String.IsNullOrWhiteSpace scope then
            Error "scope must include patient/, user/, openid, fhirUser, or launch/patient (system/* not allowed)"
        else
            let clientId = $"dcr-{newId ()}"
            let secret = newSecret ()

            let record = {
                ClientId = clientId
                ClientSecret = secret
                AllowedScopes = scope
                GrantTypes = grantTypes
                RedirectUris = redirectUris
                ClientName = clientName
                OrganizationId = None
                Dynamic = true
            }

            dynamicClients[clientId] <- record

            let redirects = JsonArray()

            for uri in redirectUris do
                redirects.Add(FhirJson.jStr uri)

            let grants = JsonArray()

            for g in grantTypes do
                grants.Add(FhirJson.jStr g)

            Ok(
                FhirJson.mkObject [
                    "client_id", FhirJson.jStr clientId
                    "client_secret", FhirJson.jStr secret
                    "client_id_issued_at", FhirJson.jInt (int (DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                    "client_name", FhirJson.jStr (clientName |> Option.defaultValue clientId)
                    "redirect_uris", redirects
                    "grant_types", grants
                    "token_endpoint_auth_method", FhirJson.jStr "client_secret_basic"
                    "scope", FhirJson.jStr scope
                ]
            )
    /// Test helper: clear dynamic clients between runs if needed.
    let clearDynamic () = dynamicClients.Clear()
