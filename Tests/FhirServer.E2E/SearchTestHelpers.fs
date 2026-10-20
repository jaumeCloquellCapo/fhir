namespace FhirServer.E2E

open System
open System.Net
open System.Net.Http
open System.Text.Json
open Xunit
open FhirServer.E2E.HttpAssert

/// Shared helpers for search E2E suites (create fixtures, assert bundle ids).
module SearchTestHelpers =

    let mustCreate (client: HttpClient) (path: string) (json: string) =
        task {
            let! status, body, _ = postJson client path json

            if status <> HttpStatusCode.Created then
                failwith (sprintf "%s create failed HTTP %d: %s" path (int status) body)

            let id = propString body "id" |> Option.defaultValue ""

            if String.IsNullOrWhiteSpace id then
                failwith (sprintf "%s create returned no id: %s" path body)

            return id, body
        }

    let bundleEntryIds (body: string) =
        use doc = JsonDocument.Parse(body)
        let mutable entry = Unchecked.defaultof<JsonElement>

        if not (doc.RootElement.TryGetProperty("entry", &entry)) then
            []
        else
            entry.EnumerateArray()
            |> Seq.choose (fun e ->
                let mutable res = Unchecked.defaultof<JsonElement>
                let mutable idEl = Unchecked.defaultof<JsonElement>

                if e.TryGetProperty("resource", &res) && res.TryGetProperty("id", &idEl) then
                    Some(idEl.GetString())
                else
                    None)
            |> Seq.toList

    let assertContainsId (body: string) (id: string) =
        Assert.True(
            bundleEntryIds body |> List.contains id,
            sprintf "Expected bundle to contain id %s. Body=%s" id (body.Substring(0, min 500 body.Length))
        )

    let assertNotContainsId (body: string) (id: string) =
        Assert.False(
            bundleEntryIds body |> List.contains id,
            sprintf "Expected bundle NOT to contain id %s. Body=%s" id (body.Substring(0, min 500 body.Length))
        )

    let enc (s: string) = Uri.EscapeDataString s
