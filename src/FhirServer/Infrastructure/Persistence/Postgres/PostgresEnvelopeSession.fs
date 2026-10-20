namespace FhirServer.Infrastructure.Persistence.Postgres

open System
open Npgsql
open FhirServer.Domain
open FhirServer.Domain.Services
open FhirServer.Domain.Types
open FhirServer.Infrastructure.Persistence

type PostgresEnvelopeSession(connectionString: string) =
    let conn = new NpgsqlConnection(connectionString)
    do conn.Open()
    let mutable tx: NpgsqlTransaction option = None

    let cmd () =
        let c = conn.CreateCommand()

        match tx with
        | Some t -> c.Transaction <- t
        | None -> ()

        c

    /// Bind ORGBAC tenant for this transaction (Postgres RLS reads app.tenant_id / app.orgbac_mode).
    /// SET LOCAL ROLE fhir_app so Docker superuser logins still obey RLS; then SET LOCAL config.
    let applyTenantLocal () =
        let tenantId = TenantContext.tryOrganizationId () |> Option.defaultValue ""

        let mode =
            if TenantContext.isHierarchical () then
                "hierarchy"
            else
                "exact"

        use c = cmd ()

        c.CommandText <-
            "SET LOCAL ROLE fhir_app; SELECT set_config('app.tenant_id', @tenant_id, true), set_config('app.orgbac_mode', @orgbac_mode, true);"

        c.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        c.Parameters.AddWithValue("orgbac_mode", mode) |> ignore
        c.ExecuteScalar() |> ignore

    interface IEnvelopeSession with
        member _.BeginTransaction() =
            match tx with
            | Some _ -> applyTenantLocal ()
            | None ->
                tx <- Some(conn.BeginTransaction())
                applyTenantLocal ()

        member _.Commit() =
            match tx with
            | Some t ->
                t.Commit()
                t.Dispose()
                tx <- None
            | None -> ()

        member _.Rollback() =
            match tx with
            | Some t ->
                t.Rollback()
                t.Dispose()
                tx <- None
            | None -> ()

        member _.NextTxid() =
            use c = cmd ()
            c.CommandText <- "UPDATE txid_counter SET value = value + 1 WHERE id = 1 RETURNING value;"
            c.ExecuteScalar() :?> int64

        member _.TryReadCurrent(resourceType, id) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"SELECT txid, cts, ts, status, resource::text, tenant_id, tenant_mode FROM {table} WHERE id = @id LIMIT 1;"

            c.Parameters.AddWithValue("id", id) |> ignore
            use reader = c.ExecuteReader()

            if reader.Read() then
                Some {
                    Txid = reader.GetInt64(0)
                    Cts = reader.GetFieldValue<DateTimeOffset>(1)
                    Ts = reader.GetFieldValue<DateTimeOffset>(2)
                    Status = reader.GetString(3)
                    BodyJson = reader.GetString(4)
                    TenantId = reader.GetString(5)
                    TenantMode = TenantMode.ofDb (reader.GetString(6))
                }
            else
                None

        member _.TryGetVersion(resourceType, id, vid) =
            let history =
                ResourceCatalog.quoteIdent (ResourceCatalog.historyTableName resourceType)

            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"SELECT txid, ts, status, resource::text FROM {history} WHERE id=@id AND txid=@txid
                  UNION ALL
                  SELECT txid, ts, status, resource::text FROM {table} WHERE id=@id AND txid=@txid
                  LIMIT 1;"

            c.Parameters.AddWithValue("id", id) |> ignore
            c.Parameters.AddWithValue("txid", vid) |> ignore
            use reader = c.ExecuteReader()

            if reader.Read() then
                Some(
                    reader.GetInt64(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetString(2),
                    reader.GetString(3)
                )
            else
                None

        member _.InsertHistory(resourceType, id, row) =
            let history =
                ResourceCatalog.quoteIdent (ResourceCatalog.historyTableName resourceType)

            use c = cmd ()

            c.CommandText <-
                $"INSERT INTO {history} (id, txid, cts, ts, resource_type, status, resource, tenant_id, tenant_mode)
                  VALUES (@id, @txid, @cts, @ts, @rtype, @status, @resource::jsonb, @tenant_id, @tenant_mode);"

            c.Parameters.AddWithValue("id", id) |> ignore
            c.Parameters.AddWithValue("txid", row.Txid) |> ignore
            c.Parameters.AddWithValue("cts", row.Cts) |> ignore
            c.Parameters.AddWithValue("ts", row.Ts) |> ignore
            c.Parameters.AddWithValue("rtype", resourceType) |> ignore
            c.Parameters.AddWithValue("status", row.Status) |> ignore
            c.Parameters.AddWithValue("resource", row.BodyJson) |> ignore
            c.Parameters.AddWithValue("tenant_id", row.TenantId) |> ignore
            c.Parameters.AddWithValue("tenant_mode", TenantMode.toDb row.TenantMode) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.UpdateCurrent(resourceType, id, txid, ts, bodyJson) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"UPDATE {table} SET txid=@txid, ts=@ts, status='updated', resource=@resource::jsonb WHERE id=@id;"

            c.Parameters.AddWithValue("txid", txid) |> ignore
            c.Parameters.AddWithValue("ts", ts) |> ignore
            c.Parameters.AddWithValue("resource", bodyJson) |> ignore
            c.Parameters.AddWithValue("id", id) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.InsertCurrent(resourceType, id, row) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"INSERT INTO {table} (id, txid, cts, ts, resource_type, status, resource, tenant_id, tenant_mode)
                  VALUES (@id, @txid, @cts, @ts, @rtype, 'created', @resource::jsonb, @tenant_id, @tenant_mode);"

            c.Parameters.AddWithValue("id", id) |> ignore
            c.Parameters.AddWithValue("txid", row.Txid) |> ignore
            c.Parameters.AddWithValue("cts", row.Cts) |> ignore
            c.Parameters.AddWithValue("ts", row.Ts) |> ignore
            c.Parameters.AddWithValue("rtype", resourceType) |> ignore
            c.Parameters.AddWithValue("resource", row.BodyJson) |> ignore
            c.Parameters.AddWithValue("tenant_id", row.TenantId) |> ignore
            c.Parameters.AddWithValue("tenant_mode", TenantMode.toDb row.TenantMode) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.DeleteCurrent(resourceType, id) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()
            c.CommandText <- $"DELETE FROM {table} WHERE id=@id;"
            c.Parameters.AddWithValue("id", id) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.WasDeleted(resourceType, id) =
            let history =
                ResourceCatalog.quoteIdent (ResourceCatalog.historyTableName resourceType)

            use c = cmd ()
            c.CommandText <- $"SELECT 1 FROM {history} WHERE id=@id AND status='deleted' LIMIT 1;"
            c.Parameters.AddWithValue("id", id) |> ignore
            not (isNull (c.ExecuteScalar()))

        member _.ListCurrent(resourceType) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()
            c.CommandText <- $"SELECT id, txid, ts, resource::text FROM {table} ORDER BY id;"
            use reader = c.ExecuteReader()

            [
                while reader.Read() do
                    yield
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.GetFieldValue<DateTimeOffset>(2),
                        reader.GetString(3)
            ]

        member _.QueryHistory(filter) =
            let types =
                match filter.ResourceType with
                | Some rt -> [ rt ]
                | None -> ResourceCatalog.supportedResourceTypes

            types
            |> List.collect (fun resourceType ->
                let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)

                let history =
                    ResourceCatalog.quoteIdent (ResourceCatalog.historyTableName resourceType)

                let read source =
                    use c = cmd ()
                    let idClause = if filter.Id.IsSome then " AND id=@id" else ""
                    let sinceClause = if filter.Since.IsSome then " AND ts >= @since" else ""

                    c.CommandText <-
                        $"SELECT id, txid, ts, status, resource::text FROM {source} WHERE 1=1{idClause}{sinceClause}"

                    match filter.Id with
                    | Some id -> c.Parameters.AddWithValue("id", id) |> ignore
                    | None -> ()

                    match filter.Since with
                    | Some since -> c.Parameters.AddWithValue("since", since) |> ignore
                    | None -> ()

                    use reader = c.ExecuteReader()

                    [
                        while reader.Read() do
                            yield
                                resourceType,
                                reader.GetString(0),
                                reader.GetInt64(1),
                                reader.GetFieldValue<DateTimeOffset>(2),
                                reader.GetString(3),
                                reader.GetString(4)
                    ]

                read history @ read table)

        member _.ClearSearchIndex(resourceType, id) =
            for table in [
                "search_string"
                "search_token"
                "search_ref"
                "search_date"
                "search_quantity"
            ] do
                use c = cmd ()
                c.CommandText <- $"DELETE FROM {table} WHERE resource_type = @rt AND id = @id;"
                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.ExecuteNonQuery() |> ignore

        member this.ReplaceSearchIndex(resourceType, id, rows: SearchIndexer.IndexRows) =
            (this :> IEnvelopeSession).ClearSearchIndex(resourceType, id)

            for row in rows.Strings do
                use c = cmd ()

                c.CommandText <-
                    "INSERT INTO search_string (resource_type, id, param, value) VALUES (@rt, @id, @param, @value);"

                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.Parameters.AddWithValue("param", row.Param) |> ignore
                c.Parameters.AddWithValue("value", row.Value) |> ignore
                c.ExecuteNonQuery() |> ignore

            for row in rows.Tokens do
                use c = cmd ()

                c.CommandText <-
                    "INSERT INTO search_token (resource_type, id, param, system, code) VALUES (@rt, @id, @param, @system, @code);"

                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.Parameters.AddWithValue("param", row.Param) |> ignore
                c.Parameters.AddWithValue("system", row.System) |> ignore
                c.Parameters.AddWithValue("code", row.Code) |> ignore
                c.ExecuteNonQuery() |> ignore

            for row in rows.Refs do
                use c = cmd ()

                c.CommandText <-
                    "INSERT INTO search_ref (resource_type, id, param, target) VALUES (@rt, @id, @param, @target);"

                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.Parameters.AddWithValue("param", row.Param) |> ignore
                c.Parameters.AddWithValue("target", row.Target) |> ignore
                c.ExecuteNonQuery() |> ignore

            for row in rows.Dates do
                use c = cmd ()

                c.CommandText <-
                    "INSERT INTO search_date (resource_type, id, param, start_ts, end_ts) VALUES (@rt, @id, @param, @start_ts, @end_ts);"

                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.Parameters.AddWithValue("param", row.Param) |> ignore
                c.Parameters.AddWithValue("start_ts", row.StartTs) |> ignore
                c.Parameters.AddWithValue("end_ts", row.EndTs) |> ignore
                c.ExecuteNonQuery() |> ignore

            for row in rows.Quantities do
                use c = cmd ()

                c.CommandText <-
                    "INSERT INTO search_quantity (resource_type, id, param, value, system, code) VALUES (@rt, @id, @param, @value, @system, @code);"

                c.Parameters.AddWithValue("rt", resourceType) |> ignore
                c.Parameters.AddWithValue("id", id) |> ignore
                c.Parameters.AddWithValue("param", row.Param) |> ignore
                c.Parameters.AddWithValue("value", row.Value) |> ignore
                c.Parameters.AddWithValue("system", row.System) |> ignore
                c.Parameters.AddWithValue("code", row.Code) |> ignore
                c.ExecuteNonQuery() |> ignore

        member _.SearchCurrent(resourceType, plan) =
            let table = ResourceCatalog.quoteIdent (ResourceCatalog.tableName resourceType)
            let built = SearchIndexQuery.buildWhere resourceType plan

            let bind (c: NpgsqlCommand) =
                for name, value in built.Parameters do
                    c.Parameters.AddWithValue(name, value) |> ignore

            let total =
                use c = cmd ()
                c.CommandText <- $"SELECT COUNT(*) FROM {table} t{built.WhereClause};"
                bind c
                Convert.ToInt32(c.ExecuteScalar())

            let rows =
                use c = cmd ()

                c.CommandText <-
                    $"SELECT t.id, t.txid, t.ts, t.resource::text FROM {table} t{built.WhereClause} {built.OrderBy} LIMIT @limit OFFSET @offset;"

                bind c
                c.Parameters.AddWithValue("limit", plan.Count) |> ignore
                c.Parameters.AddWithValue("offset", plan.Offset) |> ignore
                use reader = c.ExecuteReader()

                [
                    while reader.Read() do
                        yield
                            reader.GetString(0),
                            reader.GetInt64(1),
                            reader.GetFieldValue<DateTimeOffset>(2),
                            reader.GetString(3)
                ]

            rows, total

        member _.IsSearchIndexBackfilled() =
            use c = cmd ()
            c.CommandText <- "SELECT backfilled FROM search_index_state WHERE id = 1;"

            match c.ExecuteScalar() with
            | :? bool as b -> b
            | _ -> false

        member _.MarkSearchIndexBackfilled() =
            use c = cmd ()
            c.CommandText <- "UPDATE search_index_state SET backfilled = true WHERE id = 1;"
            c.ExecuteNonQuery() |> ignore

        member _.UpsertOrganizationClosure(orgId, parentOrgId) =
            // Remove external ancestor links into this subtree, keep internal links, then attach under parent.
            use del = cmd ()

            del.CommandText <-
                """
                WITH subtree AS (
                  SELECT descendant_id FROM organization_closure WHERE ancestor_id = @id
                  UNION
                  SELECT @id
                )
                DELETE FROM organization_closure
                WHERE descendant_id IN (SELECT descendant_id FROM subtree)
                  AND ancestor_id NOT IN (SELECT descendant_id FROM subtree);
                """

            del.Parameters.AddWithValue("id", orgId) |> ignore
            del.ExecuteNonQuery() |> ignore

            use self = cmd ()

            self.CommandText <-
                """
                INSERT INTO organization_closure (ancestor_id, descendant_id, depth)
                VALUES (@id, @id, 0)
                ON CONFLICT (ancestor_id, descendant_id) DO UPDATE SET depth = EXCLUDED.depth;
                """

            self.Parameters.AddWithValue("id", orgId) |> ignore
            self.ExecuteNonQuery() |> ignore

            match parentOrgId with
            | None -> ()
            | Some parent ->
                use link = cmd ()

                link.CommandText <-
                    """
                    INSERT INTO organization_closure (ancestor_id, descendant_id, depth)
                    SELECT a.ancestor_id, d.descendant_id, a.depth + d.depth + 1
                    FROM organization_closure a
                    CROSS JOIN organization_closure d
                    WHERE a.descendant_id = @parent
                      AND d.ancestor_id = @id
                    ON CONFLICT (ancestor_id, descendant_id) DO UPDATE SET depth = EXCLUDED.depth;
                    """

                link.Parameters.AddWithValue("parent", parent) |> ignore
                link.Parameters.AddWithValue("id", orgId) |> ignore
                link.ExecuteNonQuery() |> ignore

        member _.DeleteOrganizationClosure(orgId) =
            use c = cmd ()
            c.CommandText <- "DELETE FROM organization_closure WHERE ancestor_id = @id OR descendant_id = @id;"
            c.Parameters.AddWithValue("id", orgId) |> ignore
            c.ExecuteNonQuery() |> ignore

    interface IDisposable with
        member _.Dispose() =
            match tx with
            | Some t ->
                try
                    t.Rollback()
                with _ ->
                    ()

                t.Dispose()
                tx <- None
            | None -> ()

            conn.Dispose()

type PostgresEnvelopeDialect(connectionString: string) =
    interface IEnvelopeDialect with
        member _.Migrate() =
            PostgresMigrations.migrate connectionString

        member _.OpenSession() =
            new PostgresEnvelopeSession(connectionString) :> IEnvelopeSession
