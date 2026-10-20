namespace FhirServer.Infrastructure.Http

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.OpenApi
open Swashbuckle.AspNetCore.SwaggerGen

/// Marker types so Swagger emits distinct named schemas (not a blank object).
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

    [<Literal>]
    let TagOrgBac = "OrgBAC"

    let private fhirJson = "application/fhir+json"
    let private ndjson = "application/fhir+ndjson"

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

    let private props (items: (string * IOpenApiSchema) list) =
        let d = Dictionary<string, IOpenApiSchema>()

        for name, schema in items do
            d.[name] <- schema

        d

    let private strProp name description =
        name, OpenApiSchema(Type = JsonSchemaType.String, Description = description) :> IOpenApiSchema

    let private objProp name description =
        name, OpenApiSchema(Type = JsonSchemaType.Object, Description = description) :> IOpenApiSchema

    let private arrProp name description =
        name, OpenApiSchema(Type = JsonSchemaType.Array, Description = description) :> IOpenApiSchema

    let private intProp name description =
        name, OpenApiSchema(Type = JsonSchemaType.Integer, Description = description) :> IOpenApiSchema

    let private fhirResourceSchema () =
        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "Any FHIR resource (JSON object). Shape depends on resourceType.",
            Required = HashSet<string>([ "resourceType" ]),
            Properties =
                props [
                    strProp "resourceType" "FHIR resource type (e.g. Patient, Observation)"
                    strProp "id" "Logical id"
                    objProp "meta" "Metadata (versionId, lastUpdated, profile, …)"
                ],
            AdditionalPropertiesAllowed = true,
            Example =
                JsonNode.Parse(
                    """{"resourceType":"Patient","id":"example","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}"""
                )
        )

    let private operationOutcomeSchema () =
        let issueItem =
            OpenApiSchema(
                Type = JsonSchemaType.Object,
                Required = HashSet<string>([ "severity"; "code" ]),
                Properties =
                    props [
                        strProp "severity" "fatal | error | warning | information"
                        strProp "code" "Issue type code (e.g. not-found, invalid)"
                        strProp "diagnostics" "Human-readable diagnostics"
                    ],
                AdditionalPropertiesAllowed = true
            )

        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "FHIR OperationOutcome — used for errors and $validate results.",
            Required = HashSet<string>([ "resourceType" ]),
            Properties =
                props [
                    strProp "resourceType" "Always OperationOutcome"
                    "issue", OpenApiSchema(Type = JsonSchemaType.Array, Items = issueItem) :> IOpenApiSchema
                ],
            AdditionalPropertiesAllowed = true,
            Example =
                JsonNode.Parse(
                    """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"not-found","diagnostics":"Patient/missing not found"}]}"""
                )
        )

    let private bundleSchema () =
        OpenApiSchema(
            Type = JsonSchemaType.Object,
            Description = "FHIR Bundle (searchset, history, transaction response, …).",
            Required = HashSet<string>([ "resourceType"; "type" ]),
            Properties =
                props [
                    strProp "resourceType" "Always Bundle"
                    strProp "type" "searchset | history | transaction | batch | …"
                    intProp "total" "Total matches (search/history)"
                    arrProp "entry" "Bundle entries"
                ],
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

    let configureSwagger (options: SwaggerGenOptions) =
        let info = OpenApiInfo(Title = "FhirServer API", Version = "v1")

        info.Description <-
            "Open-source HL7 FHIR R5 server (F# / ASP.NET Core).\n\n"
            + "Bodies use **application/fhir+json** unless noted (NDJSON for `$dump` / bulk files).\n"
            + "Success: **FhirResource** / **Bundle**. Errors and `$validate`: **OperationOutcome**.\n"
            + "Create → **201**; bulk `$export` kickoff → **202**; soft-deleted read → **410**."

        options.SwaggerDoc("v1", info)
        options.OperationFilter<ResponseDescriptionFilter>()

        let basicScheme =
            OpenApiSecurityScheme(
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "basic",
                In = ParameterLocation.Header,
                Description =
                    "HTTP Basic — uses Auth:BasicScopes (default system/*.cruds); same SMART scope checks as Bearer"
            )

        let bearerScheme =
            OpenApiSecurityScheme(
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "opaque",
                In = ParameterLocation.Header,
                Description =
                    "SMART Bearer token from /auth/token. Enforces patient|user|system scopes (v2 c/r/u/d/s), patient compartment when applicable, and Organization tenant (exact on root API; hierarchical on /Organization/{orgId}/fhir)."
            )

        options.AddSecurityDefinition("basic", basicScheme)
        options.AddSecurityDefinition("bearer", bearerScheme)

        options.AddSecurityRequirement(fun document ->
            let requirement = OpenApiSecurityRequirement()
            requirement.Add(OpenApiSecuritySchemeReference("basic", document), ResizeArray<string>())
            requirement.Add(OpenApiSecuritySchemeReference("bearer", document), ResizeArray<string>())
            requirement)

        options.MapType<FhirResourceDoc>(fun () -> fhirResourceSchema ())
        options.MapType<OperationOutcomeDoc>(fun () -> operationOutcomeSchema ())
        options.MapType<BundleDoc>(fun () -> bundleSchema ())
        options.MapType<JsonObject>(fun () -> fhirResourceSchema ())
