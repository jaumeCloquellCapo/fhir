namespace FhirServer.Infrastructure.Persistence

open System
open FhirServer.Domain.Services
open FhirServer.Domain.Types

/// One envelope row (current or history).
type EnvelopeRow = {
    Txid: int64
    Cts: DateTimeOffset
    Ts: DateTimeOffset
    Status: string
    BodyJson: string
    /// ORGBAC owner Organization id; empty when written unrestricted / system-shared.
    TenantId: string
    TenantMode: TenantMode
}

/// SQL envelope session. Owned by caller; dispose closes connection.
type IEnvelopeSession =
    inherit IDisposable
    abstract member BeginTransaction: unit -> unit
    abstract member Commit: unit -> unit
    abstract member Rollback: unit -> unit
    abstract member NextTxid: unit -> int64
    abstract member TryReadCurrent: resourceType: string * id: string -> EnvelopeRow option

    abstract member TryGetVersion:
        resourceType: string * id: string * vid: int64 -> (int64 * DateTimeOffset * string * string) option

    abstract member InsertHistory: resourceType: string * id: string * row: EnvelopeRow -> unit

    abstract member UpdateCurrent:
        resourceType: string * id: string * txid: int64 * ts: DateTimeOffset * bodyJson: string -> unit

    abstract member InsertCurrent: resourceType: string * id: string * row: EnvelopeRow -> unit
    abstract member DeleteCurrent: resourceType: string * id: string -> unit
    abstract member WasDeleted: resourceType: string * id: string -> bool
    abstract member ListCurrent: resourceType: string -> (string * int64 * DateTimeOffset * string) list

    /// AND of `resource @> @patch::jsonb` for each top-level string filter; LIMIT applied in SQL.
    abstract member ListCurrentByJsonbContainment:
        resourceType: string
            * filters: (string * string) list
            * limit: int
            -> (string * int64 * DateTimeOffset * string) list

    /// Returns (page rows ordered by txid DESC, total matching count). LIMIT/OFFSET applied in SQL when possible.
    abstract member QueryHistory:
        filter: HistoryFilter -> (string * string * int64 * DateTimeOffset * string * string) list * int

    /// Keyset page of current rows: ORDER BY id, optional afterId exclusive, LIMIT.
    abstract member ListCurrentPage:
        resourceType: string * afterId: string option * limit: int -> (string * int64 * DateTimeOffset * string) list

    /// Batch read by ids (`WHERE id = ANY(@ids)`).
    abstract member ReadCurrentMany:
        resourceType: string * ids: string list -> (string * int64 * DateTimeOffset * string) list

    abstract member ReplaceSearchIndex:
        resourceType: string * id: string * rows: SearchIndexer.IndexRows -> unit

    abstract member ClearSearchIndex: resourceType: string * id: string -> unit

    abstract member SearchCurrent:
        resourceType: string * plan: SearchEngine.SearchPlan -> (string * int64 * DateTimeOffset * string) list * int

    abstract member IsSearchIndexBackfilled: unit -> bool
    abstract member MarkSearchIndexBackfilled: unit -> unit

    /// Upsert closure rows for Organization id (self + ancestors via optional partOf parent).
    abstract member UpsertOrganizationClosure: orgId: string * parentOrgId: string option -> unit

    /// Remove closure rows involving this Organization id.
    abstract member DeleteOrganizationClosure: orgId: string -> unit

/// Outbound adapter factory for the SQL store.
type IEnvelopeDialect =
    abstract member Migrate: unit -> unit
    abstract member OpenSession: unit -> IEnvelopeSession
