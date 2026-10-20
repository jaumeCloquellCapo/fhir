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
            let! status, body, _ =
                postJson
                    client
                    "/Patient/$validate"
                    """{"resourceType":"Patient","name":[{"family":"Ok"}],"gender":"female"}"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("all ok", body)
        }

    [<Fact>]
    member _.``POST /$validate without type works``() : Task =
        task {
            let! status, body, _ =
                postJson client "/$validate" """{"resourceType":"Patient","gender":"female"}"""

            assertStatus HttpStatusCode.OK status
            Assert.Contains("all ok", body)
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
