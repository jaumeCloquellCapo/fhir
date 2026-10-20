module FhirServer.E2E.Endpoints.JsonbWhereTests

open System
open System.Diagnostics
open System.Net
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

/// Equivalence + optional microbench for Aidbox-style `resource @>` lookups.
type JsonbWhereTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    let store () =
        factory.Services.GetRequiredService<IResourceStore>()

    let benchN () =
        match Environment.GetEnvironmentVariable("FHIR_JSONB_BENCH_N") with
        | null
        | "" -> 250
        | raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> min n 20_000
            | _ -> 250

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``TryFindByTypeWhere finds CodeSystem by url among distractors``() : Task =
        task {
            let targetUrl = $"http://example.org/cs/where-{Guid.NewGuid():N}"

            for i in 1..20 do
                let url = $"http://example.org/cs/noise-{i}-{Guid.NewGuid():N}"

                let! status, body, _ =
                    postJson
                        client
                        "/CodeSystem"
                        $"""{{"resourceType":"CodeSystem","status":"active","content":"complete","url":"{url}","name":"Noise{i}","concept":[{{"code":"x"}}]}}"""

                if status <> HttpStatusCode.Created then
                    failwith $"noise create failed {int status}: {body}"

            let! status, body, _ =
                postJson
                    client
                    "/CodeSystem"
                    $"""{{"resourceType":"CodeSystem","status":"active","content":"complete","url":"{targetUrl}","name":"Target","concept":[{{"code":"red","display":"Red"}}]}}"""

            assertStatus HttpStatusCode.Created status

            let found = store().TryFindByTypeWhere("CodeSystem", [ "url", targetUrl ])
            Assert.True(found.IsSome, "expected TryFindByTypeWhere to locate target url")
            Assert.Equal(Some targetUrl, found |> Option.bind (fun cs -> FhirJson.propString cs "url"))

            let! lookupStatus, lookupBody =
                get client $"/CodeSystem/$lookup?system={Uri.EscapeDataString targetUrl}&code=red"

            assertStatus HttpStatusCode.OK lookupStatus
            Assert.Contains("Red", lookupBody)
        }

    [<Fact>]
    member _.``ListByTypeWhere filters Subscription by status without listing inactive``() : Task =
        task {
            let sink = Guid.NewGuid().ToString("N")

            let! activeStatus, activeBody, _ =
                postJson
                    client
                    "/Subscription"
                    $"""{{"resourceType":"Subscription","status":"active","topic":"http://example.org/Patient","channelType":{{"code":"rest-hook"}},"endpoint":"http://localhost/_hooks/{sink}","content":"id-only"}}"""

            assertStatus HttpStatusCode.Created activeStatus

            let! offStatus, _, _ =
                postJson
                    client
                    "/Subscription"
                    $"""{{"resourceType":"Subscription","status":"off","topic":"http://example.org/Patient","channelType":{{"code":"rest-hook"}},"endpoint":"http://localhost/_hooks/{sink}-off","content":"id-only"}}"""

            assertStatus HttpStatusCode.Created offStatus

            let active = store().ListByTypeWhere("Subscription", [ "status", "active" ], 100)
            Assert.True(List.exists (fun s -> FhirJson.propString s "status" = Some "active") active)
            Assert.False(List.exists (fun s -> FhirJson.propString s "status" = Some "off") active)
            Assert.Contains("Subscription", activeBody)
        }

    [<Fact>]
    member _.``ListByTypeWhere microbench vs ListByType filter (optional N via FHIR_JSONB_BENCH_N)``() : Task =
        task {
            let n = benchN ()
            let prefix = Guid.NewGuid().ToString("N")
            let targetUrl = $"http://example.org/bench/{prefix}/target"

            for i in 1 .. n - 1 do
                let url = $"http://example.org/bench/{prefix}/noise-{i}"

                let cs =
                    FhirJson.mkObject [
                        "resourceType", FhirJson.jStr "CodeSystem"
                        "id", FhirJson.jStr $"bench-{prefix}-{i}"
                        "status", FhirJson.jStr "active"
                        "content", FhirJson.jStr "complete"
                        "url", FhirJson.jStr url
                        "name", FhirJson.jStr $"Noise{i}"
                    ]

                store().Upsert cs |> ignore

            let target =
                FhirJson.mkObject [
                    "resourceType", FhirJson.jStr "CodeSystem"
                    "id", FhirJson.jStr $"bench-{prefix}-target"
                    "status", FhirJson.jStr "active"
                    "content", FhirJson.jStr "complete"
                    "url", FhirJson.jStr targetUrl
                    "name", FhirJson.jStr "Target"
                ]

            store().Upsert target |> ignore

            let swWhere = Stopwatch.StartNew()
            let viaWhere = store().TryFindByTypeWhere("CodeSystem", [ "url", targetUrl ])
            swWhere.Stop()

            let swList = Stopwatch.StartNew()

            let viaList =
                store().ListByType "CodeSystem"
                |> List.tryFind (fun cs -> FhirJson.propString cs "url" = Some targetUrl)

            swList.Stop()

            Assert.True(viaWhere.IsSome)
            Assert.True(viaList.IsSome)
            Assert.Equal(FhirJson.id viaWhere.Value, FhirJson.id viaList.Value)

            Assert.True(
                swWhere.ElapsedMilliseconds >= 0L,
                $"where={swWhere.ElapsedMilliseconds}ms list={swList.ElapsedMilliseconds}ms n={n}"
            )
        }
