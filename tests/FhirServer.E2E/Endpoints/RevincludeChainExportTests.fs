module FhirServer.E2E.Endpoints.RevincludeChainExportTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type RevincludeChainExportTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let createPatient family =
        postJson
            client
            "/Patient"
            $"""{{"resourceType":"Patient","name":[{{"family":"{family}","given":["Ana"]}}],"gender":"female","identifier":[{{"system":"http://hospital.example/mrn","value":"MRN-{family}"}}]}}"""

    let createObservation patientId code =
        postJson
            client
            "/Observation"
            $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"{code}"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``GET /Patient?_revinclude=Observation:patient returns observations``() : Task =
        task {
            let! _, patient, _ = createPatient "RevInc"
            let patientId = propString patient "id" |> Option.defaultValue ""
            let! _ = createObservation patientId "1111-1"

            let! status, body =
                get client $"/Patient?_id={patientId}&_revinclude=Observation:patient"

            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("\"mode\":\"match\"", body)
            Assert.Contains("\"mode\":\"include\"", body)
            Assert.Contains("RevInc", body)
            Assert.Contains("\"resourceType\":\"Observation\"", body)
        }

    [<Fact>]
    member _.``GET /Observation?subject.name= chains to Patient name``() : Task =
        task {
            let! _, patient, _ = createPatient "ChainFamily"
            let patientId = propString patient "id" |> Option.defaultValue ""
            let! _ = createObservation patientId "2222-2"

            let! status, body = get client "/Observation?subject.name=ChainFamily"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("2222-2", body)
            Assert.Contains(patientId, body)
        }

    [<Fact>]
    member _.``POST / transaction conditional PUT Type?identifier= updates``() : Task =
        task {
            let! _, patient, _ = createPatient "CondPut"
            let patientId = propString patient "id" |> Option.defaultValue ""

            let bundle =
                """
                {
                  "resourceType": "Bundle",
                  "type": "transaction",
                  "entry": [
                    {
                      "request": { "method": "PUT", "url": "Patient?identifier=http://hospital.example/mrn|MRN-CondPut" },
                      "resource": {
                        "resourceType": "Patient",
                        "name": [{ "family": "CondPutUpdated", "given": ["Ana"] }],
                        "gender": "female",
                        "identifier": [{ "system": "http://hospital.example/mrn", "value": "MRN-CondPut" }]
                      }
                    }
                  ]
                }
                """

            let! status, body, _ = postJson client "/" bundle
            assertStatus HttpStatusCode.OK status
            Assert.Contains("CondPutUpdated", body)

            let! _, readBody = get client $"/Patient/{patientId}"
            Assert.Contains("CondPutUpdated", readBody)
        }

    [<Fact>]
    member _.``GET /Observation/$lastn returns at most max per code``() : Task =
        task {
            let! _, patient, _ = createPatient "LastN"
            let patientId = propString patient "id" |> Option.defaultValue ""
            let! _ = createObservation patientId "3333-3"
            let! _ = createObservation patientId "3333-3"
            let! _ = createObservation patientId "3333-3"
            let! _ = createObservation patientId "4444-4"

            let! status, body = get client $"/Observation/$lastn?patient={patientId}&max=2"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body

            use doc = JsonDocument.Parse(body)
            let total = doc.RootElement.GetProperty("total").GetInt32()
            // 2 for code 3333-3 + 1 for 4444-4
            Assert.Equal(3, total)
        }

    [<Fact>]
    member _.``GET /$export returns 202 then NDJSON files``() : Task =
        task {
            let! _ = createPatient "ExportMe"

            let! status, body, response =
                send client HttpMethod.Get "/$export?_type=Patient" None []

            assertStatus HttpStatusCode.Accepted status
            Assert.True(not (isNull response.Content.Headers.ContentLocation), "expected Content-Location")

            use doc = JsonDocument.Parse(body)
            let jobId = doc.RootElement.GetProperty("jobId").GetString()

            let! statusStatus, statusBody = get client $"/$export-status/{jobId}"
            assertStatus HttpStatusCode.OK statusStatus
            Assert.Contains("output", statusBody)
            Assert.Contains($"$export-files/{jobId}/Patient", statusBody)

            let! fileStatus, fileBody = get client $"/$export-files/{jobId}/Patient"
            assertStatus HttpStatusCode.OK fileStatus
            Assert.Contains("ExportMe", fileBody)
            Assert.Contains("\"resourceType\":\"Patient\"", fileBody)
        }
