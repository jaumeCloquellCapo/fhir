module FhirServer.E2E.Endpoints.IncludeEverythingDumpTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type IncludeEverythingDumpTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let createPatient family =
        postJson
            client
            "/Patient"
            $"""{{"resourceType":"Patient","name":[{{"family":"{family}","given":["Ana"]}}],"gender":"female"}}"""

    let createObservation patientId =
        postJson
            client
            "/Observation"
            $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"1234-5"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``GET /Observation?_include=Observation:patient includes Patient``() : Task =
        task {
            let! _, patient, _ = createPatient "IncludeMe"
            let patientId = propString patient "id" |> Option.defaultValue ""
            let! _ = createObservation patientId

            let! status, body =
                get client $"/Observation?patient={patientId}&_include=Observation:patient"

            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("\"mode\":\"match\"", body)
            Assert.Contains("\"mode\":\"include\"", body)
            Assert.Contains("IncludeMe", body)
            Assert.Contains("\"resourceType\":\"Patient\"", body)
            Assert.Contains("\"resourceType\":\"Observation\"", body)
        }

    [<Fact>]
    member _.``GET /Patient/id/$everything returns patient and related observations``() : Task =
        task {
            let! _, patient, _ = createPatient "Everything"
            let patientId = propString patient "id" |> Option.defaultValue ""
            let! _ = createObservation patientId

            let! status, body = get client $"/Patient/{patientId}/$everything"
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("Everything", body)
            Assert.Contains("\"resourceType\":\"Observation\"", body)
            Assert.Contains(patientId, body)
        }

    [<Fact>]
    member _.``GET /Patient/$dump returns NDJSON patients``() : Task =
        task {
            let! _ = createPatient "DumpOne"
            let! _ = createPatient "DumpTwo"

            let! status, body = get client "/Patient/$dump"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("DumpOne", body)
            Assert.Contains("DumpTwo", body)
            Assert.Contains("\"resourceType\":\"Patient\"", body)

            let lines =
                body.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.filter (fun l -> l.Contains("\"resourceType\""))

            Assert.True(lines.Length >= 2, sprintf "expected >=2 NDJSON lines, got %d" lines.Length)
        }

    [<Fact>]
    member _.``POST / transaction resolves urn:uuid references``() : Task =
        task {
            let patientUrn = "urn:uuid:11111111-1111-1111-1111-111111111111"

            let bundle =
                $"""
                {{
                  "resourceType": "Bundle",
                  "type": "transaction",
                  "entry": [
                    {{
                      "fullUrl": "{patientUrn}",
                      "request": {{ "method": "POST", "url": "Patient" }},
                      "resource": {{
                        "resourceType": "Patient",
                        "name": [{{ "family": "UrnTx", "given": ["Bob"] }}],
                        "gender": "male"
                      }}
                    }},
                    {{
                      "fullUrl": "urn:uuid:22222222-2222-2222-2222-222222222222",
                      "request": {{ "method": "POST", "url": "Observation" }},
                      "resource": {{
                        "resourceType": "Observation",
                        "status": "final",
                        "code": {{ "coding": [{{ "system": "http://loinc.org", "code": "8867-4" }}] }},
                        "subject": {{ "reference": "{patientUrn}" }}
                      }}
                    }}
                  ]
                }}
                """

            let! status, body, _ = postJson client "/" bundle
            assertStatus HttpStatusCode.OK status
            assertResourceType "Bundle" body
            Assert.Contains("UrnTx", body)
            Assert.DoesNotContain(patientUrn, body)
            Assert.Contains("Patient/", body)
            Assert.Contains("\"reference\":\"Patient/", body)
        }
