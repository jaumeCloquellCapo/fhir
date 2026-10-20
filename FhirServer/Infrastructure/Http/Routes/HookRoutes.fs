namespace FhirServer.Infrastructure.Http.Routes

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports
open FhirServer.Application
open FhirServer.Infrastructure.Http
open FhirServer.Infrastructure.Capability
open System.Text.Json.Nodes
open FhirServer.Application.Subscriptions

/// Local subscription capture sink /_hooks/{sinkId}.
module HookRoutes =

    let map (app: WebApplication) =
        app.MapPost(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    let! body = HttpRequestHelpers.readBody ctx
                    SubscriptionNotifier.captureHook sinkId body
                    ctx.Response.StatusCode <- StatusCodes.Status202Accepted
                })
        )
        |> ignore

        app.MapGet(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    let payloads = SubscriptionNotifier.listCaptured sinkId
                    let arr = JsonArray()

                    for p in payloads do
                        match JsonNode.Parse(p) with
                        | null -> arr.Add(FhirJson.jStr p)
                        | node -> arr.Add(node)

                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsync(arr.ToJsonString())
                })
        )
        |> ignore

        app.MapDelete(
            "/_hooks/{sinkId}",
            Func<HttpContext, string, Task<unit>>(fun ctx sinkId ->
                task {
                    SubscriptionNotifier.clearCaptured sinkId
                    ctx.Response.StatusCode <- StatusCodes.Status204NoContent
                })
        )
        |> ignore
