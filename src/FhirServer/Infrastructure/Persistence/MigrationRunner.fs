namespace FhirServer.Infrastructure.Persistence

open System

/// Shared schema-migration journal runner for the SQL store.
module MigrationRunner =

    type Migration = {
        Version: int
        Name: string
        Script: string
    }

    type Journal = {
        Ensure: unit -> unit
        AppliedVersions: unit -> Set<int>
        Apply: Migration -> unit
    }

    let migrate (journal: Journal) (migrations: Migration list) =
        journal.Ensure()
        let applied = journal.AppliedVersions()

        for migration in migrations |> List.sortBy (fun m -> m.Version) do
            if not (applied.Contains migration.Version) then
                journal.Apply migration
