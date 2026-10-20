namespace FhirServer.Application.Audit

open System.Text.Json.Nodes
open Microsoft.Extensions.Logging
open FhirServer.Domain.Ports
open FhirServer.Application

/// Persists server-generated AuditEvents from the BALP audit middleware.
module AuditRecorder =

    let record (store: IResourceStore) (logger: ILogger) (auditEvent: JsonObject) =
        try
            match ResourceHandlers.create store "AuditEvent" auditEvent with
            | Ok _ -> ()
            | Error err -> logger.LogWarning("Failed to persist AuditEvent: {Error}", sprintf "%A" err)
        with ex ->
            logger.LogWarning(ex, "Exception while persisting AuditEvent")
