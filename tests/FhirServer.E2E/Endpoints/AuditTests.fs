module FhirServer.E2E.Endpoints.AuditTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type AuditEnabledFactory() =
    inherit FhirApiFactory()

    override this.ConfigureWebHost(builder) =
        base.ConfigureWebHost(builder)
        builder.UseSetting("Audit:Enabled", "true") |> ignore

let private entryCount (searchBody: string) =
    use doc = JsonDocument.Parse(searchBody)
    let mutable total = Unchecked.defaultof<JsonElement>

    if doc.RootElement.TryGetProperty("total", &total) then
        total.GetInt32()
    else
        let mutable entry = Unchecked.defaultof<JsonElement>

        if doc.RootElement.TryGetProperty("entry", &entry) then
            entry.GetArrayLength()
        else
            0

let private firstAuditEvent (searchBody: string) =
    use doc = JsonDocument.Parse(searchBody)
    let mutable entry = Unchecked.defaultof<JsonElement>

    if not (doc.RootElement.TryGetProperty("entry", &entry)) || entry.GetArrayLength() = 0 then
        None
    else
        let mutable resource = Unchecked.defaultof<JsonElement>

        if entry.[0].TryGetProperty("resource", &resource) then
            Some(resource.GetRawText())
        else
            None

type AuditTests() =
    let enabledFactory = new AuditEnabledFactory()
    let enabledClient = enabledFactory.CreateClient()
    let disabledFactory = new FhirApiFactory()
    let disabledClient = disabledFactory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            enabledClient.Dispose()
            enabledFactory.Dispose()
            disabledClient.Dispose()
            disabledFactory.Dispose()

    [<Fact>]
    member _.``create Patient writes BALP AuditEvent when enabled``() : Task =
        task {
            let! status, _, _ =
                postJson
                    enabledClient
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"AuditMe","given":["Ana"]}],"gender":"female"}"""

            assertStatus HttpStatusCode.Created status

            let! searchStatus, searchBody = get enabledClient "/AuditEvent"
            assertStatus HttpStatusCode.OK searchStatus
            Assert.True(entryCount searchBody >= 1, $"expected AuditEvent entries, got: {searchBody}")

            Assert.Contains("IHE.BasicAudit.PatientCreate", searchBody)
            Assert.Contains("\"code\":\"create\"", searchBody)
            Assert.Contains("\"action\":\"C\"", searchBody)
        }

    [<Fact>]
    member _.``AuditEvent operations are not re-audited``() : Task =
        task {
            let! _, _, _ =
                postJson
                    enabledClient
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoLoop","given":["Ana"]}],"gender":"female"}"""

            let! _, beforeBody = get enabledClient "/AuditEvent"
            let before = entryCount beforeBody

            let! _, afterSearchBody = get enabledClient "/AuditEvent"
            let afterSearch = entryCount afterSearchBody
            Assert.Equal(before, afterSearch)

            match firstAuditEvent afterSearchBody with
            | None -> Assert.Fail("expected at least one AuditEvent")
            | Some ae ->
                let id = propString ae "id" |> Option.defaultValue ""
                let! _ = get enabledClient $"/AuditEvent/{id}"
                let! _, afterReadBody = get enabledClient "/AuditEvent"
                Assert.Equal(before, entryCount afterReadBody)
        }

    [<Fact>]
    member _.``create does not write AuditEvent when disabled``() : Task =
        task {
            let! status, _, _ =
                postJson
                    disabledClient
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoAudit","given":["Ana"]}],"gender":"female"}"""

            assertStatus HttpStatusCode.Created status

            let! searchStatus, searchBody = get disabledClient "/AuditEvent"
            assertStatus HttpStatusCode.OK searchStatus
            Assert.Equal(0, entryCount searchBody)
        }
