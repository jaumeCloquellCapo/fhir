module FhirServer.E2E.Endpoints.SearchIndexTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type SearchIndexTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let createPatient family identifierValue =
        postJson
            client
            "/Patient"
            $"""{{"resourceType":"Patient","name":[{{"family":"{family}","given":["Ana"]}}],"gender":"female","identifier":[{{"system":"http://hospital.example/mrn","value":"{identifierValue}"}}]}}"""

    let bundleTotal (body: string) =
        use doc = JsonDocument.Parse(body)
        doc.RootElement.GetProperty("total").GetInt32()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``GET /Patient?identifier= token search``() : Task =
        task {
            let! _ = createPatient "TokenFam" "MRN-IDX-1"
            let! status, body = get client "/Patient?identifier=http://hospital.example/mrn|MRN-IDX-1"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("TokenFam", body)
            Assert.Equal(1, bundleTotal body)
        }

    [<Fact>]
    member _.``GET /Patient?family=&gender= ANDs indexed params``() : Task =
        task {
            let! _ = createPatient "AndFam" "MRN-AND-1"
            let! status, body = get client "/Patient?family=AndFam&gender=female"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("AndFam", body)
            Assert.True(bundleTotal body >= 1)

            let! missStatus, missBody = get client "/Patient?family=AndFam&gender=male"
            assertStatus HttpStatusCode.OK missStatus
            Assert.Equal(0, bundleTotal missBody)
        }

    [<Fact>]
    member _.``GET /Patient?_lastUpdated=ge filters on ts``() : Task =
        task {
            let! _ = createPatient "LastUp" "MRN-LU-1"
            let past = DateTimeOffset.UtcNow.AddMinutes(-5.0).ToString("o")
            let! status, body = get client $"/Patient?family=LastUp&_lastUpdated=ge{Uri.EscapeDataString past}"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("LastUp", body)
            Assert.True(bundleTotal body >= 1)

            let future = DateTimeOffset.UtcNow.AddHours(1.0).ToString("o")
            let! missStatus, missBody =
                get client $"/Patient?family=LastUp&_lastUpdated=ge{Uri.EscapeDataString future}"

            assertStatus HttpStatusCode.OK missStatus
            Assert.Equal(0, bundleTotal missBody)
        }

    [<Fact>]
    member _.``update reindexes so old family no longer matches``() : Task =
        task {
            let! _, created, createResp = createPatient "OldFamily" "MRN-UPD-1"
            let id = propString created "id" |> Option.defaultValue ""
            let etag = etagOf createResp created

            let! _ =
                putJson
                    client
                    $"/Patient/{id}"
                    $"""{{"resourceType":"Patient","id":"{id}","name":[{{"family":"NewFamily","given":["Ana"]}}],"gender":"female","identifier":[{{"system":"http://hospital.example/mrn","value":"MRN-UPD-1"}}]}}"""
                    [ ("If-Match", etag) ]

            let! oldStatus, oldBody = get client "/Patient?family=OldFamily"
            assertStatus HttpStatusCode.OK oldStatus
            Assert.Equal(0, bundleTotal oldBody)

            let! newStatus, newBody = get client "/Patient?family=NewFamily"
            assertStatus HttpStatusCode.OK newStatus
            Assert.Contains("NewFamily", newBody)
            Assert.Equal(1, bundleTotal newBody)
        }

    [<Fact>]
    member _.``DELETE removes resource from search index``() : Task =
        task {
            let! _, created, _ = createPatient "DelFam" "MRN-DEL-1"
            let id = propString created "id" |> Option.defaultValue ""

            let! delStatus, _, _ = delete client $"/Patient/{id}" []
            assertStatus HttpStatusCode.OK delStatus

            let! status, body = get client "/Patient?family=DelFam"
            assertStatus HttpStatusCode.OK status
            Assert.Equal(0, bundleTotal body)
        }
