namespace FhirServer.Domain

open System
open System.Text.Json.Nodes

/// Domain types — no Firely dependencies.
module Types =

    /// FHIR resource document (JSON object).
    type FhirResource = JsonObject

    /// Lightweight validation issue (FHIR OperationOutcome severities).
    type ValidationIssue = {
        Diagnostics: string
        Fatal: bool
        /// fatal | error | warning | information
        Severity: string
    }

    type FhirError =
        | NotFound of resourceType: string * id: string
        /// Resource existed and was deleted (HTTP 410). issue.code = deleted.
        | Gone of resourceType: string * id: string * vid: string option
        | VersionNotFound of resourceType: string * id: string * vid: string
        | BadRequest of message: string
        | Forbidden of message: string
        | UnsupportedType of string
        | Conflict of message: string
        | PreconditionFailed of message: string
        /// Failed FHIR validation on write (HTTP 422).
        | ValidationFailed of ValidationIssue list

    type FhirResult<'T> = Result<'T, FhirError>

    type HistoryStatus =
        | Created
        | Updated
        | Deleted
        | Recreated

    [<NoComparison>]
    type HistoryEntry = {
        ResourceType: string
        Id: string
        Txid: int64
        Timestamp: DateTimeOffset
        Status: HistoryStatus
        Resource: FhirResource option
    }

    type HistoryFilter = {
        ResourceType: string option
        Id: string option
        Since: DateTimeOffset option
        Count: int
        Offset: int
    }

    type PreferReturn =
        | ReturnRepresentation
        | ReturnMinimal
        | ReturnOutcome

    [<NoComparison>]
    type Interaction =
        | Read of resourceType: string * id: string
        | VRead of resourceType: string * id: string * vid: string
        | Create of resourceType: string * body: FhirResource
        | Update of resourceType: string * id: string * body: FhirResource
        | Delete of resourceType: string * id: string
        | Search of resourceType: string * query: Map<string, string list>
        | History of HistoryFilter
        | Capabilities
