namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// FHIR history (_history).
module ResourceHistory =

    let history (store: IResourceStore) (filter: HistoryFilter) : FhirResult<FhirResource> =
        match filter.ResourceType with
        | Some rt ->
            ResourceHelpers.ensureKnownResourceType rt
            |> Result.bind (fun () ->
                let entries, total = store.ListHistory filter
                Ok(ResourceHelpers.toHistoryBundle entries total))
        | None ->
            let entries, total = store.ListHistory filter
            Ok(ResourceHelpers.toHistoryBundle entries total)

    let historyFromQuery
        (store: IResourceStore)
        (resourceType: string option)
        (id: string option)
        (query: Map<string, string list>)
        =
        let count, offset = ResourceHelpers.parseCountOffset query

        history store {
            ResourceType = resourceType
            Id = id
            Since = ResourceHelpers.parseSince query
            Count = count
            Offset = offset
        }
