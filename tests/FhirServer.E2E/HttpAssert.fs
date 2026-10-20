namespace FhirServer.E2E

open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit

module HttpAssert =

    let fhirJson = "application/fhir+json"

    let get (client: HttpClient) (path: string) =
        task {
            use! response = client.GetAsync(path)
            let! body = response.Content.ReadAsStringAsync()
            return response.StatusCode, body
        }

    let send
        (client: HttpClient)
        (method: HttpMethod)
        (path: string)
        (body: string option)
        (headers: (string * string) list)
        =
        task {
            use request = new HttpRequestMessage(method, path)

            match body with
            | Some json -> request.Content <- new StringContent(json, Encoding.UTF8, fhirJson)
            | None -> ()

            for name, value in headers do
                request.Headers.TryAddWithoutValidation(name, value) |> ignore

            use! response = client.SendAsync(request)
            let! text = response.Content.ReadAsStringAsync()
            return response.StatusCode, text, response
        }

    let postJson client path json =
        send client HttpMethod.Post path (Some json) []

    let putJson client path json headers =
        send client HttpMethod.Put path (Some json) headers

    let patchJson client path json =
        send client (HttpMethod "PATCH") path (Some json) []

    let delete client path headers =
        send client HttpMethod.Delete path None headers

    let assertStatus (expected: HttpStatusCode) (actual: HttpStatusCode) =
        Assert.True(
            (actual = expected),
            sprintf "Expected HTTP %d (%A) but got %d (%A)" (int expected) expected (int actual) actual
        )

    let propString (json: string) (name: string) =
        use doc = JsonDocument.Parse(json)
        let mutable el = Unchecked.defaultof<JsonElement>

        if doc.RootElement.TryGetProperty(name, &el) then
            Some(el.GetString())
        else
            None

    let assertResourceType expected json =
        Assert.Equal(expected, propString json "resourceType" |> Option.defaultValue "")

    let versionId (json: string) =
        use doc = JsonDocument.Parse(json)
        let mutable meta = Unchecked.defaultof<JsonElement>
        let mutable vid = Unchecked.defaultof<JsonElement>

        if
            doc.RootElement.TryGetProperty("meta", &meta)
            && meta.TryGetProperty("versionId", &vid)
        then
            vid.GetString()
        else
            ""

    let etagOf (response: HttpResponseMessage) (body: string) =
        match response.Headers.TryGetValues("ETag") with
        | true, values -> values |> Seq.head |> fun e -> e.Trim().Trim('"')
        | _ -> versionId body
