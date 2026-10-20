module FhirServer.E2E.Endpoints.OrgBacHierarchyTests

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

type OrgBacHierarchyFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)

        builder
            .UseSetting("Auth:Enabled", "true")
            .UseSetting("Auth:Username", "fhir")
            .UseSetting("Auth:Password", "secret")
            .UseSetting("Smart:Clients:0:ClientId", "client-a")
            .UseSetting("Smart:Clients:0:ClientSecret", "secret-a")
            .UseSetting("Smart:Clients:0:AllowedScopes", "system/*.cruds")
            .UseSetting("Smart:Clients:0:GrantTypes", "client_credentials")
            .UseSetting("Smart:Clients:0:OrganizationId", "org-a")
            .UseSetting("Smart:Clients:1:ClientId", "client-b")
            .UseSetting("Smart:Clients:1:ClientSecret", "secret-b")
            .UseSetting("Smart:Clients:1:AllowedScopes", "system/*.cruds")
            .UseSetting("Smart:Clients:1:GrantTypes", "client_credentials")
            .UseSetting("Smart:Clients:1:OrganizationId", "org-b")
            .UseSetting("Smart:Clients:2:ClientId", "client-c")
            .UseSetting("Smart:Clients:2:ClientSecret", "secret-c")
            .UseSetting("Smart:Clients:2:AllowedScopes", "system/*.cruds")
            .UseSetting("Smart:Clients:2:GrantTypes", "client_credentials")
            .UseSetting("Smart:Clients:2:OrganizationId", "org-c")
        |> ignore

type OrgBacHierarchyTests() =
    let factory = new OrgBacHierarchyFactory()
    let client = factory.CreateClient()

    let basicAdmin =
        let token = Convert.ToBase64String(Encoding.UTF8.GetBytes("fhir:secret"))
        [ ("Authorization", $"Basic {token}") ]

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
            return doc.RootElement.GetProperty("access_token").GetString()
        }

    let bearer token = [ ("Authorization", $"Bearer {token}") ]

    let ensureOrgTree () =
        task {
            let! s1, _, _ =
                send
                    client
                    HttpMethod.Put
                    "/Organization/org-a"
                    (Some """{"resourceType":"Organization","id":"org-a","name":"A"}""")
                    basicAdmin

            Assert.True(s1 = HttpStatusCode.OK || s1 = HttpStatusCode.Created)

            let! s2, _, _ =
                send
                    client
                    HttpMethod.Put
                    "/Organization/org-b"
                    (Some
                        """{"resourceType":"Organization","id":"org-b","name":"B","partOf":{"reference":"Organization/org-a"}}""")
                    basicAdmin

            Assert.True(s2 = HttpStatusCode.OK || s2 = HttpStatusCode.Created)

            let! s3, _, _ =
                send
                    client
                    HttpMethod.Put
                    "/Organization/org-c"
                    (Some
                        """{"resourceType":"Organization","id":"org-c","name":"C","partOf":{"reference":"Organization/org-a"}}""")
                    basicAdmin

            Assert.True(s3 = HttpStatusCode.OK || s3 = HttpStatusCode.Created)
        }

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``Parent org API sees child Patient; sibling does not``() : Task =
        task {
            do! ensureOrgTree ()
            let! tokenA = issueToken "client-a" "secret-a"
            let! tokenB = issueToken "client-b" "secret-b"
            let! tokenC = issueToken "client-c" "secret-c"

            let! createB, bodyB, _ =
                send
                    client
                    HttpMethod.Post
                    "/Organization/org-b/fhir/Patient"
                    (Some """{"resourceType":"Patient","name":[{"family":"ChildB"}]}""")
                    (bearer tokenB)

            assertStatus HttpStatusCode.Created createB
            let idB = propString bodyB "id" |> Option.defaultValue ""

            let! getA, _, _ =
                send client HttpMethod.Get $"/Organization/org-a/fhir/Patient/{idB}" None (bearer tokenA)

            assertStatus HttpStatusCode.OK getA

            let! getB, _, _ =
                send client HttpMethod.Get $"/Organization/org-b/fhir/Patient/{idB}" None (bearer tokenB)

            assertStatus HttpStatusCode.OK getB

            let! getC, _, _ =
                send client HttpMethod.Get $"/Organization/org-c/fhir/Patient/{idB}" None (bearer tokenC)

            assertStatus HttpStatusCode.NotFound getC
        }

    [<Fact>]
    member _.``Token org mismatch on org path returns 403``() : Task =
        task {
            do! ensureOrgTree ()
            let! tokenB = issueToken "client-b" "secret-b"

            let! status, _, _ =
                send
                    client
                    HttpMethod.Get
                    "/Organization/org-a/fhir/Patient"
                    None
                    (bearer tokenB)

            assertStatus HttpStatusCode.Forbidden status
        }

    [<Fact>]
    member _.``Shared resource readable from child; update from child forbidden``() : Task =
        task {
            do! ensureOrgTree ()
            let! tokenA = issueToken "client-a" "secret-a"
            let! tokenB = issueToken "client-b" "secret-b"

            let sharedBody =
                """{"resourceType":"Patient","name":[{"family":"SharedPat"}],"meta":{"extension":[{"url":"https://aidbox.app/tenant-resource-mode","valueString":"shared"}]}}"""

            let! createA, bodyA, _ =
                send
                    client
                    HttpMethod.Post
                    "/Organization/org-a/fhir/Patient"
                    (Some sharedBody)
                    (bearer tokenA)

            assertStatus HttpStatusCode.Created createA
            let id = propString bodyA "id" |> Option.defaultValue ""

            let! getB, bodyGet, _ =
                send client HttpMethod.Get $"/Organization/org-b/fhir/Patient/{id}" None (bearer tokenB)

            assertStatus HttpStatusCode.OK getB
            Assert.Contains("SharedPat", bodyGet)

            let! putB, _, _ =
                send
                    client
                    HttpMethod.Put
                    $"/Organization/org-b/fhir/Patient/{id}"
                    (Some
                        """{"resourceType":"Patient","name":[{"family":"Hacked"}],"meta":{"extension":[{"url":"https://aidbox.app/tenant-resource-mode","valueString":"shared"},{"url":"https://aidbox.app/tenant-organization-id","valueReference":{"reference":"Organization/org-a"}}]}}""")
                    (bearer tokenB)

            assertStatus HttpStatusCode.Forbidden putB
        }

    [<Fact>]
    member _.``System-shared readable from org API; mutate forbidden``() : Task =
        task {
            do! ensureOrgTree ()
            let! tokenB = issueToken "client-b" "secret-b"

            let sysBody =
                """{"resourceType":"Patient","id":"global-pat","name":[{"family":"Global"}],"meta":{"extension":[{"url":"https://aidbox.app/tenant-resource-mode","valueString":"system-shared"}]}}"""

            let! createRoot, createBody, _ =
                send client HttpMethod.Put "/Patient/global-pat" (Some sysBody) basicAdmin

            Assert.True(
                createRoot = HttpStatusCode.OK || createRoot = HttpStatusCode.Created,
                $"expected 200/201 for system-shared create, got {createRoot}: {createBody}"
            )

            let! getB, _, _ =
                send
                    client
                    HttpMethod.Get
                    "/Organization/org-b/fhir/Patient/global-pat"
                    None
                    (bearer tokenB)

            assertStatus HttpStatusCode.OK getB

            let! putB, _, _ =
                send
                    client
                    HttpMethod.Put
                    "/Organization/org-b/fhir/Patient/global-pat"
                    (Some sysBody)
                    (bearer tokenB)

            assertStatus HttpStatusCode.Forbidden putB
        }
