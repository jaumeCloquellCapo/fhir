module FhirServer.E2E.Endpoints.SmartAuthFlowTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type SmartFlowFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)

        builder
            .UseSetting("Auth:Username", "fhir")
            .UseSetting("Auth:Password", "secret")
            .UseSetting("Smart:EnableDcr", "true")
            .UseSetting("Smart:RequireAuthorizeLogin", "true")
            .UseSetting("Smart:Clients:0:ClientId", "fhir-client")
            .UseSetting("Smart:Clients:0:ClientSecret", "smart-secret")
            .UseSetting(
                "Smart:Clients:0:AllowedScopes",
                "system/*.read system/*.cruds system/Patient.r openid fhirUser"
            )
            .UseSetting("Smart:Clients:0:GrantTypes", "client_credentials authorization_code")
            .UseSetting("Smart:Clients:0:RedirectUris:0", "http://localhost/callback")
            .UseSetting("Smart:Clients:1:ClientId", "patient-app")
            .UseSetting("Smart:Clients:1:ClientSecret", "smart-secret")
            .UseSetting(
                "Smart:Clients:1:AllowedScopes",
                "openid fhirUser launch/patient patient/*.rs patient/*.read"
            )
            .UseSetting("Smart:Clients:1:GrantTypes", "authorization_code")
            .UseSetting("Smart:Clients:1:RedirectUris:0", "http://localhost/callback")
        |> ignore

