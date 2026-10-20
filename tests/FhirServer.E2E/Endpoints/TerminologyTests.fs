module FhirServer.E2E.Endpoints.TerminologyTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type TerminologyTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``CodeSystem lookup and ValueSet expand validate-code``() : Task =
        task {
            let! csStatus, csBody, _ =
                postJson
                    client
                    "/CodeSystem"
                    """{"resourceType":"CodeSystem","status":"active","content":"complete","url":"http://example.org/cs/colors","name":"Colors","concept":[{"code":"red","display":"Red"},{"code":"blue","display":"Blue"}]}"""

            if csStatus <> HttpStatusCode.Created then
                failwith ("CodeSystem create failed " + string (int csStatus) + " " + csBody)

            let! vsStatus, vsBody, _ =
                postJson
                    client
                    "/ValueSet"
                    """{"resourceType":"ValueSet","status":"active","url":"http://example.org/vs/colors","compose":{"include":[{"system":"http://example.org/cs/colors"}]}}"""

            if vsStatus <> HttpStatusCode.Created then
                failwith ("ValueSet create failed " + string (int vsStatus) + " " + vsBody)

            let vsId = propString vsBody "id" |> Option.defaultValue ""

            let! expandStatus, expandBody = get client $"/ValueSet/{vsId}/$expand"
            assertStatus HttpStatusCode.OK expandStatus
            Assert.Contains("\"code\":\"red\"", expandBody)
            Assert.Contains("\"code\":\"blue\"", expandBody)
            Assert.Contains("expansion", expandBody)

            let! lookupStatus, lookupBody =
                get client "/CodeSystem/$lookup?system=http://example.org/cs/colors&code=red"

            assertStatus HttpStatusCode.OK lookupStatus
            Assert.Contains("Parameters", lookupBody)
            Assert.Contains("Red", lookupBody)

            let! okStatus, okBody =
                get client $"/ValueSet/{vsId}/$validate-code?system=http://example.org/cs/colors&code=red"

            assertStatus HttpStatusCode.OK okStatus
            Assert.Contains("\"valueBoolean\":true", okBody)

            let! badStatus, badBody =
                get client $"/ValueSet/{vsId}/$validate-code?system=http://example.org/cs/colors&code=green"

            assertStatus HttpStatusCode.OK badStatus
            Assert.Contains("\"valueBoolean\":false", badBody)
        }
