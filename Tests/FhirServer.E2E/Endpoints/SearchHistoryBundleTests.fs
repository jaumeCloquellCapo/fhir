module FhirServer.E2E.Endpoints.SearchHistoryBundleTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type SearchHistoryBundleTests() =
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
    member _.``GET /Patient?family= searches``() : Task =
        task {
            let! _ = createPatient "Garcia"
            let! status, body = get client "/Patient?family=Garcia"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("Garcia", body)
        }

    [<Fact>]
    member _.``GET /Patient/id/_history returns versions``() : Task =
        task {
            let! _, created, createResp = createPatient "Hist"
            let id = propString created "id" |> Option.defaultValue ""
            let etag = etagOf createResp created

            let! _ =
                putJson
                    client
                    $"/Patient/{id}"
                    $"""{{"resourceType":"Patient","id":"{id}","name":[{{"family":"Hist2"}}],"gender":"female"}}"""
                    [ ("If-Match", etag) ]

            let! status, body = get client $"/Patient/{id}/_history"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("history", body)
        }

    [<Fact>]
    member _.``GET /Patient/id/_history/vid vreads``() : Task =
        task {
            let! _, created, createResp = createPatient "VRead"
            let id = propString created "id" |> Option.defaultValue ""
            let vid = etagOf createResp created

            let! status, body = get client $"/Patient/{id}/_history/{vid}"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Patient" body
        }

    [<Fact>]
    member _.``GET /Patient/_history and /_history succeed``() : Task =
        task {
            let! _ = createPatient "TypeHist"
            let! s1, b1 = get client "/Patient/_history?_count=5"
            assertStatus HttpStatusCode.OK s1
            assertResourceType "Bundle" b1

            let! s2, b2 = get client "/_history?_count=5"
            assertStatus HttpStatusCode.OK s2
            assertResourceType "Bundle" b2
        }

    [<Fact>]
    member _.``POST / transaction bundle creates patient``() : Task =
        task {
            let bundle =
                """
                {
                  "resourceType": "Bundle",
                  "type": "transaction",
                  "entry": [
                    {
                      "request": { "method": "POST", "url": "Patient" },
                      "resource": {
                        "resourceType": "Patient",
                        "name": [{ "family": "BundleTx", "given": ["Bob"] }],
                        "gender": "male"
                      }
                    }
                  ]
                }
                """

            let! status, body, _ = postJson client "/" bundle
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("BundleTx", body)
        }

    [<Fact>]
    member _.``transaction bundle rolls back prior entries on failure``() : Task =
        task {
            let marker = Guid.NewGuid().ToString("N")

            let bundle =
                $"""
                {{
                  "resourceType": "Bundle",
                  "type": "transaction",
                  "entry": [
                    {{
                      "request": {{ "method": "POST", "url": "Patient" }},
                      "resource": {{
                        "resourceType": "Patient",
                        "id": "tx-ok-{marker}",
                        "name": [{{ "family": "TxRollbackOk" }}],
                        "gender": "male"
                      }}
                    }},
                    {{
                      "request": {{ "method": "POST", "url": "Patient" }}
                    }}
                  ]
                }}
                """

            let! status, _, _ = postJson client "/" bundle
            Assert.True(status = HttpStatusCode.BadRequest || int status >= 400)

            let! getStatus, _ = get client $"/Patient/tx-ok-{marker}"
            assertStatus HttpStatusCode.NotFound getStatus
        }

    [<Fact>]
    member _.``create with existing id returns Conflict``() : Task =
        task {
            let id = Guid.NewGuid().ToString("N")

            let body =
                $"""{{"resourceType":"Patient","id":"{id}","name":[{{"family":"Dup"}}],"gender":"female"}}"""

            let! s1, _, _ = postJson client "/Patient" body
            assertStatus HttpStatusCode.Created s1

            let! s2, b2, _ = postJson client "/Patient" body
            assertStatus HttpStatusCode.Conflict s2
            Assert.Contains("already exists", b2)
        }
