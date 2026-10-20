namespace FhirServer.Infrastructure.Persistence.Sqlite

open System
open Microsoft.Data.Sqlite
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Infrastructure.Persistence

type SqliteEnvelopeSession(connectionString: string) =
    let conn = new SqliteConnection(connectionString)
    do conn.Open()
    let mutable tx: SqliteTransaction option = None

    let cmd () =
        let c = conn.CreateCommand()

        match tx with
        | Some t -> c.Transaction <- t
        | None -> ()

        c

    let parseTs (s: string) = EnvelopeCodec.parseTs s

    interface IEnvelopeSession with
        member _.BeginTransaction() = tx <- Some(conn.BeginTransaction())

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
            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <- $"SELECT txid, cts, ts, status, resource FROM {table} WHERE id = $id LIMIT 1;"

            c.Parameters.AddWithValue("$id", id) |> ignore
            use reader = c.ExecuteReader()

            if reader.Read() then
                Some {
                    Txid = reader.GetInt64(0)
                    Cts = parseTs (reader.GetString(1))
                    Ts = parseTs (reader.GetString(2))
                    Status = reader.GetString(3)
                    BodyJson = reader.GetString(4)
                }
            else
                None

        member _.TryGetVersion(resourceType, id, vid) =
            let history =
                ResourceCatalog.sqlIdent (ResourceCatalog.historyTableName resourceType)

            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"""
                SELECT txid, ts, status, resource FROM {history}
                WHERE id = $id AND txid = $txid
                UNION ALL
                SELECT txid, ts, status, resource FROM {table}
                WHERE id = $id AND txid = $txid
                LIMIT 1;
                """

            c.Parameters.AddWithValue("$id", id) |> ignore
            c.Parameters.AddWithValue("$txid", vid) |> ignore
            use reader = c.ExecuteReader()

            if reader.Read() then
                Some(reader.GetInt64(0), parseTs (reader.GetString(1)), reader.GetString(2), reader.GetString(3))
            else
                None

        member _.InsertHistory(resourceType, id, row) =
            let history =
                ResourceCatalog.sqlIdent (ResourceCatalog.historyTableName resourceType)

            use c = cmd ()

            c.CommandText <-
                $"""
                INSERT INTO {history} (id, txid, cts, ts, resource_type, status, resource)
                VALUES ($id, $txid, $cts, $ts, $rtype, $status, $resource);
                """

            c.Parameters.AddWithValue("$id", id) |> ignore
            c.Parameters.AddWithValue("$txid", row.Txid) |> ignore
            c.Parameters.AddWithValue("$cts", row.Cts.ToString("O")) |> ignore
            c.Parameters.AddWithValue("$ts", row.Ts.ToString("O")) |> ignore
            c.Parameters.AddWithValue("$rtype", resourceType) |> ignore
            c.Parameters.AddWithValue("$status", row.Status) |> ignore
            c.Parameters.AddWithValue("$resource", row.BodyJson) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.UpdateCurrent(resourceType, id, txid, ts, bodyJson) =
            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"UPDATE {table} SET txid = $txid, ts = $ts, status = 'updated', resource = $resource WHERE id = $id;"

            c.Parameters.AddWithValue("$txid", txid) |> ignore
            c.Parameters.AddWithValue("$ts", ts.ToString("O")) |> ignore
            c.Parameters.AddWithValue("$resource", bodyJson) |> ignore
            c.Parameters.AddWithValue("$id", id) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.InsertCurrent(resourceType, id, row) =
            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()

            c.CommandText <-
                $"""
                INSERT INTO {table} (id, txid, cts, ts, resource_type, status, resource)
                VALUES ($id, $txid, $cts, $ts, $rtype, 'created', $resource);
                """

            c.Parameters.AddWithValue("$id", id) |> ignore
            c.Parameters.AddWithValue("$txid", row.Txid) |> ignore
            c.Parameters.AddWithValue("$cts", row.Cts.ToString("O")) |> ignore
            c.Parameters.AddWithValue("$ts", row.Ts.ToString("O")) |> ignore
            c.Parameters.AddWithValue("$rtype", resourceType) |> ignore
            c.Parameters.AddWithValue("$resource", row.BodyJson) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.DeleteCurrent(resourceType, id) =
            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()
            c.CommandText <- $"DELETE FROM {table} WHERE id = $id;"
            c.Parameters.AddWithValue("$id", id) |> ignore
            c.ExecuteNonQuery() |> ignore

        member _.WasDeleted(resourceType, id) =
            let history =
                ResourceCatalog.sqlIdent (ResourceCatalog.historyTableName resourceType)

            use c = cmd ()
            c.CommandText <- $"SELECT 1 FROM {history} WHERE id = $id AND status = 'deleted' LIMIT 1;"
            c.Parameters.AddWithValue("$id", id) |> ignore
            not (isNull (c.ExecuteScalar()))

        member _.ListCurrent(resourceType) =
            let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)
            use c = cmd ()
            c.CommandText <- $"SELECT id, txid, ts, resource FROM {table} ORDER BY id;"
            use reader = c.ExecuteReader()

            [
                while reader.Read() do
                    yield reader.GetString(0), reader.GetInt64(1), parseTs (reader.GetString(2)), reader.GetString(3)
            ]

        member _.QueryHistory(filter) =
            let types =
                match filter.ResourceType with
                | Some rt -> [ rt ]
                | None -> ResourceCatalog.supportedResourceTypes

            types
            |> List.collect (fun resourceType ->
                let table = ResourceCatalog.sqlIdent (ResourceCatalog.tableName resourceType)

                let history =
                    ResourceCatalog.sqlIdent (ResourceCatalog.historyTableName resourceType)

                let idClause, idParam =
                    match filter.Id with
                    | Some id -> " AND id = $id", Some id
                    | None -> "", None

                let sinceClause, sinceParam =
                    match filter.Since with
                    | Some since -> " AND ts >= $since", Some(since.ToString("O"))
                    | None -> "", None

                let read source =
                    use c = cmd ()

                    c.CommandText <-
                        $"""
                        SELECT '{resourceType}' AS resource_type, id, txid, ts, status, resource
                        FROM {source}
                        WHERE 1 = 1{idClause}{sinceClause}
                        """

                    match idParam with
                    | Some id -> c.Parameters.AddWithValue("$id", id) |> ignore
                    | None -> ()

                    match sinceParam with
                    | Some since -> c.Parameters.AddWithValue("$since", since) |> ignore
                    | None -> ()

                    use reader = c.ExecuteReader()

                    [
                        while reader.Read() do
                            yield
                                reader.GetString(0),
                                reader.GetString(1),
                                reader.GetInt64(2),
                                parseTs (reader.GetString(3)),
                                reader.GetString(4),
                                reader.GetString(5)
                    ]

                read history @ read table)

    interface IDisposable with
        member _.Dispose() =
            match tx with
            | Some t ->
                t.Dispose()
                tx <- None
            | None -> ()

            conn.Dispose()

type SqliteEnvelopeDialect(connectionString: string) =
    interface IEnvelopeDialect with
        member _.Migrate() =
            SqliteMigrations.migrate connectionString

        member _.OpenSession() =
            new SqliteEnvelopeSession(connectionString) :> IEnvelopeSession
