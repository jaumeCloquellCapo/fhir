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
        {
            Version = 5
            Name = "search_date_quantity_tables"
            Script =
                """
                CREATE TABLE IF NOT EXISTS search_date (
                    resource_type text NOT NULL,
                    id            text NOT NULL,
                    param         text NOT NULL,
                    start_ts      timestamptz NOT NULL,
                    end_ts        timestamptz NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_search_date_lookup
                    ON search_date (resource_type, param, start_ts, end_ts);
                CREATE INDEX IF NOT EXISTS idx_search_date_resource
                    ON search_date (resource_type, id);

                CREATE TABLE IF NOT EXISTS search_quantity (
                    resource_type text NOT NULL,
                    id            text NOT NULL,
                    param         text NOT NULL,
                    value         numeric NOT NULL,
                    system        text NOT NULL,
                    code          text NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_search_quantity_lookup
                    ON search_quantity (resource_type, param, value);
                CREATE INDEX IF NOT EXISTS idx_search_quantity_resource
                    ON search_quantity (resource_type, id);

                UPDATE search_index_state SET backfilled = false WHERE id = 1;
                """
        }
        {
            Version = 6
            Name = "search_catalog_reindex"
            Script =
                """
                -- Regenerate search_* rows after GeneratedSearchCatalog extractors.
                UPDATE search_index_state SET backfilled = false WHERE id = 1;
                """
        }
        {
            Version = 7
            Name = "tenant_id_orgbac"
            Script =
                ResourceCatalog.supportedResourceTypes
                |> List.map (fun resourceType ->
                    let table = ResourceCatalog.tableName resourceType
                    let history = ResourceCatalog.historyTableName resourceType
                    let qTable = ResourceCatalog.quoteIdent table
                    let qHistory = ResourceCatalog.quoteIdent history
                    let qIdx = ResourceCatalog.quoteIdent $"idx_{table}_tenant"
                    let qIdxHistory = ResourceCatalog.quoteIdent $"idx_{history}_tenant"

                    $"""
                    ALTER TABLE {qTable} ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '';
                    ALTER TABLE {qHistory} ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '';
                    CREATE INDEX IF NOT EXISTS {qIdx} ON {qTable} (tenant_id);
                    CREATE INDEX IF NOT EXISTS {qIdxHistory} ON {qHistory} (tenant_id);
                    """)
                |> String.concat "\n"
        }
        {
            Version = 8
            Name = "tenant_rls"
            Script =
                // Empty app.tenant_id = unrestricted (admin). Otherwise row.tenant_id must match.
                // Session sets: SELECT set_config('app.tenant_id', <org|''>, true) inside a transaction.
                ResourceCatalog.supportedResourceTypes
                |> List.map (fun resourceType ->
                    let table = ResourceCatalog.tableName resourceType
                    let history = ResourceCatalog.historyTableName resourceType
                    let qTable = ResourceCatalog.quoteIdent table
                    let qHistory = ResourceCatalog.quoteIdent history
                    let pred =
                        """(COALESCE(current_setting('app.tenant_id', true), '') = ''
                            OR tenant_id = current_setting('app.tenant_id', true))"""

                    $"""
                    ALTER TABLE {qTable} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {qTable} FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS tenant_isolation ON {qTable};
                    CREATE POLICY tenant_isolation ON {qTable}
                      FOR ALL
                      USING {pred}
                      WITH CHECK {pred};

                    ALTER TABLE {qHistory} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {qHistory} FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS tenant_isolation ON {qHistory};
                    CREATE POLICY tenant_isolation ON {qHistory}
                      FOR ALL
                      USING {pred}
                      WITH CHECK {pred};
                    """)
                |> String.concat "\n"
        }
        {
            // Docker POSTGRES_USER is a superuser and bypasses RLS; sessions SET LOCAL ROLE fhir_app.
            Version = 9
            Name = "tenant_rls_app_role"
            Script =
                """
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fhir_app') THEN
                    CREATE ROLE fhir_app NOSUPERUSER NOBYPASSRLS NOINHERIT;
                  END IF;
                END
                $$;

                GRANT USAGE ON SCHEMA public TO fhir_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO fhir_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO fhir_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO fhir_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT USAGE, SELECT ON SEQUENCES TO fhir_app;

                -- Allow the login role (often superuser) to assume fhir_app for RLS.
                GRANT fhir_app TO CURRENT_USER;
                """
        }
        {
            Version = 10
            Name = "orgbac_hierarchy_shared"
            Script =
                let modeColumns =
                    ResourceCatalog.supportedResourceTypes
                    |> List.map (fun resourceType ->
                        let table = ResourceCatalog.tableName resourceType
                        let history = ResourceCatalog.historyTableName resourceType
                        let qTable = ResourceCatalog.quoteIdent table
                        let qHistory = ResourceCatalog.quoteIdent history

                        $"""
                        ALTER TABLE {qTable} ADD COLUMN IF NOT EXISTS tenant_mode text NOT NULL DEFAULT 'owned';
                        ALTER TABLE {qHistory} ADD COLUMN IF NOT EXISTS tenant_mode text NOT NULL DEFAULT 'owned';
                        """)
                    |> String.concat "\n"

                let rls =
                    ResourceCatalog.supportedResourceTypes
                    |> List.map (fun resourceType ->
                        let table = ResourceCatalog.tableName resourceType
                        let history = ResourceCatalog.historyTableName resourceType
                        let qTable = ResourceCatalog.quoteIdent table
                        let qHistory = ResourceCatalog.quoteIdent history

                        // Empty app.tenant_id = admin. orgbac_mode=hierarchy enables parent/child + shared.
                        let usingPred =
                            """(
                              COALESCE(current_setting('app.tenant_id', true), '') = ''
                              OR (
                                COALESCE(current_setting('app.orgbac_mode', true), 'exact') = 'hierarchy'
                                AND (
                                  tenant_mode = 'system-shared'
                                  OR tenant_id IN (
                                    SELECT descendant_id FROM organization_closure
                                    WHERE ancestor_id = current_setting('app.tenant_id', true)
                                  )
                                  OR (
                                    tenant_mode = 'shared'
                                    AND tenant_id IN (
                                      SELECT ancestor_id FROM organization_closure
                                      WHERE descendant_id = current_setting('app.tenant_id', true)
                                        AND depth > 0
                                    )
                                  )
                                )
                              )
                              OR (
                                COALESCE(current_setting('app.orgbac_mode', true), 'exact') <> 'hierarchy'
                                AND tenant_id = current_setting('app.tenant_id', true)
                              )
                            )"""

                        let checkPred =
                            """(
                              COALESCE(current_setting('app.tenant_id', true), '') = ''
                              OR (
                                COALESCE(current_setting('app.orgbac_mode', true), 'exact') = 'hierarchy'
                                AND (
                                  (
                                    tenant_mode = 'owned'
                                    AND tenant_id IN (
                                      SELECT descendant_id FROM organization_closure
                                      WHERE ancestor_id = current_setting('app.tenant_id', true)
                                    )
                                  )
                                  OR (
                                    tenant_mode = 'shared'
                                    AND tenant_id = current_setting('app.tenant_id', true)
                                  )
                                )
                              )
                              OR (
                                COALESCE(current_setting('app.orgbac_mode', true), 'exact') <> 'hierarchy'
                                AND tenant_id = current_setting('app.tenant_id', true)
                                AND tenant_mode IN ('owned', 'shared')
                              )
                            )"""

                        $"""
                        DROP POLICY IF EXISTS tenant_isolation ON {qTable};
                        CREATE POLICY tenant_isolation ON {qTable}
                          FOR ALL
                          USING {usingPred}
                          WITH CHECK {checkPred};

                        DROP POLICY IF EXISTS tenant_isolation ON {qHistory};
                        CREATE POLICY tenant_isolation ON {qHistory}
                          FOR ALL
                          USING {usingPred}
                          WITH CHECK {checkPred};
                        """)
                    |> String.concat "\n"

                $"""
                CREATE TABLE IF NOT EXISTS organization_closure (
                    ancestor_id   text NOT NULL,
                    descendant_id text NOT NULL,
                    depth         integer NOT NULL CHECK (depth >= 0),
                    PRIMARY KEY (ancestor_id, descendant_id)
                );
                CREATE INDEX IF NOT EXISTS idx_organization_closure_descendant
                    ON organization_closure (descendant_id);

                GRANT SELECT, INSERT, UPDATE, DELETE ON organization_closure TO fhir_app;

                {modeColumns}

                {rls}
                """
        }
        {
            // Aidbox-style expression indexes for internal jsonb equality lookups
            // (Subscription status, terminology url) — not full SearchCatalog coverage.
            Version = 11
            Name = "jsonb_hot_path_expression_indexes"
            Script =
                """
                CREATE INDEX IF NOT EXISTS idx_subscription_resource_status
                    ON subscription ((resource->>'status'));
                CREATE INDEX IF NOT EXISTS idx_codesystem_resource_url
                    ON codesystem ((resource->>'url'));
                CREATE INDEX IF NOT EXISTS idx_valueset_resource_url
                    ON valueset ((resource->>'url'));
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
