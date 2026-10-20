module FhirServer.E2E.Endpoints.SmartScopeTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type ScopeAuthFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)

        builder
            .UseSetting("Auth:Enabled", "true")
            .UseSetting("Auth:Username", "fhir")
            .UseSetting("Auth:Password", "secret")
            .UseSetting("Smart:Clients:0:ClientId", "fhir-client")
            .UseSetting("Smart:Clients:0:ClientSecret", "smart-secret")
            .UseSetting(
                "Smart:Clients:0:AllowedScopes",
                "system/*.read system/*.write system/*.cruds system/Patient.read openid fhirUser"
            )
            .UseSetting("Smart:Clients:0:GrantTypes", "client_credentials authorization_code")
            .UseSetting("Smart:Clients:0:RedirectUris:0", "http://localhost/callback")
        |> ignore

type SmartScopeTests() =
    let factory = new ScopeAuthFactory()
    let client = factory.CreateClient()

    let issueToken (scope: string) =
        task {
            let basic =
                Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir-client:smart-secret"))

            use request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")

            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}")
            |> ignore

            request.Content <-
                new StringContent(
                    $"grant_type=client_credentials&scope={Uri.EscapeDataString scope}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )

            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK response.StatusCode
            use doc = JsonDocument.Parse(body)
            return doc.RootElement.GetProperty("access_token").GetString()
        }

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``Patient.read scope allows GET Patient but not POST Observation``() : Task =
        task {
            let! token = issueToken "system/Patient.read"

            let! getStatus, getBody, _ =
                send client HttpMethod.Get "/Patient" None [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.OK getStatus
            assertResourceType "Bundle" getBody

            let! postStatus, postBody, _ =
                send
                    client
                    HttpMethod.Post
                    "/Observation"
                    (Some
                        """{"resourceType":"Observation","status":"final","code":{"coding":[{"code":"1"}]}}""")
                    [ ("Authorization", $"Bearer {token}") ]

            assertStatus HttpStatusCode.Forbidden postStatus
            Assert.Contains("forbidden", postBody)
        }

    [<Fact>]
    member _.``Basic auth uses configured system scopes``() : Task =
        task {
            let basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir:secret"))

            let! status, body, _ =
                send client HttpMethod.Get "/Patient" None [ ("Authorization", $"Basic {basic}") ]

            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
        }
