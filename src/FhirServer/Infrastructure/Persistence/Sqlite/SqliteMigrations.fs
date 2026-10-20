namespace FhirServer.Infrastructure.Persistence.Sqlite

open System
open System.IO
open Microsoft.Data.Sqlite
open FhirServer.Domain
open FhirServer.Infrastructure.Persistence

module SqliteMigrations =

    let private createResourceTablesSql (resourceType: string) =
        let table = ResourceCatalog.tableName resourceType
        let history = ResourceCatalog.historyTableName resourceType
        let qTable = ResourceCatalog.sqlIdent table
        let qHistory = ResourceCatalog.sqlIdent history
        let qIdxTable = ResourceCatalog.sqlIdent $"idx_{table}_ts"
        let qIdxHistory = ResourceCatalog.sqlIdent $"idx_{history}_ts"

        $"""
        CREATE TABLE IF NOT EXISTS {qTable} (
            id            TEXT NOT NULL PRIMARY KEY,
            txid          INTEGER NOT NULL,
            cts           TEXT NOT NULL,
            ts            TEXT NOT NULL,
            resource_type TEXT NOT NULL DEFAULT '{resourceType}',
            status        TEXT NOT NULL CHECK (status IN ('created', 'updated', 'deleted', 'recreated')),
            resource      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS {qHistory} (
            id            TEXT NOT NULL,
            txid          INTEGER NOT NULL,
            cts           TEXT NOT NULL,
            ts            TEXT NOT NULL,
            resource_type TEXT NOT NULL DEFAULT '{resourceType}',
            status        TEXT NOT NULL CHECK (status IN ('created', 'updated', 'deleted', 'recreated')),
            resource      TEXT NOT NULL,
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
                  version    INTEGER NOT NULL PRIMARY KEY,
                  name       TEXT NOT NULL,
                  applied_at TEXT NOT NULL
              );

              CREATE TABLE IF NOT EXISTS txid_counter (
                  id    INTEGER PRIMARY KEY CHECK (id = 1),
                  value INTEGER NOT NULL
              );

              INSERT OR IGNORE INTO txid_counter (id, value) VALUES (1, 0);
              """
        }
        {
            Version = 2
            Name = "resource_tables"
            Sql =
                // Historical: first five types. Kept so existing DBs stay consistent.
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

    let private ensureDirectory (connectionString: string) =
        let builder = SqliteConnectionStringBuilder(connectionString)
        let dataSource = builder.DataSource

        if not (String.IsNullOrWhiteSpace dataSource) && dataSource <> ":memory:" then
            let fullPath = Path.GetFullPath(dataSource)
            let dir = Path.GetDirectoryName(fullPath)

            if not (String.IsNullOrWhiteSpace dir) then
                Directory.CreateDirectory(dir) |> ignore

    let migrate (connectionString: string) =
        ensureDirectory connectionString

        use conn = new SqliteConnection(connectionString)
        conn.Open()

        let journal: MigrationRunner.Journal = {
            Ensure = fun () -> ()
            AppliedVersions =
                fun () ->
                    use existsCmd = conn.CreateCommand()

                    existsCmd.CommandText <-
                        """
                        SELECT 1 FROM sqlite_master
                        WHERE type = 'table' AND name = 'schema_migrations'
                        LIMIT 1;
                        """

                    match existsCmd.ExecuteScalar() with
                    | null -> Set.empty
                    | _ ->
                        use cmd = conn.CreateCommand()
                        cmd.CommandText <- "SELECT version FROM schema_migrations;"
                        use reader = cmd.ExecuteReader()

                        [
                            while reader.Read() do
                                yield reader.GetInt32(0)
                        ]
                        |> Set.ofList
            Apply =
                fun migration ->
                    use tx = conn.BeginTransaction()
                    use cmd = conn.CreateCommand()
                    cmd.Transaction <- tx
                    cmd.CommandText <- migration.Sql
                    cmd.ExecuteNonQuery() |> ignore

                    use mark = conn.CreateCommand()
                    mark.Transaction <- tx

                    mark.CommandText <-
                        """
                        INSERT INTO schema_migrations (version, name, applied_at)
                        VALUES ($version, $name, $applied_at);
                        """

                    mark.Parameters.AddWithValue("$version", migration.Version) |> ignore
                    mark.Parameters.AddWithValue("$name", migration.Name) |> ignore

                    mark.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"))
                    |> ignore

                    mark.ExecuteNonQuery() |> ignore
                    tx.Commit()
        }

        MigrationRunner.migrate journal migrations
