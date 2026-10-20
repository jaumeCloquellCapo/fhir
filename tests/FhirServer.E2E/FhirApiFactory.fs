namespace FhirServer.E2E

open System
open System.IO
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Mvc.Testing
open Microsoft.Extensions.Hosting
open FhirServer

/// Boots FhirServer in-process with an isolated SQLite file per factory.
type FhirApiFactory() =
    inherit WebApplicationFactory<Marker>()

    let dbPath = Path.Combine(Path.GetTempPath(), $"fhir-e2e-{Guid.NewGuid():N}.db")

    member _.DbPath = dbPath

    override _.ConfigureWebHost(builder: IWebHostBuilder) =
        let contentRoot =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FhirServer"))

        builder
            .UseEnvironment(Environments.Development)
            .UseContentRoot(contentRoot)
            .UseSetting("Store:Provider", "sqlite")
            .UseSetting("ConnectionStrings:Fhir", $"Data Source={dbPath}")
            .UseSetting("Auth:Enabled", "false")
        |> ignore

    override this.Dispose(disposing) =
        base.Dispose(disposing)

        if disposing then
            try
                if File.Exists dbPath then
                    File.Delete dbPath
            with _ ->
                ()
