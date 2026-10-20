module FhirServer.E2E.Endpoints.ValidateTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type ValidateTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``POST /Patient/$validate invalid returns 200 OperationOutcome fatals``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","birthDate":"2024-13-45","gender":"M"}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("Month should be in range", body)
            Assert.Contains("administrative-gender", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate valid returns all ok``() : Task =
        task {
            // Include Narrative so dom-6 (warning) passes; unsupported dom-3 still warns.
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","text":{"status":"generated","div":"<div xmlns=\"http://www.w3.org/1999/xhtml\">Ok</div>"},"name":[{"family":"Ok"}],"gender":"female"}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.DoesNotContain("\"severity\":\"fatal\"", body)
            Assert.Contains("dom-3", body) // unevaluable full FHIRPath → warning
        }

    [<Fact>]
    member _.``POST /$validate without type works``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/$validate"
                    """{"resourceType":"Patient","text":{"status":"generated","div":"<div xmlns=\"http://www.w3.org/1999/xhtml\">Ok</div>"},"gender":"female"}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.DoesNotContain("\"severity\":\"fatal\"", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate reports pat-1 on incomplete contact``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","contact":[{"gender":"male"}]}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("pat-1", body)
        }

    [<Fact>]
    member _.``POST /Observation/$validate applies bmi profile requireds``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Observation/$validate"
                    """{"resourceType":"Observation","meta":{"profile":["http://hl7.org/fhir/StructureDefinition/bmi"]},"status":"final","code":{"coding":[{"system":"http://loinc.org","code":"39156-5"}]}}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("subject: required", body)
            Assert.Contains("\"severity\":\"fatal\"", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate warns on unknown meta.profile``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","meta":{"profile":["http://example.org/StructureDefinition/UnknownProfile"]},"gender":"female"}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("not loaded", body)
            Assert.Contains("warning", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate rejects bad HumanName.use``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","name":[{"family":"X","use":"not-a-use"}]}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("name-use", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate rejects empty complex object (ele-1)``() : Task =
        task {
            let! status, body, _ =
                postJson client "/Patient/$validate" """{"resourceType":"Patient","name":[{}]}"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("ele-1", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate rejects bad dateTime format``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","deceasedDateTime":"not-a-datetime"}"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("dateTime", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate rejects bad meta.profile``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","meta":{"profile":["not a uri"]}}"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("canonical", body)
        }

    [<Fact>]
    member _.``POST /Observation/$validate?profile= applies forced bmi profile``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Observation/$validate?profile=http://hl7.org/fhir/StructureDefinition/bmi"
                    """{"resourceType":"Observation","status":"final","code":{"coding":[{"system":"http://loinc.org","code":"39156-5"}]}}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("subject: required", body)
            Assert.Contains("\"severity\":\"fatal\"", body)
        }

    [<Fact>]
    member _.``POST /Patient/$validate warns on dangling Reference``() : Task =
        task {
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","gender":"female","generalPractitioner":[{"reference":"Practitioner/does-not-exist"}]}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("Practitioner/does-not-exist", body)
            Assert.Contains("warning", body)
            Assert.DoesNotContain("\"severity\":\"fatal\"", body)
        }

    [<Fact>]
    member _.``POST /FamilyMemberHistory/$validate warns on extensible binding miss``() : Task =
        task {
            // FamilyMemberHistory.sex is extensible + administrative-gender ValueSet is embedded.
            let! status, body, _ =
                postJson
                    client
                    "/FamilyMemberHistory/$validate"
                    """{"resourceType":"FamilyMemberHistory","status":"completed","patient":{"reference":"Patient/p1"},"relationship":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/v3-RoleCode","code":"FTH"}]},"sex":{"coding":[{"system":"http://hl7.org/fhir/administrative-gender","code":"NOT-A-GENDER"}]}}"""

            assertStatus HttpStatusCode.OK status
            assertResourceType "OperationOutcome" body
            Assert.Contains("NOT-A-GENDER", body)
            Assert.Contains("warning", body)
        }
