module FhirServer.E2E.Endpoints.OrgBacIsolationTests

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

type OrgBacFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)

        builder
            .UseSetting("Auth:Username", "fhir")
            .UseSetting("Auth:Password", "secret")
            .UseSetting("Smart:Clients:0:ClientId", "org-a")
            .UseSetting("Smart:Clients:0:ClientSecret", "secret-a")
            .UseSetting("Smart:Clients:0:AllowedScopes", "system/*.cruds")
            .UseSetting("Smart:Clients:0:GrantTypes", "client_credentials")
            .UseSetting("Smart:Clients:0:OrganizationId", "hospital-a")
            .UseSetting("Smart:Clients:1:ClientId", "org-b")
            .UseSetting("Smart:Clients:1:ClientSecret", "secret-b")
            .UseSetting("Smart:Clients:1:AllowedScopes", "system/*.cruds")
            .UseSetting("Smart:Clients:1:GrantTypes", "client_credentials")
            .UseSetting("Smart:Clients:1:OrganizationId", "hospital-b")
        |> ignore

type OrgBacIsolationTests() =
    let factory = new OrgBacFactory()
    let client = factory.CreateClient()

    let issueToken (clientId: string) (secret: string) =
        task {
            let basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"))
            use request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
            request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}") |> ignore

            request.Content <-
                new StringContent(
                    "grant_type=client_credentials&scope=system/*.cruds",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )

            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK response.StatusCode
            use doc = JsonDocument.Parse(body)
            let token = doc.RootElement.GetProperty("access_token").GetString()
            let org = doc.RootElement.GetProperty("organization").GetString()
            return token, org
        }

    let bearer token = [ ("Authorization", $"Bearer {token}") ]

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``Token includes organization and isolates Patient search``() : Task =
        task {
            let! tokenA, orgA = issueToken "org-a" "secret-a"
            let! tokenB, orgB = issueToken "org-b" "secret-b"
            Assert.Equal("hospital-a", orgA)
            Assert.Equal("hospital-b", orgB)

            let! createA, bodyA, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"Alpha"}]}""")
                    (bearer tokenA)

            assertStatus HttpStatusCode.Created createA
            let idA = propString bodyA "id" |> Option.defaultValue ""

            let! createB, bodyB, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"Beta"}]}""")
                    (bearer tokenB)

            assertStatus HttpStatusCode.Created createB
            let idB = propString bodyB "id" |> Option.defaultValue ""

            let! searchA, searchBodyA, _ =
                send client HttpMethod.Get "/Patient" None (bearer tokenA)

            assertStatus HttpStatusCode.OK searchA
            Assert.Contains(idA, searchBodyA)
            Assert.DoesNotContain(idB, searchBodyA)

            let! searchB, searchBodyB, _ =
                send client HttpMethod.Get "/Patient" None (bearer tokenB)

            assertStatus HttpStatusCode.OK searchB
            Assert.Contains(idB, searchBodyB)
            Assert.DoesNotContain(idA, searchBodyB)

            let! getCross, _, _ =
                send client HttpMethod.Get $"/Patient/{idA}" None (bearer tokenB)

            assertStatus HttpStatusCode.NotFound getCross
        }

    [<Fact>]
    member _.``Basic admin sees resources from both organizations``() : Task =
        task {
            let! tokenA, _ = issueToken "org-a" "secret-a"
            let! tokenB, _ = issueToken "org-b" "secret-b"

            let! _, bodyA, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"AdminA"}]}""")
                    (bearer tokenA)

            let! _, bodyB, _ =
                send
                    client
                    HttpMethod.Post
                    "/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"AdminB"}]}""")
                    (bearer tokenB)

            let idA = propString bodyA "id" |> Option.defaultValue ""
            let idB = propString bodyB "id" |> Option.defaultValue ""
            let basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir:secret"))

            let! status, body, _ =
                send client HttpMethod.Get "/Patient" None [ ("Authorization", $"Basic {basic}") ]

            assertStatus HttpStatusCode.OK status
            Assert.Contains(idA, body)
            Assert.Contains(idB, body)
        }
