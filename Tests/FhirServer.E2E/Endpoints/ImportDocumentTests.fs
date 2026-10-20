module FhirServer.E2E.Endpoints.ImportDocumentTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type ImportDocumentTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``POST /$import NDJSON creates patients``() : Task =
        task {
            let ndjson =
                """
                {"resourceType":"Patient","id":"imp-1","name":[{"family":"ImportOne"}],"gender":"male"}
                {"resourceType":"Patient","id":"imp-2","name":[{"family":"ImportTwo"}],"gender":"female"}
                """
                    .Trim()

            use request = new HttpRequestMessage(HttpMethod.Post, "/$import")
            request.Content <- new StringContent(ndjson, Encoding.UTF8, "application/fhir+ndjson")
            applyE2EAuth request

            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK response.StatusCode
            assertResourceType "Parameters" body
            Assert.Contains("\"name\":\"created\"", body)

            let! s1, b1 = get client "/Patient/imp-1"
            assertStatus HttpStatusCode.OK s1
            Assert.Contains("ImportOne", b1)

            let! s2, b2 = get client "/Patient/imp-2"
            assertStatus HttpStatusCode.OK s2
            Assert.Contains("ImportTwo", b2)
        }

    [<Fact>]
    member _.``POST /$load updates existing by id``() : Task =
        task {
            let! _, created, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","id":"load-1","name":[{"family":"Before"}],"gender":"female"}"""

            Assert.Equal("load-1", propString created "id" |> Option.defaultValue "")

            let ndjson =
                """{"resourceType":"Patient","id":"load-1","name":[{"family":"After"}],"gender":"female"}"""

            use request = new HttpRequestMessage(HttpMethod.Post, "/$load")
            request.Content <- new StringContent(ndjson, Encoding.UTF8, "application/fhir+ndjson")
            applyE2EAuth request

            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            assertStatus HttpStatusCode.OK response.StatusCode
            Assert.Contains("\"name\":\"updated\"", body)

            let! _, readBody = get client "/Patient/load-1"
            Assert.Contains("After", readBody)
            Assert.DoesNotContain("Before", readBody)
        }

    [<Fact>]
    member _.``GET /Patient/id/$document returns document Bundle with Composition``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"DocMe","given":["Ana"]}],"gender":"female"}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! status, body = get client $"/Patient/{patientId}/$document"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("\"type\":\"document\"", body)
            Assert.Contains("\"resourceType\":\"Composition\"", body)
            Assert.Contains("DocMe", body)
            Assert.Contains("\"resourceType\":\"Observation\"", body)
            Assert.Contains("34133-9", body)
        }
