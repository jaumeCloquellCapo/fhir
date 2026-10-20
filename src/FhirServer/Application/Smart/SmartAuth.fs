namespace FhirServer.Application.Smart

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.Configuration
open FhirServer.Domain
open FhirServer.Domain.Services

/// SMART App Launch: discovery, auth codes + PKCE, opaque access tokens.
module SmartAuth =

    type TokenRecord = {
        AccessToken: string
        ClientId: string
        Scope: string
        ExpiresAt: DateTimeOffset
        PatientId: string option
        FhirUser: string option
        OrganizationId: string option
    }

    type AuthCodeRecord = {
        Code: string
        ClientId: string
        RedirectUri: string
        Scope: string
        CodeChallenge: string
        ExpiresAt: DateTimeOffset
        PatientId: string option
        FhirUser: string option
    }

    let private tokens = ConcurrentDictionary<string, TokenRecord>()
    let private authCodes = ConcurrentDictionary<string, AuthCodeRecord>()

    let isEnabled (config: IConfiguration) =
        config.GetValue<bool>("Smart:Enabled", true)

    let tokenLifetimeMinutes (config: IConfiguration) =
        config.GetValue<int>("Smart:TokenLifetimeMinutes", 60)

    let authCodeLifetimeMinutes (config: IConfiguration) =
        config.GetValue<int>("Smart:AuthCodeLifetimeMinutes", 5)

    let private stringArray (values: string seq) =
        let arr = JsonArray()

        for value in values do
            arr.Add(FhirJson.jStr value)

        arr

    let private newOpaque () =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let s256Challenge (verifier: string) =
        let hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
        Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let wellKnown (baseUrl: string) (config: IConfiguration) =
        let root = baseUrl.TrimEnd('/')
        let scopes = SmartClients.allSupportedScopes config

        let props = [
            "issuer", FhirJson.jStr root
            "authorization_endpoint", FhirJson.jStr $"{root}/auth/authorize"
            "token_endpoint", FhirJson.jStr $"{root}/auth/token"
            "capabilities",
            stringArray [
                "client-confidential-symmetric"
                "permission-v2"
                "context-standalone-patient"
                "launch-standalone"
                "authorize-post"
            ]
            "grant_types_supported", stringArray [ "client_credentials"; "authorization_code" ]
            "code_challenge_methods_supported", stringArray [ "S256" ]
            "scopes_supported", stringArray scopes
            "response_types_supported", stringArray [ "code" ]
            "token_endpoint_auth_methods_supported", stringArray [ "client_secret_basic"; "client_secret_post" ]
        ]

        let props =
            if SmartClients.isDcrEnabled config then
                props @ [ "registration_endpoint", FhirJson.jStr $"{root}/auth/register" ]
            else
                props

        FhirJson.mkObject props

    let private tokenResponse (record: TokenRecord) (expiresInSeconds: int) =
        let props = [
            "access_token", FhirJson.jStr record.AccessToken
            "token_type", FhirJson.jStr "Bearer"
            "expires_in", FhirJson.jInt expiresInSeconds
            "scope", FhirJson.jStr record.Scope
        ]

        let props =
            match record.PatientId with
            | Some pid -> props @ [ "patient", FhirJson.jStr pid ]
            | None -> props

        let props =
            match record.FhirUser with
            | Some fu -> props @ [ "fhirUser", FhirJson.jStr fu ]
            | None -> props

        let props =
            match record.OrganizationId with
            | Some orgId -> props @ [ "organization", FhirJson.jStr orgId ]
            | None -> props

        FhirJson.mkObject props

    let issueAccessToken
        (config: IConfiguration)
        (clientId: string)
        (scope: string)
        (patientId: string option)
        (fhirUser: string option)
        (organizationId: string option)
        =
        let token = newOpaque ()
        let minutes = tokenLifetimeMinutes config
        let expires = DateTimeOffset.UtcNow.AddMinutes(float minutes)

        let record = {
            AccessToken = token
            ClientId = clientId
            Scope = scope
            ExpiresAt = expires
            PatientId = patientId |> Option.map PatientCompartment.normalizePatientId
            FhirUser = fhirUser
            OrganizationId = organizationId |> Option.map TenantContext.normalizeOrgId
        }

        tokens[token] <- record
        tokenResponse record (minutes * 60)

    let tryValidateBearer (accessToken: string) =
        match tokens.TryGetValue accessToken with
        | true, record when record.ExpiresAt > DateTimeOffset.UtcNow -> Some record
        | true, _ ->
            tokens.TryRemove(accessToken) |> ignore
            None
        | _ -> None

    let createAuthCode
        (config: IConfiguration)
        (clientId: string)
        (redirectUri: string)
        (scope: string)
        (codeChallenge: string)
        (patientId: string option)
        (fhirUser: string option)
        =
        let code = newOpaque ()
        let minutes = authCodeLifetimeMinutes config

        let record = {
            Code = code
            ClientId = clientId
            RedirectUri = redirectUri
            Scope = scope
            CodeChallenge = codeChallenge
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(float minutes)
            PatientId = patientId |> Option.map PatientCompartment.normalizePatientId
            FhirUser = fhirUser
        }

        authCodes[code] <- record
        code

    let private takeAuthCode (code: string) =
        match authCodes.TryRemove code with
        | true, record when record.ExpiresAt > DateTimeOffset.UtcNow -> Ok record
        | true, _ -> Error "invalid_grant"
        | _ -> Error "invalid_grant"

    let exchangeAuthorizationCode
        (config: IConfiguration)
        (client: SmartClients.ClientRecord)
        (code: string)
        (redirectUri: string)
        (codeVerifier: string)
        =
        match takeAuthCode code with
        | Error e -> Error e
        | Ok record ->
            if record.ClientId <> client.ClientId then
                Error "invalid_grant"
            elif record.RedirectUri <> redirectUri then
                Error "invalid_grant"
            elif s256Challenge codeVerifier <> record.CodeChallenge then
                Error "invalid_grant"
            else
                Ok(
                    issueAccessToken
                        config
                        client.ClientId
                        record.Scope
                        record.PatientId
                        record.FhirUser
                        client.OrganizationId
                )

    let issueClientCredentials
        (config: IConfiguration)
        (client: SmartClients.ClientRecord)
        (requestedScope: string option)
        =
        let scope = SmartClients.intersectScopes client requestedScope

        if String.IsNullOrWhiteSpace scope then
            Error "invalid_scope"
        elif SmartScopes.hasPatientOrUserClinicalScopes scope then
            Error "invalid_scope"
        elif not (SmartClients.allowsGrant client "client_credentials") then
            Error "unauthorized_client"
        else
            Ok(issueAccessToken config client.ClientId scope None None client.OrganizationId)

    let parseClientCredentials (authorizationHeader: string option) (form: Map<string, string>) =
        let fromBasic =
            match authorizationHeader with
            | Some header when header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ->
                try
                    let encoded = header.Substring(6).Trim()
                    let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                    let parts = decoded.Split(':', 2)

                    if parts.Length = 2 then
                        Some(parts.[0], parts.[1])
                    else
                        None
                with _ ->
                    None
            | _ -> None

        match fromBasic with
        | Some creds -> Some creds
        | None ->
            match Map.tryFind "client_id" form, Map.tryFind "client_secret" form with
            | Some id, Some secret -> Some(id, secret)
            | _ -> None

    /// Auth context attached to HttpContext.Items for downstream compartment / ORGBAC checks.
    type AuthContext = {
        ClientId: string
        Scope: string
        PatientId: string option
        FhirUser: string option
        CompartmentPatientId: string option
        OrganizationId: string option
    }

    [<Literal>]
    let AuthContextItemKey = "FhirServer.SmartAuthContext"

    let toAuthContext (record: TokenRecord) : AuthContext =
        let compartment =
            PatientCompartment.tryCompartmentPatientId record.Scope record.PatientId record.FhirUser

        {
            ClientId = record.ClientId
            Scope = record.Scope
            PatientId = record.PatientId
            FhirUser = record.FhirUser
            CompartmentPatientId = compartment
            OrganizationId = record.OrganizationId
        }

    let tenantAccessOf (auth: AuthContext) =
        match auth.OrganizationId with
        | Some orgId -> TenantAccess.Organization orgId
        | None -> TenantAccess.Unrestricted

    /// Bind ORGBAC tenant for this request (AsyncLocal + HttpContext.Items).
    let applyTenantContext (ctx: Microsoft.AspNetCore.Http.HttpContext) (auth: AuthContext) =
        let access = tenantAccessOf auth
        TenantContext.set access
        ctx.Items[TenantContext.HttpItemKey] <- access