type SmartAuthFlowTests() =
    let factory = new SmartFlowFactory()

    let client =
        factory.CreateClient(WebApplicationFactoryClientOptions(AllowAutoRedirect = false))

    let basicAuth user pass =
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"))

    let s256 (verifier: string) =
        let hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
        Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let issueClientCredentials (clientId: string) (secret: string) (scope: string) =
        task {
            let basic = basicAuth clientId secret
            use request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}") |> ignore

            request.Content <-
                new StringContent(
                    $"grant_type=client_credentials&scope={Uri.EscapeDataString scope}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )

            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            return response.StatusCode, body
        }

    let registerClient (redirect: string) (scope: string) =
        task {
            let json =
                $$"""{"client_name":"e2e-app","redirect_uris":["{{redirect}}"],"grant_types":["authorization_code"],"scope":"{{scope}}","token_endpoint_auth_method":"client_secret_basic"}"""

            let! status, body, _ =
                send client HttpMethod.Post "/auth/register" (Some json) []

            assertStatus HttpStatusCode.Created status
            use doc = JsonDocument.Parse(body)
            let id = doc.RootElement.GetProperty("client_id").GetString()
            let secret = doc.RootElement.GetProperty("client_secret").GetString()
            return id, secret
        }

    let authorizeCode
        (clientId: string)
        (redirect: string)
        (scope: string)
        (challenge: string)
        (patientId: string option)
        =
        task {
            let qs =
                [
                    "response_type=code"
                    $"client_id={Uri.EscapeDataString clientId}"
                    $"redirect_uri={Uri.EscapeDataString redirect}"
                    $"scope={Uri.EscapeDataString scope}"
                    $"code_challenge={Uri.EscapeDataString challenge}"
                    "code_challenge_method=S256"
                    "state=xyz"
                ]
                @ (match patientId with
                   | Some p -> [ $"patient={Uri.EscapeDataString p}" ]
                   | None -> [])
                |> String.concat "&"

            use request = new HttpRequestMessage(HttpMethod.Get, $"/auth/authorize?{qs}")
            let basic = basicAuth "fhir" "secret"
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}") |> ignore
            use! response = client.SendAsync(request)
            Assert.True(
                response.StatusCode = HttpStatusCode.Redirect
                || response.StatusCode = HttpStatusCode.Found
                || response.StatusCode = HttpStatusCode.RedirectMethod,
                $"Expected redirect, got {response.StatusCode}"
            )

            let location = response.Headers.Location.ToString()

            let code =
                Uri(location).Query.TrimStart('?').Split('&')
                |> Array.choose (fun part ->
                    match part.Split('=', 2) with
                    | [| "code"; v |] -> Some(Uri.UnescapeDataString v)
                    | _ -> None)
                |> Array.tryHead
                |> Option.defaultValue ""

            Assert.False(String.IsNullOrWhiteSpace code)
            return code
        }

    let exchangeCode (clientId: string) (secret: string) (redirect: string) (code: string) (verifier: string) =
        task {
            let basic = basicAuth clientId secret
            use request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}") |> ignore

            let form =
                [
                    "grant_type=authorization_code"
                    $"code={Uri.EscapeDataString code}"
                    $"redirect_uri={Uri.EscapeDataString redirect}"
                    $"code_verifier={Uri.EscapeDataString verifier}"
                ]
                |> String.concat "&"

            request.Content <- new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")
            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK response.StatusCode
            use doc = JsonDocument.Parse(body)
            return doc.RootElement.GetProperty("access_token").GetString(), body
        }

    let createPatient () =
        task {
            let basic = basicAuth "fhir" "secret"
            let json = """{"resourceType":"Patient","name":[{"family":"AuthFlow"}]}"""

            let! status, body, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some json)
                    [ ("Authorization", $"Basic {basic}") ]

            assertStatus HttpStatusCode.Created status
            return propString body "id" |> Option.defaultValue ""
        }

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``client_credentials rejects patient scopes``() : Task =
        task {
            let! status, body =
                issueClientCredentials "fhir-client" "smart-secret" "patient/*.read"

            assertStatus HttpStatusCode.BadRequest status
            Assert.Contains("invalid_scope", body)
        }

    [<Fact>]
    member _.``scope intersection drops disallowed scopes``() : Task =
        task {
            let! status, body =
                issueClientCredentials "fhir-client" "smart-secret" "system/Patient.r system/*.write"

            assertStatus HttpStatusCode.OK status
            use doc = JsonDocument.Parse(body)
            let scope = doc.RootElement.GetProperty("scope").GetString()
            Assert.Contains("system/Patient.r", scope)
            Assert.DoesNotContain("system/*.write", scope)
        }

    [<Fact>]
    member _.``system Patient.r allows read but not search``() : Task =
        task {
            let! createStatus, createBody =
                issueClientCredentials "fhir-client" "smart-secret" "system/*.cruds"

            assertStatus HttpStatusCode.OK createStatus
            use createDoc = JsonDocument.Parse(createBody)
            let adminToken = createDoc.RootElement.GetProperty("access_token").GetString()

            let! postStatus, postBody, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"ROnly"}]}""")
                    [ ("Authorization", $"Bearer {adminToken}") ]

            assertStatus HttpStatusCode.Created postStatus
            let id = propString postBody "id" |> Option.defaultValue ""

            let! status, body =
                issueClientCredentials "fhir-client" "smart-secret" "system/Patient.r"

            assertStatus HttpStatusCode.OK status
            use doc = JsonDocument.Parse(body)
            let token = doc.RootElement.GetProperty("access_token").GetString()

            let! searchStatus, _, _ =
                send client HttpMethod.Get "/Patient" None [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.Forbidden searchStatus

            let! readStatus, readBody, _ =
                send
                    client
                    HttpMethod.Get
                    $"/Patient/{id}"
                    None
                    [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.OK readStatus
            assertResourceType "Patient" readBody
        }

    [<Fact>]
    member _.``DCR authorize PKCE patient token filters compartment``() : Task =
        task {
            let redirect = "http://localhost/callback"
            let! clientId, secret = registerClient redirect "openid fhirUser launch/patient patient/*.rs"
            let! patientId = createPatient ()
            Assert.False(String.IsNullOrWhiteSpace patientId)

            let otherJson = """{"resourceType":"Patient","name":[{"family":"Other"}]}"""
            let basic = basicAuth "fhir" "secret"

            let! otherStatus, otherBody, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some otherJson)
                    [ ("Authorization", $"Basic {basic}") ]

            assertStatus HttpStatusCode.Created otherStatus
            let otherId = propString otherBody "id" |> Option.defaultValue ""

            let verifier = "e2e-pkce-verifier-abcdefghijklmnopqrstuvwxyz0123456789"
            let challenge = s256 verifier

            let! code =
                authorizeCode clientId redirect "launch/patient patient/*.rs" challenge (Some patientId)

            let! token, tokenBody = exchangeCode clientId secret redirect code verifier
            Assert.Contains($"\"patient\":\"{patientId}\"", tokenBody.Replace(" ", ""))

            let! searchStatus, searchBody, _ =
                send client HttpMethod.Get "/Patient" None [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.OK searchStatus
            Assert.Contains(patientId, searchBody)
            Assert.DoesNotContain(otherId, searchBody)

            let! conflictStatus, conflictBody, _ =
                send
                    client
                    HttpMethod.Get
                    $"/Patient?_id={otherId}"
                    None
                    [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.Forbidden conflictStatus
            Assert.Contains("forbidden", conflictBody)

            let! otherReadStatus, _, _ =
                send
                    client
                    HttpMethod.Get
                    $"/Patient/{otherId}"
                    None
                    [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.Forbidden otherReadStatus
        }

    [<Fact>]
    member _.``discovery advertises registration_endpoint``() : Task =
        task {
            let! status, body = get client "/.well-known/smart-configuration"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("registration_endpoint", body)
            Assert.Contains("/auth/register", body)
            Assert.Contains("authorization_code", body)
        }
