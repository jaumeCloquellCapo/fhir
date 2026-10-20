namespace FhirServer.Application.Smart

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.Configuration
open FhirServer.Domain

/// SMART App Launch MVP: discovery + client_credentials tokens (opaque).
module SmartAuth =

    type TokenRecord = {
        AccessToken: string
        ClientId: string
        Scope: string
        ExpiresAt: DateTimeOffset
    }

    let private tokens = ConcurrentDictionary<string, TokenRecord>()

    let private configString (config: IConfiguration) (key: string) (fallback: string) =
        match config.[key] with
        | null
        | "" -> fallback
        | value -> value

    let isEnabled (config: IConfiguration) =
        config.GetValue<bool>("Smart:Enabled", true)

    let clientId (config: IConfiguration) =
        configString config "Smart:ClientId" "fhir-client"

    let clientSecret (config: IConfiguration) =
        configString config "Smart:ClientSecret" "change-me"

    let defaultScope (config: IConfiguration) =
        configString config "Smart:Scopes" "system/*.read system/*.write openid fhirUser"

    let tokenLifetimeMinutes (config: IConfiguration) =
        config.GetValue<int>("Smart:TokenLifetimeMinutes", 60)

    let private stringArray (values: string seq) =
        let arr = JsonArray()

        for value in values do
            arr.Add(FhirJson.jStr value)

        arr

    let wellKnown (baseUrl: string) (config: IConfiguration) =
        let root = baseUrl.TrimEnd('/')
        let scopes = defaultScope config

        FhirJson.mkObject [
            "issuer", FhirJson.jStr root
            "authorization_endpoint", FhirJson.jStr $"{root}/auth/authorize"
            "token_endpoint", FhirJson.jStr $"{root}/auth/token"
            "capabilities",
            stringArray [
                "client-confidential-symmetric"
                "client-public"
                "sso-openid-connect"
                "permission-v2"
                "context-standalone-patient"
                "launch-standalone"
            ]
            "grant_types_supported", stringArray [ "client_credentials"; "authorization_code" ]
            "code_challenge_methods_supported", stringArray [ "S256" ]
            "scopes_supported", stringArray (scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            "response_types_supported", stringArray [ "code"; "token" ]
            "token_endpoint_auth_methods_supported", stringArray [ "client_secret_basic"; "client_secret_post" ]
        ]

    let private newToken () =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let issueToken (config: IConfiguration) (requestedScope: string option) =
        let scope =
            match requestedScope with
            | Some s when not (String.IsNullOrWhiteSpace s) -> s
            | _ -> defaultScope config

        let token = newToken ()
        let expires = DateTimeOffset.UtcNow.AddMinutes(float (tokenLifetimeMinutes config))

        let record = {
            AccessToken = token
            ClientId = clientId config
            Scope = scope
            ExpiresAt = expires
        }

        tokens[token] <- record

        FhirJson.mkObject [
            "access_token", FhirJson.jStr token
            "token_type", FhirJson.jStr "Bearer"
            "expires_in", FhirJson.jInt (tokenLifetimeMinutes config * 60)
            "scope", FhirJson.jStr scope
        ]

    let tryValidateBearer (accessToken: string) =
        match tokens.TryGetValue accessToken with
        | true, record when record.ExpiresAt > DateTimeOffset.UtcNow -> Some record
        | true, _ ->
            tokens.TryRemove(accessToken) |> ignore
            None
        | _ -> None

    let validateClient (config: IConfiguration) (id: string) (secret: string) =
        id = clientId config && secret = clientSecret config

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
