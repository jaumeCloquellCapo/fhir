namespace FhirServer.E2E

open System
open System.IO
open System.Text.Json

/// Loads NDJSON fixtures copied next to the test assembly (`Fixtures/`).
module FixtureFiles =

    let private fixturesDir () =
        Path.Combine(AppContext.BaseDirectory, "Fixtures")

    let read (relativeName: string) =
        let path = Path.Combine(fixturesDir (), relativeName)

        if not (File.Exists path) then
            failwithf "Fixture not found: %s (looked in %s)" relativeName (fixturesDir ())

        File.ReadAllText(path)

    /// First resource id in an NDJSON file (skips blank / comment lines).
    let firstResourceId (relativeName: string) =
        read relativeName
        |> fun text -> text.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun l -> l.Trim())
        |> Array.tryFind (fun l -> l.Length > 0 && not (l.StartsWith('#')))
        |> Option.map (fun line ->
            use doc = JsonDocument.Parse(line)
            doc.RootElement.GetProperty("id").GetString())
        |> Option.defaultWith (fun () -> failwithf "No resources in fixture %s" relativeName)
