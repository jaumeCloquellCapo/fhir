module FhirServer.E2E.Endpoints.RootHealthVersionTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type RootHealthVersionTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``GET / returns tip text``() : Task =
        task {
            let! status, body = get client "/"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("metadata", body)
        }

    [<Fact>]
    member _.``GET /health returns pass``() : Task =
        task {
            let! status, body = get client "/health"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("\"status\":\"pass\"", body)
        }

    [<Fact>]
    member _.``GET /$version returns product version``() : Task =
        task {
            let! status, body = get client "/$version"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("FhirServer", body)
            Assert.Contains("version", body)
        }
