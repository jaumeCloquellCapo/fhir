namespace FhirServer.Domain

open System
open System.Collections.Generic
open FhirServer.Domain.Types
open FhirServer.Schemas

/// Catalog of supported FHIR types and envelope table naming.
module ResourceCatalog =

    let tryParseStatus =
        function
        | "created" -> Some Created
        | "updated" -> Some Updated
        | "deleted" -> Some Deleted
        | "recreated" -> Some Recreated
        | _ -> None

    let parseStatus status =
        match tryParseStatus status with
        | Some s -> s
        | None -> invalidArg (nameof status) $"Unknown resource status '{status}'"

    let statusString =
        function
        | Created -> "created"
        | Updated -> "updated"
        | Deleted -> "deleted"
        | Recreated -> "recreated"

    /// Lowercase table names equal to the FHIR type.
    let tableName (resourceType: string) = resourceType.ToLowerInvariant()

    let historyTableName (resourceType: string) = $"{tableName resourceType}_history"

    /// Quote SQL identifiers (FHIR types like Group collide with reserved words).
    let quoteIdent (name: string) =
        "\"" + name.Replace("\"", "\"\"") + "\""

    /// All concrete R5 resource types from the generator (`make generate-r5-all` / sync-schemas).
    /// Keep migrations in sync via CREATE TABLE IF NOT EXISTS for the full list.
    let supportedResourceTypes = GeneratedResourceCatalog.supportedResourceTypes

    let private supportedSet =
        HashSet<string>(supportedResourceTypes, StringComparer.Ordinal)

    let fhirVersion = GeneratedResourceCatalog.fhirVersion

    let isSupported (resourceType: string) = supportedSet.Contains(resourceType)

    let ensureSupported (resourceType: string) =
        if isSupported resourceType then
            ()
        else
            invalidArg (nameof resourceType) $"Unsupported resource type '{resourceType}'"
