namespace FhirServer.Application

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Application.Subscriptions

/// FHIR search + Observation $lastn.
module ResourceSearch =

    let search
        (store: IResourceStore)
        (resourceType: string)
        (query: Map<string, string list>)
        : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType resourceType
        |> Result.bind (fun () ->
            let includeTokens = query |> Map.tryFind "_include" |> Option.defaultValue []
            let revIncludeTokens = query |> Map.tryFind "_revinclude" |> Option.defaultValue []

            let rec searchList (depth: int) (rt: string, q: Map<string, string list>) =
                if depth > 5 || not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let nested = searchList (depth + 1)

                    let expanded =
                        q
                        |> Map.add "_count" [ string ResourceHelpers.internalFetchCap ]
                        |> SearchEngine.expandChainedQuery nested
                        |> SearchEngine.expandHasQuery nested

                    store.Search(rt, expanded) |> fst

            let expandSearch = searchList 0

            let baseQuery =
                query
                |> Map.remove "_include"
                |> Map.remove "_revinclude"

            match SearchFilter.expand baseQuery with
            | SearchFilter.Expansion.Invalid reason -> Error(BadRequest reason)
            | expansion ->
                let runSearch (q: Map<string, string list>) =
                    let filterQuery =
                        q
                        |> SearchEngine.expandChainedQuery expandSearch
                        |> SearchEngine.expandHasQuery expandSearch

                    store.Search(resourceType, filterQuery)

                let page, total =
                    match expansion with
                    | SearchFilter.Expansion.Unchanged q
                    | SearchFilter.Expansion.Single q -> runSearch q
                    | SearchFilter.Expansion.Alternatives alts ->
                        let ids =
                            alts
                            |> List.collect (fun alt ->
                                let q = alt |> Map.add "_count" [ string ResourceHelpers.internalFetchCap ]
                                runSearch q |> fst |> List.map FhirJson.id)
                            |> List.distinct

                        let idQuery =
                            if List.isEmpty ids then
                                baseQuery
                                |> Map.remove "_filter"
                                |> Map.add "_id" [ "__filter_nomatch__" ]
                            else
                                baseQuery
                                |> Map.remove "_filter"
                                |> Map.add "_id" ids

                        runSearch idQuery
                    | SearchFilter.Expansion.Invalid _ -> [], 0

                let getMany rt ids = store.GetMany(rt, ids)

                let includes =
                    SearchEngine.resolveIncludes getMany resourceType includeTokens page

                let revincludes =
                    SearchEngine.resolveRevincludes expandSearch resourceType revIncludeTokens page

                Ok(ResourceHelpers.toSearchBundle resourceType page (includes @ revincludes) total))

    let observationLastN (store: IResourceStore) (query: Map<string, string list>) : FhirResult<FhirResource> =
        ResourceHelpers.ensureKnownResourceType "Observation"
        |> Result.bind (fun () ->
            let maxN =
                QueryParams.first query "max"
                |> Option.bind (fun s ->
                    match Int32.TryParse s with
                    | true, n when n > 0 -> Some(min n 100)
                    | _ -> None)
                |> Option.defaultValue 1

            let rec chainSearch (depth: int) (rt: string, q: Map<string, string list>) =
                if depth > 5 || not (ResourceCatalog.isSupported rt) then
                    []
                else
                    let nested = chainSearch (depth + 1)

                    let expanded =
                        q
                        |> Map.add "_count" [ string ResourceHelpers.internalFetchCap ]
                        |> SearchEngine.expandChainedQuery nested
                        |> SearchEngine.expandHasQuery nested

                    store.Search(rt, expanded) |> fst

            let expandSearch = chainSearch 0

            let filterQuery =
                query
                |> Map.remove "max"
                |> Map.remove "_count"
                |> Map.remove "_offset"
                |> SearchEngine.expandChainedQuery expandSearch
                |> SearchEngine.expandHasQuery expandSearch

            // Fetch a bounded window then group in memory (MVP $lastn).
            let fetchCap = min ResourceHelpers.internalFetchCap (maxN * 50 |> max 50)

            let all =
                store.Search("Observation", filterQuery |> Map.add "_count" [ string fetchCap ])
                |> fst
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)

            let codeKey (resource: FhirResource) =
                match resource["code"] with
                | :? JsonObject as code ->
                    match code["coding"] with
                    | :? JsonArray as arr when arr.Count > 0 ->
                        match arr.[0] with
                        | :? JsonObject as c ->
                            let system = FhirJson.propString c "system" |> Option.defaultValue ""
                            let code = FhirJson.propString c "code" |> Option.defaultValue ""
                            $"{system}|{code}"
                        | _ -> FhirJson.id resource
                    | _ -> FhirJson.id resource
                | _ -> FhirJson.id resource

            let selected =
                all
                |> List.groupBy codeKey
                |> List.collect (fun (_, group) -> group |> List.truncate maxN)
                |> List.sortByDescending (fun r ->
                    FhirJson.lastUpdated r |> Option.defaultValue DateTimeOffset.MinValue)

            Ok(ResourceHelpers.toSearchBundle "Observation" selected [] selected.Length))
