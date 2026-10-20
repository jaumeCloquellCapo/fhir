namespace FhirServer.Infrastructure.Persistence.Postgres

open System
open Npgsql
open FhirServer.Domain
open FhirServer.Infrastructure.Persistence

module PostgresMigrations =

    let private createResourceTables (resourceType: string) =
        let table = ResourceCatalog.tableName resourceType
        let history = ResourceCatalog.historyTableName resourceType
        let qTable = ResourceCatalog.quoteIdent table
        let qHistory = ResourceCatalog.quoteIdent history
        let qIdxTable = ResourceCatalog.quoteIdent $"idx_{table}_ts"
        let qIdxHistory = ResourceCatalog.quoteIdent $"idx_{history}_ts"

        $"""
        CREATE TABLE IF NOT EXISTS {qTable} (
            id            text PRIMARY KEY,
            txid          bigint NOT NULL,
            cts           timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            ts            timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            resource_type text NOT NULL DEFAULT '{resourceType}',
            status        text NOT NULL CHECK (status IN ('created', 'updated', 'deleted', 'recreated')),
            resource      jsonb NOT NULL
        );

        CREATE TABLE IF NOT EXISTS {qHistory} (
            id            text NOT NULL,
            txid          bigint NOT NULL,
            cts           timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            ts            timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
            resource_type text NOT NULL DEFAULT '{resourceType}',
            status        text NOT NULL CHECK (status IN ('created', 'updated', 'deleted', 'recreated')),
            resource      jsonb NOT NULL,
            PRIMARY KEY (id, txid)
        );

        CREATE INDEX IF NOT EXISTS {qIdxTable} ON {qTable} (ts);
        CREATE INDEX IF NOT EXISTS {qIdxHistory} ON {qHistory} (ts);
        """

    let private migrations: MigrationRunner.Migration list = [
        {
            Version = 1
            Name = "envelope_core"
            Script =
                """
              CREATE TABLE IF NOT EXISTS schema_migrations (
                  version    integer PRIMARY KEY,
                  name       text NOT NULL,
                  applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
              );

              CREATE TABLE IF NOT EXISTS txid_counter (
                  id    integer PRIMARY KEY CHECK (id = 1),
                  value bigint NOT NULL
              );

              INSERT INTO txid_counter (id, value) VALUES (1, 0)
              ON CONFLICT (id) DO NOTHING;
              """
        }
        {
            Version = 2
            Name = "resource_tables"
            Script =
                [ "Patient"; "Observation"; "Encounter"; "Practitioner"; "Organization" ]
                |> List.map createResourceTables
                |> String.concat "\n"
        }
        {
            Version = 3
            Name = "all_generated_resource_tables"
            Script =
                ResourceCatalog.supportedResourceTypes
                |> List.map createResourceTables
                |> String.concat "\n"
        }
        {
            Version = 4
            Name = "search_index_tables"
            Script =
                """
                CREATE TABLE IF NOT EXISTS search_string (
                    resource_type text NOT NULL,
                    id            text NOT NULL,
                    param         text NOT NULL,
                    value         text NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_search_string_lookup
                    ON search_string (resource_type, param, value);
                CREATE INDEX IF NOT EXISTS idx_search_string_resource
                    ON search_string (resource_type, id);

                CREATE TABLE IF NOT EXISTS search_token (
                    resource_type text NOT NULL,
                    id            text NOT NULL,
                    param         text NOT NULL,
                    system        text NOT NULL,
                    code          text NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_search_token_lookup
                    ON search_token (resource_type, param, code);
                CREATE INDEX IF NOT EXISTS idx_search_token_resource
                    ON search_token (resource_type, id);

                CREATE TABLE IF NOT EXISTS search_ref (
                    resource_type text NOT NULL,
                    id            text NOT NULL,
                    param         text NOT NULL,
                    target        text NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_search_ref_lookup
                    ON search_ref (resource_type, param, target);
                CREATE INDEX IF NOT EXISTS idx_search_ref_resource
                    ON search_ref (resource_type, id);

                CREATE TABLE IF NOT EXISTS search_index_state (
                    id         integer PRIMARY KEY CHECK (id = 1),
                    backfilled boolean NOT NULL DEFAULT false
                );
                INSERT INTO search_index_state (id, backfilled) VALUES (1, false)
                ON CONFLICT (id) DO NOTHING;
                """
        }
    ]

    let migrate (connectionString: string) =
        use conn = new NpgsqlConnection(connectionString)
        conn.Open()

        let journal: MigrationRunner.Journal = {
            Ensure =
                fun () ->
                    use ensure = conn.CreateCommand()

                    ensure.CommandText <-
                        """
                        CREATE TABLE IF NOT EXISTS schema_migrations (
                            version    integer PRIMARY KEY,
                            name       text NOT NULL,
                            applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
                        );
                        """

                    ensure.ExecuteNonQuery() |> ignore
            AppliedVersions =
                fun () ->
                    use c = conn.CreateCommand()
                    c.CommandText <- "SELECT version FROM schema_migrations;"
                    use reader = c.ExecuteReader()

                    [
                        while reader.Read() do
                            yield reader.GetInt32(0)
                    ]
                    |> Set.ofList
            Apply =
                fun migration ->
                    use tx = conn.BeginTransaction()
                    use c = conn.CreateCommand()
                    c.Transaction <- tx
                    c.CommandText <- migration.Script
                    c.ExecuteNonQuery() |> ignore

                    use mark = conn.CreateCommand()
                    mark.Transaction <- tx
                    mark.CommandText <- "INSERT INTO schema_migrations (version, name) VALUES (@version, @name);"
                    mark.Parameters.AddWithValue("version", migration.Version) |> ignore
                    mark.Parameters.AddWithValue("name", migration.Name) |> ignore
                    mark.ExecuteNonQuery() |> ignore
                    tx.Commit()
        }

        MigrationRunner.migrate journal migrations
