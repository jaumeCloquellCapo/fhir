namespace FhirServer.E2E

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit

module HttpAssert =

    let fhirJson = "application/fhir+json"

    /// Matches FhirApiFactory Auth:Username / Auth:Password.
    let e2eUsername = "fhir"
    let e2ePassword = "secret"

    let e2eBasicHeader =
        let token =
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{e2eUsername}:{e2ePassword}"))

        $"Basic {token}"

    let private hasAuthorization (headers: (string * string) list) =
        headers
        |> List.exists (fun (name, _) -> name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))

    let private withE2EAuth (headers: (string * string) list) =
        if hasAuthorization headers then
            headers
        else
            ("Authorization", e2eBasicHeader) :: headers

    /// Attach E2E Basic auth when the request has no Authorization header yet.
    let applyE2EAuth (request: HttpRequestMessage) =
        if isNull (box request.Headers.Authorization) && not (request.Headers.Contains "Authorization") then
            request.Headers.TryAddWithoutValidation("Authorization", e2eBasicHeader) |> ignore

    let sendWith
        (injectAuth: bool)
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

            let headers =
                if injectAuth then
                    withE2EAuth headers
                else
                    headers

            for name, value in headers do
                request.Headers.TryAddWithoutValidation(name, value) |> ignore

            use! response = client.SendAsync(request)
            let! text = response.Content.ReadAsStringAsync()
            return response.StatusCode, text, response
        }

    /// Authenticated request (default E2E Basic unless Authorization is already set).
    let send client method path body headers =
        sendWith true client method path body headers

    /// No Authorization header — for asserting 401 when Auth is on.
    let sendAnonymous client method path body =
        sendWith false client method path body []

    let get (client: HttpClient) (path: string) =
        task {
            let! status, body, _ = send client HttpMethod.Get path None []
            return status, body
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
