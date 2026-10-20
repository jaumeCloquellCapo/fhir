module FhirServer.E2E.Endpoints.ImportHl7ExampleTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

/// E2E `$import` / `$load` against official HL7 FHIR R4 example NDJSON fixtures.
type ImportHl7ExampleTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let postNdjson (path: string) (ndjson: string) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, path)
            request.Content <- new StringContent(ndjson, Encoding.UTF8, "application/fhir+ndjson")
            use! response = client.SendAsync(request)
            let! body = response.Content.ReadAsStringAsync()
            return response.StatusCode, body
        }

    let parameterInt (body: string) (name: string) =
        use doc = JsonDocument.Parse(body)
        let mutable parameters = Unchecked.defaultof<JsonElement>

        if not (doc.RootElement.TryGetProperty("parameter", &parameters)) then
            None
        else
            parameters.EnumerateArray()
            |> Seq.tryPick (fun p ->
                let mutable n = Unchecked.defaultof<JsonElement>
                let mutable v = Unchecked.defaultof<JsonElement>

                if
                    p.TryGetProperty("name", &n)
                    && n.GetString() = name
                    && p.TryGetProperty("valueInteger", &v)
                then
                    Some(v.GetInt32())
                else
                    None)

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``POST /$import HL7 patients NDJSON creates resources``() : Task =
        task {
            let ndjson = FixtureFiles.read "hl7-patients.ndjson"
            let firstId = FixtureFiles.firstResourceId "hl7-patients.ndjson"

            let! status, body = postNdjson "/$import" ndjson
            assertStatus HttpStatusCode.OK status
            assertResourceType "Parameters" body

            match parameterInt body "created" with
            | Some 3 -> ()
            | Some n -> Assert.Fail($"expected 3 created, got {n}; body={body}")
            | None -> Assert.Fail($"missing created parameter: {body}")

            match parameterInt body "errors" with
            | Some 0 -> ()
            | Some n -> Assert.Fail($"expected 0 errors, got {n}; body={body}")
            | None -> Assert.Fail($"missing errors parameter: {body}")

            let! readStatus, readBody = get client $"/Patient/{firstId}"
            assertStatus HttpStatusCode.OK readStatus
            Assert.Contains("\"resourceType\":\"Patient\"", readBody)
            Assert.Contains("Chalmers", readBody)
        }

    [<Fact>]
    member _.``POST /$load HL7 mixed NDJSON imports orgs patients observations``() : Task =
        task {
            let ndjson = FixtureFiles.read "hl7-mixed.ndjson"
            let patientId = FixtureFiles.firstResourceId "hl7-patients.ndjson"
            let orgId = FixtureFiles.firstResourceId "hl7-organizations.ndjson"
            let obsId = FixtureFiles.firstResourceId "hl7-observations.ndjson"

            let! status, body = postNdjson "/$load" ndjson
            assertStatus HttpStatusCode.OK status
            assertResourceType "Parameters" body

            match parameterInt body "total" with
            | Some 6 -> ()
            | Some n -> Assert.Fail($"expected total 6, got {n}; body={body}")
            | None -> Assert.Fail($"missing total: {body}")

            match parameterInt body "errors" with
            | Some 0 -> ()
            | Some n -> Assert.Fail($"expected 0 errors, got {n}; body={body}")
            | None -> Assert.Fail($"missing errors: {body}")

            let! orgStatus, orgBody = get client $"/Organization/{orgId}"
            assertStatus HttpStatusCode.OK orgStatus
            Assert.Contains("Health Level Seven International", orgBody)

            let! patientStatus, patientBody = get client $"/Patient/{patientId}"
            assertStatus HttpStatusCode.OK patientStatus
            Assert.Contains("Chalmers", patientBody)

            let! obsStatus, obsBody = get client $"/Observation/{obsId}"
            assertStatus HttpStatusCode.OK obsStatus
            Assert.Contains($"Patient/{patientId}", obsBody)
        }

    [<Fact>]
    member _.``POST /$import HL7 patients then /$load is idempotent update``() : Task =
        task {
            let ndjson = FixtureFiles.read "hl7-patients.ndjson"
            let firstId = FixtureFiles.firstResourceId "hl7-patients.ndjson"

            let! s1, b1 = postNdjson "/$import" ndjson
            assertStatus HttpStatusCode.OK s1
            Assert.Equal(Some 3, parameterInt b1 "created")

            let! s2, b2 = postNdjson "/$load" ndjson
            assertStatus HttpStatusCode.OK s2
            Assert.Equal(Some 3, parameterInt b2 "updated")
            Assert.Equal(Some 0, parameterInt b2 "created")
            Assert.Equal(Some 0, parameterInt b2 "errors")

            let! readStatus, _ = get client $"/Patient/{firstId}"
            assertStatus HttpStatusCode.OK readStatus
        }
