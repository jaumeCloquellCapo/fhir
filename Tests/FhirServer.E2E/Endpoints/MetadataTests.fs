module FhirServer.E2E.Endpoints.MetadataTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type MetadataTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``GET /metadata returns CapabilityStatement``() : Task =
        task {
            let! status, body = get client "/metadata"
            assertStatus HttpStatusCode.OK status
            assertResourceType "CapabilityStatement" body
            Assert.Contains("Patient", body)
            Assert.Contains("validate", body)
        }
