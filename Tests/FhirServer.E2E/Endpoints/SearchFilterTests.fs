module FhirServer.E2E.Endpoints.SearchFilterTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert
open FhirServer.E2E.SearchTestHelpers

/// `_filter` expression search.
type SearchFilterTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let mustCreate = SearchTestHelpers.mustCreate client

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``_filter and not on patient demographics``() : Task =
        task {
            let! matchId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Garcia", "given": ["Ana"] }],
                      "gender": "female",
                      "birthDate": "1988-03-12"
                    }
                    """

            let! maleGarciaId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Garcia", "given": ["Luis"] }],
                      "gender": "male",
                      "birthDate": "1990-01-05"
                    }
                    """

            let! otherFamilyId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Martinez", "given": ["Sofia"] }],
                      "gender": "female",
                      "birthDate": "1995-12-20"
                    }
                    """

            let filterAnd = enc "family eq Garcia and gender eq female"
            let! andStatus, andBody = get client (sprintf "/Patient?_filter=%s" filterAnd)
            assertStatus HttpStatusCode.OK andStatus
            assertContainsId andBody matchId
            assertNotContainsId andBody maleGarciaId
            assertNotContainsId andBody otherFamilyId

            let filterNot = enc "not (gender eq male)"

            let! notStatus, notBody =
                get client (sprintf "/Patient?family%%3Aexact=Garcia&_filter=%s" filterNot)

            assertStatus HttpStatusCode.OK notStatus
            assertContainsId notBody matchId
            assertNotContainsId notBody maleGarciaId
        }
