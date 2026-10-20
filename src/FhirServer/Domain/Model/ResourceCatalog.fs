namespace FhirServer.Domain

open FhirServer.Domain.Types
open FhirServer.Schemas

/// Catalog of supported FHIR types and envelope table naming.
module ResourceCatalog =

    let parseStatus =
        function
        | "created" -> Created
        | "updated" -> Updated
        | "deleted" -> Deleted
        | "recreated" -> Recreated
        | other -> failwith $"Unknown resource status '{other}'"

    let statusString =
        function
        | Created -> "created"
        | Updated -> "updated"
        | Deleted -> "deleted"
        | Recreated -> "recreated"

    /// Lowercase table names equal to the FHIR type.
    let tableName (resourceType: string) = resourceType.ToLowerInvariant()

    let historyTableName (resourceType: string) = $"{tableName resourceType}_history"

    /// Quote Postgres identifiers (FHIR types like Group collide with reserved words).
    let quoteIdent (name: string) =
        "\"" + name.Replace("\"", "\"\"") + "\""

    /// All concrete R5 resource types from the generator (`make generate-r5-all` / sync-schemas).
    /// Keep migrations in sync via CREATE TABLE IF NOT EXISTS for the full list.
    let supportedResourceTypes = GeneratedResourceCatalog.supportedResourceTypes

    let fhirVersion = GeneratedResourceCatalog.fhirVersion

    let isSupported (resourceType: string) =
        supportedResourceTypes
        |> List.exists (fun t -> t.Equals(resourceType, System.StringComparison.Ordinal))

    let ensureSupported (resourceType: string) =
        if isSupported resourceType then
            ()
        else
            invalidArg (nameof resourceType) $"Unsupported resource type '{resourceType}'"
