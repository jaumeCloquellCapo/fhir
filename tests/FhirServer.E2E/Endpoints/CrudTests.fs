module FhirServer.E2E.Endpoints.CrudTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type CrudTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let createPatient family =
        postJson
            client
            "/Patient"
            $"""{{"resourceType":"Patient","name":[{{"family":"{family}","given":["Ana"]}}],"gender":"female"}}"""

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``POST /Patient creates resource``() : Task =
        task {
            let! status, body, response = createPatient "CreateMe"
            assertStatus HttpStatusCode.Created status
            assertResourceType "Patient" body
            Assert.True(response.Headers.Contains("ETag"))
            Assert.True(not (isNull response.Headers.Location))
        }

    [<Fact>]
    member _.``GET /Patient/id reads resource``() : Task =
        task {
            let! _, created, _ = createPatient "ReadMe"
            let id = propString created "id" |> Option.defaultValue ""
            let! status, body = get client $"/Patient/{id}"
            assertStatus HttpStatusCode.OK status
            Assert.Equal(id, propString body "id" |> Option.defaultValue "")
        }

    [<Fact>]
    member _.``PUT /Patient/id updates resource``() : Task =
        task {
            let! _, created, createResp = createPatient "UpdateMe"
            let id = propString created "id" |> Option.defaultValue ""
            let etag = etagOf createResp created

            let! status, body, _ =
                putJson
                    client
                    $"/Patient/{id}"
                    $"""{{"resourceType":"Patient","id":"{id}","name":[{{"family":"Updated","given":["Ana"]}}],"gender":"female"}}"""
                    [ ("If-Match", etag) ]

            assertStatus HttpStatusCode.OK status
            Assert.Contains("Updated", body)
        }

    [<Fact>]
    member _.``PUT with wrong If-Match returns 412``() : Task =
        task {
            let! _, created, _ = createPatient "Precondition"
            let id = propString created "id" |> Option.defaultValue ""

            let! status, _, _ =
                putJson
                    client
                    $"/Patient/{id}"
                    $"""{{"resourceType":"Patient","id":"{id}","name":[{{"family":"X"}}],"gender":"female"}}"""
                    [ ("If-Match", "99999") ]

            assertStatus HttpStatusCode.PreconditionFailed status
        }

    [<Fact>]
    member _.``PATCH /Patient/id replaces gender``() : Task =
        task {
            let! _, created, _ = createPatient "PatchMe"
            let id = propString created "id" |> Option.defaultValue ""

            let! status, body, _ =
                patchJson client $"/Patient/{id}" """[{"op":"replace","path":"/gender","value":"male"}]"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("male", body)
        }

    [<Fact>]
    member _.``DELETE /Patient/id returns 200 then GET is 410``() : Task =
        task {
            let! _, created, _ = createPatient "DeleteMe"
            let id = propString created "id" |> Option.defaultValue ""

            let! delStatus, delBody, _ = delete client $"/Patient/{id}" []
            assertStatus HttpStatusCode.OK delStatus
            assertResourceType "Patient" delBody

            let! getStatus, getBody = get client $"/Patient/{id}"
            assertStatus HttpStatusCode.Gone getStatus
            Assert.Contains("deleted", getBody)
        }

    [<Fact>]
    member _.``GET unknown id returns 404``() : Task =
        task {
            let! status, _ = get client "/Patient/does-not-exist"
            assertStatus HttpStatusCode.NotFound status
        }
