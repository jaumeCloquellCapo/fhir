namespace FhirServer.Infrastructure.Persistence

open System
open System.Globalization
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

/// Shared envelope JSON codec (body omits id / meta / resourceType).
module EnvelopeCodec =

    let parseTs (ts: string) =
        DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

    let toBodyJson (resource: FhirResource) =
        let node = FhirJson.deepClone resource
        node.Remove("id") |> ignore
        node.Remove("resourceType") |> ignore

        // Columns own versionId/lastUpdated; keep profile/tag/security in body (BALP, etc.).
        match node["meta"] with
        | :? JsonObject as meta ->
            meta.Remove("versionId") |> ignore
            meta.Remove("lastUpdated") |> ignore

            if meta.Count = 0 then
                node.Remove("meta") |> ignore
        | _ -> node.Remove("meta") |> ignore

        node.ToJsonString()

    let hydrate (resourceType: string) (id: string) (txid: int64) (ts: DateTimeOffset) (json: string) : FhirResource =
        match JsonNode.Parse(json) with
        | :? JsonObject as node ->
            node["resourceType"] <- resourceType
            node["id"] <- id
            FhirJson.stampMeta node txid ts
        | _ ->
            invalidArg (nameof json) $"Envelope body for {resourceType}/{id} must be a JSON object."

    let hydrateTs (resourceType: string) (id: string) (txid: int64) (ts: string) (json: string) =
        hydrate resourceType id txid (parseTs ts) json

    let stamp (resource: FhirResource) (txid: int64) (ts: DateTimeOffset) = FhirJson.stampMeta resource txid ts

    let stampTs (resource: FhirResource) (txid: int64) (ts: string) = stamp resource txid (parseTs ts)

    let toHistoryEntry resourceType id txid (ts: DateTimeOffset) status json = {
        ResourceType = resourceType
        Id = id
        Txid = txid
        Timestamp = ts
        Status = ResourceCatalog.parseStatus status
        Resource = Some(hydrate resourceType id txid ts json)
    }

    let toHistoryEntryTs resourceType id txid (ts: string) status json =
        toHistoryEntry resourceType id txid (parseTs ts) status json
