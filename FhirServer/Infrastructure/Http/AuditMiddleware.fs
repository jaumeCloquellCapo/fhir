namespace FhirServer.Infrastructure.Http

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FhirServer.Application.Audit
open FhirServer.Domain.Ports

module AuditMiddleware =

    let private systemPaths =
        set [
            "/"
            "/health"
            "/$version"
            "/metadata"
            "/.well-known/smart-configuration"
            "/auth/token"
            "/auth/authorize"
            "/auth/register"
        ]

    let private splitPath (path: string) =
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)

    let private firstSegment (path: string) =
        let parts = splitPath path
        if parts.Length = 0 then None else Some parts.[0]

    let private shouldSkip (path: string) =
        systemPaths.Contains path
        || (match firstSegment path with
            | Some "AuditEvent" -> true
            | Some "_hooks" -> true
            | _ -> false)

    let private agentDisplay (ctx: HttpContext) =
        let header = ctx.Request.Headers.Authorization.ToString()

        if header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) then
            try
                let encoded = header.Substring(6).Trim()
                let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
                let parts = decoded.Split(':', 2)

                if parts.Length >= 1 && not (String.IsNullOrWhiteSpace parts.[0]) then
                    parts.[0]
                else
                    "anonymous"
            with _ ->
                "anonymous"
        else
            "anonymous"

    let private hasPatientParam (ctx: HttpContext) =
        ctx.Request.Query.ContainsKey("patient")

    let private queryRaw (ctx: HttpContext) =
        let path = ctx.Request.Path.Value
        let qs = ctx.Request.QueryString.Value
        $"{ctx.Request.Method} {path}{qs}"

    let private classify (ctx: HttpContext) : BalpAuditEventBuilder.AuditRequest option =
        let method = ctx.Request.Method
        let path = ctx.Request.Path.Value

        if shouldSkip path then
            None
        else
            let parts = splitPath path
            let status = ctx.Response.StatusCode
            let agent = agentDisplay ctx
            let recorded = DateTimeOffset.UtcNow
            let patientParam = hasPatientParam ctx

            let baseReq interaction resourceType resourceId versionId includeQuery =
                let req: BalpAuditEventBuilder.AuditRequest = {
                    Interaction = interaction
                    ResourceType = resourceType
                    ResourceId = resourceId
                    VersionId = versionId
                    QueryRaw = if includeQuery then Some(queryRaw ctx) else None
                    HasPatientParam = patientParam
                    AgentDisplay = agent
                    StatusCode = status
                    Recorded = recorded
                }

                Some req

            match method, parts with
            | "POST", [||] -> baseReq BalpAuditEventBuilder.RestInteraction.Transaction None None None false
            | "POST", [| "$validate" |] -> baseReq BalpAuditEventBuilder.RestInteraction.Validate None None None false
            | "POST", [| rt; "$validate" |] ->
                baseReq BalpAuditEventBuilder.RestInteraction.Validate (Some rt) None None false
            | "GET", [| "_history" |] -> baseReq BalpAuditEventBuilder.RestInteraction.History None None None false
            | "GET", [| rt; "_history" |] ->
                baseReq BalpAuditEventBuilder.RestInteraction.History (Some rt) None None false
            | "GET", [| rt; id; "_history"; vid |] ->
                baseReq BalpAuditEventBuilder.RestInteraction.VRead (Some rt) (Some id) (Some vid) false
            | "GET", [| rt; id; "_history" |] ->
                baseReq BalpAuditEventBuilder.RestInteraction.History (Some rt) (Some id) None false
            | "GET", [| _; "$dump" |] -> None
            | "GET", [| _; "$lastn" |] -> None
            | "GET", [| "Patient"; _; "$everything" |] -> None
            | "GET", [| "Patient"; _; "$document" |] -> None
            | "GET", [| "$export" |]
            | "POST", [| "$export" |]
            | "GET", [| "$export-status"; _ |]
            | "GET", [| "$export-files"; _; _ |]
            | "POST", [| "$import" |]
            | "POST", [| "$load" |] -> None
            | "GET", [| rt; id |] -> baseReq BalpAuditEventBuilder.RestInteraction.Read (Some rt) (Some id) None false
            | "GET", [| rt |] -> baseReq BalpAuditEventBuilder.RestInteraction.Search (Some rt) None None true
            | "POST", [| rt |] ->
                // Location is …/{type}/{id}/_history/{vid} (relative or absolute)
                let createdId, versionId =
                    match ctx.Response.Headers.Location.ToString() with
                    | loc when String.IsNullOrWhiteSpace loc -> None, None
                    | loc ->
                        let segs = loc.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                        let len = segs.Length

                        if len >= 4 && segs.[len - 2] = "_history" then
                            Some segs.[len - 3], Some segs.[len - 1]
                        elif len >= 2 then
                            Some segs.[len - 1], None
                        else
                            None, None

                baseReq BalpAuditEventBuilder.RestInteraction.Create (Some rt) createdId versionId false
            | "PUT", [| rt; id |] -> baseReq BalpAuditEventBuilder.RestInteraction.Update (Some rt) (Some id) None false
            | "PATCH", [| rt; id |] ->
                baseReq BalpAuditEventBuilder.RestInteraction.Patch (Some rt) (Some id) None false
            | "DELETE", [| rt; id |] ->
                let vid =
                    match ctx.Response.Headers.ETag.ToString() with
                    | s when String.IsNullOrWhiteSpace s -> None
                    | s -> Some(s.Trim().Trim('"'))

                baseReq BalpAuditEventBuilder.RestInteraction.Delete (Some rt) (Some id) vid false
            | _ -> None

    let useAudit (app: WebApplication) =
        let enabled = app.Configuration.GetValue<bool>("Audit:Enabled", false)

        if not enabled then
            app
        else
            app.Use(
                Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                    task {
                        do! next.Invoke(ctx)

                        match classify ctx with
                        | None -> ()
                        | Some req ->
                            let store = ctx.RequestServices.GetRequiredService<IResourceStore>()

                            let logger =
                                ctx.RequestServices
                                    .GetRequiredService<ILoggerFactory>()
                                    .CreateLogger("FhirServer.Audit")

                            let auditEvent = BalpAuditEventBuilder.build req
                            AuditRecorder.record store logger auditEvent
                    })
            )
            |> ignore

            app
