namespace FhirServer.Infrastructure.Http

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.OpenApi
open Swashbuckle.AspNetCore.SwaggerGen

/// Marker types so Swagger can emit distinct named schemas (not a blank object).
type FhirResourceDoc() = class end

type OperationOutcomeDoc() = class end

type BundleDoc() = class end

/// OpenAPI / Swagger metadata helpers for FHIR minimal APIs.
module OpenApiDocs =

    [<Literal>]
    let TagSystem = "System"

    [<Literal>]
    let TagMetadata = "Metadata"

    [<Literal>]
    let TagValidate = "Validate"

    [<Literal>]
    let TagCrud = "CRUD"

    [<Literal>]
    let TagSearch = "Search"

    [<Literal>]
    let TagHistory = "History"

    [<Literal>]
    let TagOperations = "Operations"

    [<Literal>]
    let TagBulk = "Bulk export"

    [<Literal>]
    let TagBundle = "Bundle"

    let private fhirJson = "application/fhir+json"
    let private ndjson = "application/fhir+ndjson"

    /// Which success / error responses to advertise for an endpoint.
    type ResponseProfile =
        | TextPlain
        | JsonOk
        | FhirRead
        | FhirSearch
        | FhirCreate
        | FhirUpdate
        | FhirDelete
        | FhirValidate
        | FhirBundle
        | NdjsonStream
        | BulkAccepted
        | NoContent

    let private prop (name: string) (schemaType: JsonSchemaType) (description: string) =
        let s = OpenApiSchema(Type = schemaType, Description = description)
        KeyValuePair(name, s :> IOpenApiSchema)

    let private fhirResourceSchema () =
        let required = HashSet<string>([ "resourceType" ])

        let properties =
            Dictionary<string, IOpenApiSchema>(
                [
                    prop "resourceType" JsonSchemaType.String "FHIR resource type (e.g. Patient, Observation)"
                    prop "id" JsonSchemaType.String "Logical id"
                    prop "meta" JsonSchemaType.Object "Metadata (versionId, lastUpdated, profile, …)"
                ]
            )

        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "Any FHIR resource (JSON object). Shape depends on resourceType.",
            Required = required,
            Properties = properties,
            AdditionalPropertiesAllowed = true,
            Example =
                JsonNode.Parse(
                    """{"resourceType":"Patient","id":"example","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}"""
                )
        )

    let private operationOutcomeSchema () =
        let required = HashSet<string>([ "resourceType" ])

        let issueRequired = HashSet<string>([ "severity"; "code" ])

        let issueProps =
            Dictionary<string, IOpenApiSchema>(
                [
                    prop "severity" JsonSchemaType.String "fatal | error | warning | information"
                    prop "code" JsonSchemaType.String "Issue type code (e.g. not-found, invalid)"
                    prop "diagnostics" JsonSchemaType.String "Human-readable diagnostics"
                ]
                |> dict
            )

        let issueItem =
            OpenApiSchema(
                Type = JsonSchemaType.Object,
                Required = issueRequired,
                Properties = issueProps,
                AdditionalPropertiesAllowed = true
            )

        let issueArray = OpenApiSchema(Type = JsonSchemaType.Array, Items = issueItem)

        let properties =
            Dictionary<string, IOpenApiSchema>(
                [
                    prop "resourceType" JsonSchemaType.String "Always OperationOutcome"
                    KeyValuePair("issue", issueArray :> IOpenApiSchema)
                ]
                |> dict
            )

        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "FHIR OperationOutcome — used for errors and $validate results.",
            Required = required,
            Properties = properties,
            AdditionalPropertiesAllowed = true,
            Example =
                JsonNode.Parse(
                    """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"not-found","diagnostics":"Patient/missing not found"}]}"""
                )
        )

    let private bundleSchema () =
        let required = HashSet<string>([ "resourceType"; "type" ])

        let properties =
            Dictionary<string, IOpenApiSchema>(
                [
                    prop "resourceType" JsonSchemaType.String "Always Bundle"
                    prop "type" JsonSchemaType.String "searchset | history | transaction | batch | …"
                    prop "total" JsonSchemaType.Integer "Total matches (search/history)"
                    prop "entry" JsonSchemaType.Array "Bundle entries"
                ]
                |> dict
            )

        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "FHIR Bundle (searchset, history, transaction response, …).",
            Required = required,
            Properties = properties,
            AdditionalPropertiesAllowed = true,
            Example =
                JsonNode.Parse(
                    """{"resourceType":"Bundle","type":"searchset","total":1,"entry":[{"resource":{"resourceType":"Patient","id":"example"}}]}"""
                )
        )

    let private produce (status: int) (responseType: Type) (contentType: string) (builder: RouteHandlerBuilder) =
        builder.Produces(status, responseType = responseType, contentType = contentType)

    let private produceEmpty (status: int) (builder: RouteHandlerBuilder) =
        builder.Produces(status)

    let private fhirErrors (builder: RouteHandlerBuilder) =
        builder
        |> produce StatusCodes.Status400BadRequest typeof<OperationOutcomeDoc> fhirJson
        |> produce StatusCodes.Status404NotFound typeof<OperationOutcomeDoc> fhirJson
        |> produce StatusCodes.Status409Conflict typeof<OperationOutcomeDoc> fhirJson
        |> produce StatusCodes.Status410Gone typeof<OperationOutcomeDoc> fhirJson
        |> produce StatusCodes.Status412PreconditionFailed typeof<OperationOutcomeDoc> fhirJson
        |> produce StatusCodes.Status422UnprocessableEntity typeof<OperationOutcomeDoc> fhirJson

    let private acceptsFhir (builder: RouteHandlerBuilder) =
        builder.Accepts(typeof<FhirResourceDoc>, false, fhirJson)

    let private applyProfile (profile: ResponseProfile) (builder: RouteHandlerBuilder) =
        match profile with
        | TextPlain -> produceEmpty StatusCodes.Status200OK builder
        | JsonOk ->
            builder.Produces(StatusCodes.Status200OK, responseType = typeof<obj>, contentType = "application/json")
        | FhirRead ->
            builder
            |> produce StatusCodes.Status200OK typeof<FhirResourceDoc> fhirJson
            |> fhirErrors
        | FhirSearch ->
            builder
            |> produce StatusCodes.Status200OK typeof<BundleDoc> fhirJson
            |> produce StatusCodes.Status400BadRequest typeof<OperationOutcomeDoc> fhirJson
            |> produce StatusCodes.Status404NotFound typeof<OperationOutcomeDoc> fhirJson
        | FhirCreate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status201Created typeof<FhirResourceDoc> fhirJson
            |> fhirErrors
        | FhirUpdate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK typeof<FhirResourceDoc> fhirJson
            |> fhirErrors
        | FhirDelete ->
            builder
            |> produce StatusCodes.Status200OK typeof<FhirResourceDoc> fhirJson
            |> produce StatusCodes.Status404NotFound typeof<OperationOutcomeDoc> fhirJson
            |> produce StatusCodes.Status410Gone typeof<OperationOutcomeDoc> fhirJson
            |> produce StatusCodes.Status412PreconditionFailed typeof<OperationOutcomeDoc> fhirJson
        | FhirValidate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK typeof<OperationOutcomeDoc> fhirJson
            |> produce StatusCodes.Status400BadRequest typeof<OperationOutcomeDoc> fhirJson
        | FhirBundle ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK typeof<BundleDoc> fhirJson
            |> fhirErrors
        | NdjsonStream ->
            builder
            |> fun b -> b.Produces(StatusCodes.Status200OK, responseType = typeof<string>, contentType = ndjson)
            |> produce StatusCodes.Status400BadRequest typeof<OperationOutcomeDoc> fhirJson
            |> produce StatusCodes.Status404NotFound typeof<OperationOutcomeDoc> fhirJson
        | BulkAccepted ->
            builder
            |> fun b ->
                b.Produces(StatusCodes.Status202Accepted, responseType = typeof<obj>, contentType = "application/json")
            |> produce StatusCodes.Status400BadRequest typeof<OperationOutcomeDoc> fhirJson
        | NoContent -> produceEmpty StatusCodes.Status204NoContent builder

    let private responseDescriptions =
        dict [
            StatusCodes.Status200OK, "OK — FHIR resource, Bundle, or OperationOutcome"
            StatusCodes.Status201Created, "Created — resource with Location / ETag"
            StatusCodes.Status202Accepted, "Accepted — async job started (Content-Location)"
            StatusCodes.Status204NoContent, "No Content"
            StatusCodes.Status400BadRequest, "Bad Request — OperationOutcome"
            StatusCodes.Status404NotFound, "Not Found — OperationOutcome"
            StatusCodes.Status409Conflict, "Conflict — OperationOutcome"
            StatusCodes.Status410Gone, "Gone (soft-deleted) — OperationOutcome"
            StatusCodes.Status412PreconditionFailed, "Precondition Failed (If-Match) — OperationOutcome"
            StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity (schema validation) — OperationOutcome"
        ]

    /// Adds human descriptions on each documented status code.
    type private ResponseDescriptionFilter() =
        interface IOperationFilter with
            member _.Apply(operation, _context) =
                if isNull operation.Responses then
                    ()
                else
                    for KeyValue(code, response) in operation.Responses do
                        match Int32.TryParse(code), response with
                        | (true, status), (:? OpenApiResponse as concrete) ->
                            match responseDescriptions.TryGetValue(status) with
                            | true, text when String.IsNullOrWhiteSpace concrete.Description ->
                                concrete.Description <- text
                            | _ -> ()
                        | _ -> ()

    /// Attach tags, summary, description and response/request OpenAPI metadata.
    let document
        (tags: string[])
        (summary: string)
        (description: string)
        (profile: ResponseProfile)
        (endpoint: RouteHandlerBuilder)
        =
        endpoint
            .WithTags(tags)
            .WithSummary(summary)
            .WithDescription(description)
        |> applyProfile profile

    let documentText tags summary description endpoint =
        document tags summary description TextPlain endpoint

    let documentJson tags summary description endpoint =
        document tags summary description JsonOk endpoint

    let documentFhir tags summary description endpoint =
        document tags summary description FhirRead endpoint

    let documentFhirSearch tags summary description endpoint =
        document tags summary description FhirSearch endpoint

    let documentFhirCreate tags summary description endpoint =
        document tags summary description FhirCreate endpoint

    let documentFhirUpdate tags summary description endpoint =
        document tags summary description FhirUpdate endpoint

    let documentFhirDelete tags summary description endpoint =
        document tags summary description FhirDelete endpoint

    let documentFhirValidate tags summary description endpoint =
        document tags summary description FhirValidate endpoint

    let documentFhirBundle tags summary description endpoint =
        document tags summary description FhirBundle endpoint

    let documentNdjson tags summary description endpoint =
        document tags summary description NdjsonStream endpoint

    let documentBulkAccepted tags summary description endpoint =
        document tags summary description BulkAccepted endpoint

    let documentNoContent tags summary description endpoint =
        document tags summary description NoContent endpoint

    /// SwaggerGen document info + Basic auth + named FHIR schemas with examples.
    let configureSwagger (options: SwaggerGenOptions) =
        let info = OpenApiInfo(Title = "FhirServer API", Version = "v1")

        info.Description <-
            "Open-source HL7 FHIR R5 server (F# / ASP.NET Core).\n\n"
            + "Bodies use **application/fhir+json** unless noted (NDJSON for `$dump` / bulk files).\n"
            + "Success bodies: **FhirResource** / **Bundle**. Errors and `$validate`: **OperationOutcome**.\n"
            + "Create → **201**; bulk `$export` kickoff → **202**; soft-deleted read → **410**."

        options.SwaggerDoc("v1", info)
        options.OperationFilter<ResponseDescriptionFilter>()

        let scheme =
            OpenApiSecurityScheme(
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "basic",
                In = ParameterLocation.Header,
                Description = "HTTP Basic (when Auth:Enabled=true)"
            )

        options.AddSecurityDefinition("basic", scheme)

        options.AddSecurityRequirement(fun document ->
            let requirement = OpenApiSecurityRequirement()
            requirement.Add(OpenApiSecuritySchemeReference("basic", document), ResizeArray<string>())
            requirement)

        options.MapType<FhirResourceDoc>(fun () -> fhirResourceSchema ())
        options.MapType<OperationOutcomeDoc>(fun () -> operationOutcomeSchema ())
        options.MapType<BundleDoc>(fun () -> bundleSchema ())
        // Legacy JsonObject mappings (if any endpoint still uses it).
        options.MapType<JsonObject>(fun () -> fhirResourceSchema ())
