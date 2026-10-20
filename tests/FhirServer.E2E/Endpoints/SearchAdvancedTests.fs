module FhirServer.E2E.Endpoints.SearchAdvancedTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type SearchAdvancedTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``family:exact matches only exact family``() : Task =
        task {
            let! _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"GarciaExact","given":["Ana"]}]}"""

            let! _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"GarciaExacto","given":["Luis"]}]}"""

            let! status, body = get client "/Patient?family:exact=GarciaExact"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("GarciaExact", body)
            Assert.DoesNotContain("GarciaExacto", body)
        }

    [<Fact>]
    member _.``gender:missing=true finds patients without gender``() : Task =
        task {
            let! _, withGender, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"HasGender"}],"gender":"male"}"""

            let! _, noGender, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoGender"}]}"""

            let withId = propString withGender "id" |> Option.defaultValue ""
            let noId = propString noGender "id" |> Option.defaultValue ""

            let! status, body = get client "/Patient?gender:missing=true"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(noId, body)
            Assert.DoesNotContain($"\"id\":\"{withId}\"", body)
        }

    [<Fact>]
    member _.``birthdate ge filters patients``() : Task =
        task {
            let! _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Old"}],"birthDate":"1980-01-01"}"""

            let! stYoung, young, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Young"}],"birthDate":"2000-06-15"}"""

            Assert.True(stYoung = HttpStatusCode.Created, sprintf "young create failed %A %s" stYoung young)
            let youngId = propString young "id" |> Option.defaultValue ""

            let! status, body = get client "/Patient?birthdate=ge1990-01-01"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(youngId, body)
            Assert.Contains("Young", body)
            Assert.DoesNotContain("\"family\":\"Old\"", body)
        }

    [<Fact>]
    member _.``value-quantity filters observations``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Qty"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _, hi, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":120,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":80,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let hiId = propString hi "id" |> Option.defaultValue ""

            let! status, body =
                get client "/Observation?value-quantity=gt100|http://unitsofmeasure.org|mm[Hg]"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(hiId, body)
            Assert.DoesNotContain("\"value\":80", body)
        }

    [<Fact>]
    member _.``_has Observation patient code finds patients``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"HasObs"}]}"""

            let! _, other, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoObs"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""
            let otherId = propString other "id" |> Option.defaultValue ""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"9999-1"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! status, body =
                get client "/Patient?_has:Observation:patient:code=http://loinc.org|9999-1"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(patientId, body)
            Assert.Contains("HasObs", body)
            Assert.DoesNotContain(otherId, body)
        }
