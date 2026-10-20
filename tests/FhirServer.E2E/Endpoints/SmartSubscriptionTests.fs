module FhirServer.E2E.Endpoints.SmartSubscriptionTests

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

type SmartAuthFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)

        builder
            .UseSetting("Auth:Enabled", "true")
            .UseSetting("Auth:Username", "fhir")
            .UseSetting("Auth:Password", "secret")
            .UseSetting("Smart:ClientId", "fhir-client")
            .UseSetting("Smart:ClientSecret", "smart-secret")
        |> ignore

type SmartSubscriptionTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let authFactory = new SmartAuthFactory()
    let authClient = authFactory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()
            authClient.Dispose()
            authFactory.Dispose()

    [<Fact>]
    member _.``GET /.well-known/smart-configuration returns discovery``() : Task =
        task {
            let! status, body = get client "/.well-known/smart-configuration"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("token_endpoint", body)
            Assert.Contains("/auth/token", body)
            Assert.Contains("client_credentials", body)
        }

    [<Fact>]
    member _.``POST /auth/token issues bearer token``() : Task =
        task {
            let basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir-client:change-me"))

            let! status, body, _ =
                send client HttpMethod.Post "/auth/token" (Some "grant_type=client_credentials") [
                    ("Authorization", $"Basic {basic}")
                ]

            assertStatus HttpStatusCode.OK status
            Assert.Contains("access_token", body)
            Assert.Contains("Bearer", body)
        }

    [<Fact>]
    member _.``Bearer token authorizes when Auth enabled``() : Task =
        task {
            let basic =
                Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir-client:smart-secret"))

            use tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/token")

            tokenRequest.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}")
            |> ignore

            tokenRequest.Content <-
                new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded")

            use! tokenResponse = authClient.SendAsync(tokenRequest)
            let! tokenBody = tokenResponse.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK tokenResponse.StatusCode

            use doc = JsonDocument.Parse(tokenBody)
            let accessToken = doc.RootElement.GetProperty("access_token").GetString()

            let! deniedStatus, _, _ = send authClient HttpMethod.Get "/Patient" None []

            assertStatus HttpStatusCode.Unauthorized deniedStatus

            let! okStatus, body, _ =
                send authClient HttpMethod.Get "/Patient" None [ ("Authorization", $"Bearer {accessToken}") ]

            assertStatus HttpStatusCode.OK okStatus
            assertResourceType "Bundle" body
        }

    [<Fact>]
    member _.``Subscription rest-hook notifies on Patient create``() : Task =
        task {
            let sinkId = Guid.NewGuid().ToString("N")
            let! _ = send client HttpMethod.Delete $"/_hooks/{sinkId}" None []

            let subscription =
                $"""{{
                  "resourceType":"Subscription",
                  "status":"active",
                  "topic":"http://fhirserver.local/SubscriptionTopic/Patient",
                  "channelType":{{"system":"http://terminology.hl7.org/CodeSystem/subscription-channel-type","code":"rest-hook"}},
                  "endpoint":"{client.BaseAddress}_hooks/{sinkId}",
                  "content":"full-resource",
                  "reason":"e2e"
                }}"""

            let! subStatus, subBody, _ = postJson client "/Subscription" subscription
            assertStatus HttpStatusCode.Created subStatus
            assertResourceType "Subscription" subBody

            let! createStatus, _, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"SubNotify"}],"gender":"female"}"""

            assertStatus HttpStatusCode.Created createStatus

            let! hookStatus, hookBody = get client $"/_hooks/{sinkId}"
            assertStatus HttpStatusCode.OK hookStatus
            Assert.Contains("subscription-notification", hookBody)
            Assert.Contains("SubNotify", hookBody)
            Assert.Contains("SubscriptionStatus", hookBody)
        }
