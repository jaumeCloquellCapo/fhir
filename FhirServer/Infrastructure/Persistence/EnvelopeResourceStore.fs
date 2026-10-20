namespace FhirServer.Infrastructure.Persistence

open System
open System.Threading
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Services
open FhirServer.Domain.Types
open Npgsql

/// Single envelope store implementation; SQL dialect supplies persistence.
type EnvelopeResourceStore(dialect: IEnvelopeDialect) =
    let gate = obj ()
    /// Ambient session for RunInTransaction / nested ops (Bundle.type=transaction).
    let ambientSession = AsyncLocal<IEnvelopeSession>()

    let tryAmbient () =
        let s = ambientSession.Value
        if isNull (box s) then None else Some s

    /// Every store op runs in a transaction so SET LOCAL app.tenant_id (RLS) applies.
    /// When ambient session is set, reuses it (no nested commit).
    let withSession action =
        match tryAmbient () with
        | Some session -> action session
        | None ->
            use session = dialect.OpenSession()
            session.BeginTransaction()

            try
                let result = action session
                session.Commit()
                result
            with _ ->
                session.Rollback()
                reraise ()

    let indexResource (session: IEnvelopeSession) (resourceType: string) (resource: FhirResource) =
        let id = FhirJson.id resource
        session.ReplaceSearchIndex(resourceType, id, SearchIndexer.extract resourceType resource)

    let backfillSearchIndexes () =
        withSession (fun session ->
            if not (session.IsSearchIndexBackfilled()) then
                for resourceType in ResourceCatalog.supportedResourceTypes do
                    let mutable after: string option = None
                    let mutable cont = true

                    while cont do
                        let page = session.ListCurrentPage(resourceType, after, 500)

                        match page with
                        | [] -> cont <- false
                        | rows ->
                            for id, txid, ts, json in rows do
                                let resource = EnvelopeCodec.hydrate resourceType id txid ts json
                                indexResource session resourceType resource

                            after <- rows |> List.tryLast |> Option.map (fun (id, _, _, _) -> id)

                session.MarkSearchIndexBackfilled())

    let denyMutationIfNeeded (old: EnvelopeRow) =
        match TenantContext.tryOrganizationId () with
        | None -> ()
        | Some orgId ->
            match old.TenantMode with
            | TenantMode.SystemShared ->
                raise (TenantMutationForbiddenException "system-shared resources are read-only on organization-scoped APIs")
            | TenantMode.Shared when not (String.Equals(old.TenantId, orgId, StringComparison.Ordinal)) ->
                raise (
                    TenantMutationForbiddenException
                        "shared resources can only be updated or deleted from their owning organization API"
                )
            | _ -> ()

    let syncOrganizationClosure (session: IEnvelopeSession) (resourceType: string) (resource: FhirResource) =
        if resourceType.Equals("Organization", StringComparison.Ordinal) then
            let orgId = FhirJson.id resource
            let parent = OrganizationHierarchy.tryPartOfOrgId resource
            session.UpsertOrganizationClosure(orgId, parent)

    let removeOrganizationClosure (session: IEnvelopeSession) (resourceType: string) (id: string) =
        if resourceType.Equals("Organization", StringComparison.Ordinal) then
            session.DeleteOrganizationClosure id

    let insertCreated (session: IEnvelopeSession) (resource: FhirResource) =
        let resourceType = FhirJson.resourceType resource
        let id = FhirJson.id resource
        let now = DateTimeOffset.UtcNow
        let txid = session.NextTxid()

        let mode, tenantId =
            match OrganizationHierarchy.resolveCreateStamp resource None with
            | Error msg -> raise (TenantStampException msg)
            | Ok pair -> pair

        let bodyJson = EnvelopeCodec.toBodyJson resource

        try
            session.InsertCurrent(
                resourceType,
                id,
                {
                    Txid = txid
                    Cts = now
                    Ts = now
                    Status = "created"
                    BodyJson = bodyJson
                    TenantId = tenantId
                    TenantMode = mode
                }
            )
        with :? PostgresException as ex when ex.SqlState = "23505" ->
            raise (DuplicateResourceException(resourceType, id))

        let stamped = EnvelopeCodec.stamp resource txid now
        syncOrganizationClosure session resourceType stamped
        indexResource session resourceType stamped
        stamped

    let upsertCore (session: IEnvelopeSession) (resource: FhirResource) =
        let resourceType = FhirJson.resourceType resource
        let id = FhirJson.id resource
        let now = DateTimeOffset.UtcNow
        let txid = session.NextTxid()

        match session.TryReadCurrent(resourceType, id) with
        | Some old ->
            denyMutationIfNeeded old
            session.InsertHistory(resourceType, id, old)
            let bodyJson = EnvelopeCodec.toBodyJson resource
            session.UpdateCurrent(resourceType, id, txid, now, bodyJson)
            let stamped = EnvelopeCodec.stamp resource txid now
            syncOrganizationClosure session resourceType stamped
            indexResource session resourceType stamped
            stamped
        | None -> insertCreated session resource

    do
        dialect.Migrate()
        // One-time startup backfill only; do not serialize all writes on this gate.
        lock gate backfillSearchIndexes

    interface IResourceStore with
        member _.Get(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                match session.TryReadCurrent(resourceType, id) with
                | Some row -> Some(EnvelopeCodec.hydrate resourceType id row.Txid row.Ts row.BodyJson)
                | None -> None)

        member _.GetMany(resourceType, ids) =
            ResourceCatalog.ensureSupported resourceType

            if List.isEmpty ids then
                []
            else
                withSession (fun session ->
                    session.ReadCurrentMany(resourceType, ids)
                    |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

        member _.GetVersion(resourceType, id, vid) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.TryGetVersion(resourceType, id, vid)
                |> Option.map (fun (txid, ts, status, json) ->
                    EnvelopeCodec.toHistoryEntry resourceType id txid ts status json))

        member _.WasDeleted(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType
            withSession (fun session -> session.WasDeleted(resourceType, id))

        member _.Create(resource) =
            let resourceType = FhirJson.resourceType resource
            ResourceCatalog.ensureSupported resourceType
            withSession (fun session -> insertCreated session resource)

        member _.Upsert(resource) =
            let resourceType = FhirJson.resourceType resource
            ResourceCatalog.ensureSupported resourceType
            withSession (fun session -> upsertCore session resource)

        member _.Delete(resourceType, id) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                match session.TryReadCurrent(resourceType, id) with
                | None -> None
                | Some old ->
                    denyMutationIfNeeded old
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
                            TenantId = old.TenantId
                            TenantMode = old.TenantMode
                        }
                    )

                    session.DeleteCurrent(resourceType, id)
                    session.ClearSearchIndex(resourceType, id)
                    removeOrganizationClosure session resourceType id
                    Some(EnvelopeCodec.hydrate resourceType id txid now old.BodyJson))

        member _.ListByType(resourceType) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.ListCurrent resourceType
                |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

        member _.ListByTypePage(resourceType, afterId, limit) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.ListCurrentPage(resourceType, afterId, limit)
                |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

        member _.ListByTypeWhere(resourceType, filters, limit) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.ListCurrentByJsonbContainment(resourceType, filters, limit)
                |> List.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

        member _.TryFindByTypeWhere(resourceType, filters) =
            ResourceCatalog.ensureSupported resourceType

            withSession (fun session ->
                session.ListCurrentByJsonbContainment(resourceType, filters, 1)
                |> List.tryHead
                |> Option.map (fun (id, txid, ts, json) -> EnvelopeCodec.hydrate resourceType id txid ts json))

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
                let rows, total = session.QueryHistory filter

                let page =
                    rows
                    |> List.map (fun (rt, id, txid, ts, status, json) ->
                        EnvelopeCodec.toHistoryEntry rt id txid ts status json)

                page, total)

        member _.RunInTransaction(action) =
            match tryAmbient () with
            | Some _ -> action ()
            | None ->
                withSession (fun session ->
                    ambientSession.Value <- session

                    try
                        action ()
                    finally
                        ambientSession.Value <- Unchecked.defaultof<_>)
