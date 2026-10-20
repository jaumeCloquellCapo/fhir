module FhirServer.E2E.Endpoints.SearchModifierTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert
open FhirServer.E2E.SearchTestHelpers

/// Search modifiers: `:exact`, `:missing`.
type SearchModifierTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let mustCreate = SearchTestHelpers.mustCreate client

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``family:exact matches only exact family``() : Task =
        task {
            let! garciaId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "use": "official", "family": "Garcia", "given": ["Ana", "Maria"] }],
                      "gender": "female",
                      "birthDate": "1988-03-12",
                      "identifier": [{ "system": "urn:oid:2.16.724.4.10.10.1", "value": "NIS-Garcia-Ana" }]
                    }
                    """

            let! _, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "use": "official", "family": "GarciaExacto", "given": ["Luis"] }],
                      "gender": "male",
                      "birthDate": "1991-07-01",
                      "identifier": [{ "system": "urn:oid:2.16.724.4.10.10.1", "value": "NIS-GarciaExacto" }]
                    }
                    """

            let! status, body = get client "/Patient?family%3Aexact=Garcia"
            assertStatus HttpStatusCode.OK status
            assertContainsId body garciaId
            Assert.DoesNotContain("GarciaExacto", body)
        }

    [<Fact>]
    member _.``gender:missing=true finds patients without gender``() : Task =
        task {
            let! withId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "HasGender", "given": ["Pedro"] }],
                      "gender": "male"
                    }
                    """

            let! noId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "NoGender", "given": ["Lucia"] }]
                    }
                    """

            let! status, body = get client "/Patient?gender%3Amissing=true&family=NoGender"
            assertStatus HttpStatusCode.OK status
            assertContainsId body noId
            assertNotContainsId body withId
        }
