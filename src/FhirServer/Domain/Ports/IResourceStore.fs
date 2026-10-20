namespace FhirServer.Domain.Ports

open FhirServer.Domain.Types

/// Outbound port: persistence of FHIR resources (envelope model).
type IResourceStore =
    abstract member Get: resourceType: string * id: string -> FhirResource option
    abstract member GetVersion: resourceType: string * id: string * vid: int64 -> HistoryEntry option
    /// True when current row is gone but a deleted history row exists (HTTP 410).
    abstract member WasDeleted: resourceType: string * id: string -> bool
    abstract member Upsert: resource: FhirResource -> FhirResource
    /// Soft-delete via history; returns the deleted version resource (DELETE 200 body).
    abstract member Delete: resourceType: string * id: string -> FhirResource option
    abstract member ListByType: resourceType: string -> FhirResource list

    /// Aidbox-style jsonb containment: AND of `resource @> {"field":"value"}` for top-level string props.
    /// Filters before hydrate; default limit 100. Prefer over ListByType + in-memory filter.
    abstract member ListByTypeWhere:
        resourceType: string * filters: (string * string) list * limit: int -> FhirResource list

    /// Same WHERE as ListByTypeWhere with ORDER BY id LIMIT 1 (List.tryFind semantics).
    abstract member TryFindByTypeWhere:
        resourceType: string * filters: (string * string) list -> FhirResource option

    /// Returns (page, total matching count).
    abstract member Search: resourceType: string * query: Map<string, string list> -> FhirResource list * int
    abstract member ListHistory: filter: HistoryFilter -> HistoryEntry list * int
    abstract member RunInTransaction: action: (unit -> 'T) -> 'T
