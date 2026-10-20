namespace FhirServer.E2E

open System
open System.IO
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.Hosting
open Npgsql
open FhirServer

/// Boots FhirServer in-process with an isolated Postgres database per factory.
type FhirApiFactory() =
    inherit WebApplicationFactory<Marker>()

    static let adminConnectionString () =
        match Environment.GetEnvironmentVariable("FHIR_E2E_ADMIN_CONNECTION") with
        | null
        | "" -> "Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir"
        | value -> value

    let dbName = $"fhir_e2e_{Guid.NewGuid():N}"

    let appConnectionString =
        let builder = NpgsqlConnectionStringBuilder(adminConnectionString ())
        builder.Database <- dbName
        builder.ConnectionString

    do
        use conn = new NpgsqlConnection(adminConnectionString ())
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"CREATE DATABASE \"{dbName.Replace("\"", "\"\"")}\";"
        cmd.ExecuteNonQuery() |> ignore

    member _.DbName = dbName

    override _.ConfigureWebHost(builder: IWebHostBuilder) =
        let contentRoot =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FhirServer"))

        builder
            .UseEnvironment(Environments.Development)
            .UseContentRoot(contentRoot)
            .UseSetting("ConnectionStrings:Fhir", appConnectionString)
            .UseSetting("Auth:Enabled", "false")
        |> ignore

    override this.Dispose(disposing) =
        base.Dispose(disposing)

        if disposing then
            try
                use conn = new NpgsqlConnection(adminConnectionString ())
                conn.Open()

                use terminate = conn.CreateCommand()

                terminate.CommandText <-
                    """
                    SELECT pg_terminate_backend(pid)
                    FROM pg_stat_activity
                    WHERE datname = @db AND pid <> pg_backend_pid();
                    """

                terminate.Parameters.AddWithValue("db", dbName) |> ignore
                terminate.ExecuteNonQuery() |> ignore

                use drop = conn.CreateCommand()
                drop.CommandText <- $"DROP DATABASE IF EXISTS \"{dbName.Replace("\"", "\"\"")}\";"
                drop.ExecuteNonQuery() |> ignore
            with _ ->
                ()
