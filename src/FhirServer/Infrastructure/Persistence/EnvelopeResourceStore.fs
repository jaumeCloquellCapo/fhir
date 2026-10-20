namespace FhirServer.Infrastructure.Persistence

open System
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Domain.Types

/// Single envelope store implementation; SQL dialect supplies persistence.
type EnvelopeResourceStore(dialect: IEnvelopeDialect) =
    let gate = obj ()

    let withSession action =
        use session = dialect.OpenSession()
        action session

    let indexResource (session: IEnvelopeSession) (resourceType: string) (resource: FhirResource) =
        let id = FhirJson.id resource
        session.ReplaceSearchIndex(resourceType, id, SearchIndexer.extract resourceType resource)

    let backfillSearchIndexes () =
        withSession (fun session ->
            if not (session.IsSearchIndexBackfilled()) then
                session.BeginTransaction()

                try
                    for resourceType in ResourceCatalog.supportedResourceTypes do
                        for id, txid, ts, json in session.ListCurrent resourceType do
                            let resource = EnvelopeCodec.hydrate resourceType id txid ts json
                            indexResource session resourceType resource

                    session.MarkSearchIndexBackfilled()
                    session.Commit()
                with _ ->
                    session.Rollback()
                    reraise ())

    do
        dialect.Migrate()
        lock gate backfillSearchIndexes

    interface IResourceStore with
        member _.Get(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                match session.TryReadCurrent(resourceType, id) with
                | Some row -> Some(EnvelopeCodec.hydrate resourceType id row.Txid row.Ts row.BodyJson)
                | None -> None)

        member _.GetVersion(resourceType, id, vid) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.TryGetVersion(resourceType, id, vid)
                |> Option.map (fun (txid, ts, status, json) ->
                    EnvelopeCodec.toHistoryEntry resourceType id txid ts status json))

        member _.WasDeleted(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType
            withSession (fun session -> session.WasDeleted(resourceType, id))

        member _.Upsert(resource) =
            let resourceType = FhirJson.resourceType resource
            ResourceCatalog.ensureSupported resourceType

            lock gate (fun () ->
                withSession (fun session ->
                    session.BeginTransaction()

                    try
                        let id = FhirJson.id resource
                        let bodyJson = EnvelopeCodec.toBodyJson resource
                        let now = DateTimeOffset.UtcNow
                        let txid = session.NextTxid()

                        let stamped =
                            match session.TryReadCurrent(resourceType, id) with
                            | Some old ->
                                session.InsertHistory(resourceType, id, old)
                                session.UpdateCurrent(resourceType, id, txid, now, bodyJson)
                                EnvelopeCodec.stamp resource txid now
                            | None ->
                                session.InsertCurrent(
                                    resourceType,
                                    id,
                                    {
                                        Txid = txid
                                        Cts = now
                                        Ts = now
                                        Status = "created"
                                        BodyJson = bodyJson
                                    }
                                )

                                EnvelopeCodec.stamp resource txid now

                        indexResource session resourceType stamped
                        session.Commit()
                        stamped
                    with _ ->
                        session.Rollback()
                        reraise ()))

        member _.Delete(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType

            lock gate (fun () ->
                withSession (fun session ->
                    session.BeginTransaction()

                    try
                        match session.TryReadCurrent(resourceType, id) with
                        | None ->
                            session.Rollback()
                            None
                        | Some old ->
                            session.InsertHistory(resourceType, id, old)
                            let txid = session.NextTxid()
                            let now = DateTimeOffset.UtcNow

                            session.InsertHistory(
                                resourceType,
                                id,
                                {
                                    Txid = txid
                                    Cts = now
                                    Ts = now
                                    Status = "deleted"
                                    BodyJson = old.BodyJson
                                }
                            )

                            session.DeleteCurrent(resourceType, id)
                            session.ClearSearchIndex(resourceType, id)
                            session.Commit()
                            Some(EnvelopeCodec.hydrate resourceType id txid now old.BodyJson)
                    with _ ->
                        session.Rollback()
                        reraise ()))

        member _.ListByType(resourceType) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.ListCurrent resourceType
                |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

        member _.Search(resourceType, query) =
            ResourceCatalog.ensureSupported resourceType
            let plan = SearchEngine.toSearchPlan resourceType query

            withSession (fun session ->
                let rows, total = session.SearchCurrent(resourceType, plan)

                let page =
                    rows
                    |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json)

                page, total)

        member _.ListHistory(filter) =
            match filter.ResourceType with
            | Some rt -> ResourceCatalog.ensureSupported rt
            | None -> ()

            withSession (fun session ->
                let rows =
                    session.QueryHistory filter
                    |> List.map (fun (rt, id, txid, ts, status, json) ->
                        EnvelopeCodec.toHistoryEntry rt id txid ts status json)
                    |> List.distinctBy (fun e -> e.ResourceType, e.Id, e.Txid)
                    |> List.sortByDescending (fun e -> e.Txid)

                let total = rows.Length
                let page = rows |> List.skip filter.Offset |> List.truncate filter.Count
                page, total)

        member _.RunInTransaction(action) = lock gate action
