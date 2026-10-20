namespace FhirServer

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open FhirServer.Domain.Services
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Application.Subscriptions
open FhirServer.Infrastructure.Http
open FhirServer.Infrastructure.Http.HttpRequestHelpers
open FhirServer.Infrastructure.Http.Routes
open FhirServer.Infrastructure.Persistence
open FhirServer.Infrastructure.Persistence.Postgres

/// Composition root (also used by E2E WebApplicationFactory).
module AppHost =

    let createStore (config: IConfiguration) : IResourceStore =
        let cs =
            match config.["ConnectionStrings:Fhir"] with
            | null
            | "" ->
                invalidOp
                    "ConnectionStrings:Fhir is required. Set it in .env.dev / .env (ConnectionStrings__Fhir) or configuration."
            | value -> value

        EnvelopeResourceStore(PostgresEnvelopeDialect(cs)) :> IResourceStore

    /// Register HTTP endpoints. Order matters (specific routes before `/{resourceType}`).
    let mapRoutes (app: WebApplication) =
        SystemRoutes.map app
        SmartAuthRoutes.map app
        HookRoutes.map app
        BulkRoutes.map app
        HistoryBundleRoutes.map app
        TerminologyRoutes.map app
        OperationRoutes.map app
        CrudRoutes.map app
        OrgBacRoutes.map app
        app

    let create (args: string[]) =
        // Repo root (…/FhirServer → …/) then content root after builder exists.
        let startDir = Directory.GetCurrentDirectory()
        DotEnv.loadFromDirectory startDir
        DotEnv.loadFromDirectory (Path.GetFullPath(Path.Combine(startDir, "..")))

        let builder = WebApplication.CreateBuilder(args)

        DotEnv.loadFromDirectory builder.Environment.ContentRootPath

        builder.Services.Configure<KestrelServerOptions>(fun (options: KestrelServerOptions) ->
            options.Limits.MaxRequestBodySize <- maxRequestBodyBytes)
        |> ignore

        builder.Services.AddHttpContextAccessor() |> ignore

        builder.Services.AddSingleton<IResourceStore>(fun sp ->
            createStore (sp.GetRequiredService<IConfiguration>()))
        |> ignore

        builder.Services.AddEndpointsApiExplorer() |> ignore
        builder.Services.AddSwaggerGen(Action<_> OpenApiDocs.configureSwagger) |> ignore

        let app = builder.Build()

        let httpAccessor = app.Services.GetRequiredService<IHttpContextAccessor>()

        TenantContext.configureHttpResolver (fun () ->
            match httpAccessor.HttpContext with
            | null -> None
            | ctx ->
                match ctx.Items.TryGetValue(TenantContext.HttpItemKey) with
                | true, (:? TenantAccess as access) -> Some access
                | _ -> None)

        let subscriptionLog =
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FhirServer.Subscriptions")

        SubscriptionNotifier.configure app.Configuration subscriptionLog

        if app.Environment.IsDevelopment() then
            app.UseSwagger() |> ignore

            app.UseSwaggerUI(fun options ->
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "FhirServer v1")
                options.DocumentTitle <- "FhirServer API"
                options.RoutePrefix <- "swagger")
            |> ignore

        Auth.useTenantScope app |> ignore
        Auth.useAuth app |> ignore
        Auth.useOrgBacPath app |> ignore
        AuditMiddleware.useAudit app |> ignore
        mapRoutes app

/// Marker for Microsoft.AspNetCore.Mvc.Testing WebApplicationFactory.
type Marker = class end
