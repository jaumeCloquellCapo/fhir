namespace FhirServer.Infrastructure.Persistence.Postgres

open System
open Npgsql
open FhirServer.Domain
open FhirServer.Infrastructure.Persistence

module PostgresMigrations =

    let private createResourceTablesSql (resourceType: string) =
        let table = ResourceCatalog.tableName resourceType
        let history = ResourceCatalog.historyTableName resourceType
        let qTable = ResourceCatalog.sqlIdent table
        let qHistory = ResourceCatalog.sqlIdent history
        let qIdxTable = ResourceCatalog.sqlIdent $"idx_{table}_ts"
        let qIdxHistory = ResourceCatalog.sqlIdent $"idx_{history}_ts"

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
            Sql =
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
            Sql =
                [ "Patient"; "Observation"; "Encounter"; "Practitioner"; "Organization" ]
                |> List.map createResourceTablesSql
                |> String.concat "\n"
        }
        {
            Version = 3
            Name = "all_generated_resource_tables"
            Sql =
                ResourceCatalog.supportedResourceTypes
                |> List.map createResourceTablesSql
                |> String.concat "\n"
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
                    c.CommandText <- migration.Sql
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
