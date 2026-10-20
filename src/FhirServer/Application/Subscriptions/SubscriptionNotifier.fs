namespace FhirServer.Application.Subscriptions

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open FhirServer.Domain
open FhirServer.Domain.Ports
open FhirServer.Domain.Types

/// Rest-hook notifications for active R5 Subscription resources (MVP).
module SubscriptionNotifier =

    let private http =
        let c = new HttpClient()
        c.Timeout <- TimeSpan.FromSeconds(5.0)
        c

    let private hookCapture = ConcurrentDictionary<string, ConcurrentBag<string>>()

    /// Set from composition root (optional). When None, remote hooks are denied.
    let mutable private allowRemoteHooks = false

    let configure (config: IConfiguration) =
        allowRemoteHooks <- config.GetValue<bool>("Subscriptions:AllowRemoteHooks", false)

    let captureHook (sinkId: string) (body: string) =
        let bag = hookCapture.GetOrAdd(sinkId, fun _ -> ConcurrentBag<string>())
        bag.Add(body)

    let listCaptured (sinkId: string) =
        match hookCapture.TryGetValue sinkId with
        | true, bag -> bag |> Seq.toList
        | _ -> []

    let clearCaptured (sinkId: string) = hookCapture.TryRemove(sinkId) |> ignore

    let private topicMatches (topic: string) (resourceType: string) =
        String.IsNullOrWhiteSpace topic
        || topic.Contains(resourceType, StringComparison.OrdinalIgnoreCase)
        || topic.EndsWith("/Patient", StringComparison.OrdinalIgnoreCase)
           && resourceType = "Patient"
        || topic.Contains("*", StringComparison.Ordinal)

    let private isRestHook (subscription: FhirResource) =
        match subscription["channelType"] with
        | :? JsonObject as coding ->
            match FhirJson.propString coding "code" with
            | Some code -> code.Equals("rest-hook", StringComparison.OrdinalIgnoreCase)
            | None -> false
        | _ -> false

    let private endpointOf (subscription: FhirResource) =
        FhirJson.propString subscription "endpoint"

    let private tryHookSinkId (endpoint: string) =
        let marker = "/_hooks/"
        let idx = endpoint.IndexOf(marker, StringComparison.OrdinalIgnoreCase)

        if idx < 0 then
            None
        else
            let rest = endpoint.Substring(idx + marker.Length).Trim('/')
            let id = rest.Split('/', StringSplitOptions.RemoveEmptyEntries)
            if id.Length > 0 then Some id.[0] else None

    /// Block SSRF: only in-process /_hooks sinks, or loopback hosts when AllowRemoteHooks=true.
    let private isSafeRemoteEndpoint (endpoint: string) =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> false
        | true, uri ->
            let schemeOk =
                uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)

            if not schemeOk then
                false
            else
                let host = uri.Host

                host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                || host.Equals("::1", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)

    let private buildNotification (subscription: FhirResource) (interaction: string) (resource: FhirResource) =
        let subId = FhirJson.id subscription
        let rt = FhirJson.resourceType resource
        let rid = FhirJson.id resource
        let now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")

        let status =
            FhirJson.mkObject [
                "resourceType", FhirJson.jStr "SubscriptionStatus"
                "status", FhirJson.jStr "active"
                "type", FhirJson.jStr "event-notification"
                "notificationEvent",
                JsonArray(
                    FhirJson.mkObject [
                        "eventNumber", FhirJson.jStr "1"
                        "timestamp", FhirJson.jStr now
                        "focus", FhirJson.mkObject [ "reference", FhirJson.jStr $"{rt}/{rid}" ]
                    ]
                )
                "subscription", FhirJson.mkObject [ "reference", FhirJson.jStr $"Subscription/{subId}" ]
                "topic",
                FhirJson.jStr (
                    FhirJson.propString subscription "topic"
                    |> Option.defaultValue "http://fhirserver.local/SubscriptionTopic/all"
                )
            ]

        let content =
            FhirJson.propString subscription "content"
            |> Option.defaultValue "full-resource"

        let bundle = FhirJson.emptyBundle "subscription-notification" 0
        bundle.Remove("total") |> ignore

        ignore (FhirJson.addBundleEntry bundle (FhirJson.mkObject [ "resource", status ]))

        if content <> "empty" then
            let payload =
                if content = "id-only" then
                    FhirJson.mkObject [ "resourceType", FhirJson.jStr rt; "id", FhirJson.jStr rid ]
                else
                    FhirJson.deepClone resource

            ignore (
                FhirJson.addBundleEntry
                    bundle
                    (FhirJson.mkObject [
                        "fullUrl", FhirJson.jStr $"{rt}/{rid}"
                        "resource", payload
                        "request",
                        FhirJson.mkObject [
                            "method", FhirJson.jStr (interaction.ToUpperInvariant())
                            "url", FhirJson.jStr $"{rt}/{rid}"
                        ]
                    ])
            )

        bundle

    let private deliver (endpoint: string) (payload: string) =
        match tryHookSinkId endpoint with
        | Some id ->
            captureHook id payload
            true
        | None when not allowRemoteHooks -> false
        | None when not (isSafeRemoteEndpoint endpoint) -> false
        | None ->
            try
                use content = new StringContent(payload, Encoding.UTF8, "application/fhir+json")
                // Local MVP: short timeout; remote non-loopback already rejected.
                http.PostAsync(endpoint, content).GetAwaiter().GetResult().IsSuccessStatusCode
            with
            | :? HttpRequestException
            | :? TaskCanceledException
            | :? SocketException -> false
            | _ -> false

    /// Notify active rest-hook subscriptions whose topic matches the resource type.
    let notifyChange (store: IResourceStore) (interaction: string) (resource: FhirResource) =
        if not (ResourceCatalog.isSupported "Subscription") then
            ()
        else
            let resourceType = FhirJson.resourceType resource

            // Avoid notification loops on Subscription / SubscriptionStatus writes.
            if resourceType = "Subscription" || resourceType = "SubscriptionStatus" then
                ()
            else
                try
                    let subscriptions =
                        store.ListByType "Subscription"
                        |> List.filter (fun s -> FhirJson.propString s "status" = Some "active")

                    for subscription in subscriptions do
                        let topic = FhirJson.propString subscription "topic" |> Option.defaultValue ""

                        if isRestHook subscription && topicMatches topic resourceType then
                            match endpointOf subscription with
                            | None
                            | Some "" -> ()
                            | Some endpoint ->
                                let notification = buildNotification subscription interaction resource
                                deliver endpoint (FhirJson.toJson notification) |> ignore
                with _ ->
                    ()
