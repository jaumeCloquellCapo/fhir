namespace FhirServer.Infrastructure.Http

open System
open System.Text.Json.Nodes
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.OpenApi
open Swashbuckle.AspNetCore.SwaggerGen

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
    let private fhirBody = typeof<JsonObject>

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

    let private produce (status: int) (contentType: string) (builder: RouteHandlerBuilder) =
        builder.Produces(status, responseType = fhirBody, contentType = contentType)

    let private produceEmpty (status: int) (builder: RouteHandlerBuilder) =
        builder.Produces(status)

    let private fhirErrors (builder: RouteHandlerBuilder) =
        builder
        |> produce StatusCodes.Status400BadRequest fhirJson
        |> produce StatusCodes.Status404NotFound fhirJson
        |> produce StatusCodes.Status409Conflict fhirJson
        |> produce StatusCodes.Status410Gone fhirJson
        |> produce StatusCodes.Status412PreconditionFailed fhirJson
        |> produce StatusCodes.Status422UnprocessableEntity fhirJson

    let private acceptsFhir (builder: RouteHandlerBuilder) =
        builder.Accepts(fhirBody, false, fhirJson)

    let private applyProfile (profile: ResponseProfile) (builder: RouteHandlerBuilder) =
        match profile with
        | TextPlain -> produceEmpty StatusCodes.Status200OK builder
        | JsonOk ->
            builder.Produces(StatusCodes.Status200OK, responseType = typeof<obj>, contentType = "application/json")
        | FhirRead ->
            builder
            |> produce StatusCodes.Status200OK fhirJson
            |> fhirErrors
        | FhirSearch ->
            builder
            |> produce StatusCodes.Status200OK fhirJson
            |> produce StatusCodes.Status400BadRequest fhirJson
            |> produce StatusCodes.Status404NotFound fhirJson
        | FhirCreate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status201Created fhirJson
            |> fhirErrors
        | FhirUpdate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK fhirJson
            |> fhirErrors
        | FhirDelete ->
            builder
            |> produce StatusCodes.Status200OK fhirJson
            |> produce StatusCodes.Status404NotFound fhirJson
            |> produce StatusCodes.Status410Gone fhirJson
            |> produce StatusCodes.Status412PreconditionFailed fhirJson
        | FhirValidate ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK fhirJson
            |> produce StatusCodes.Status400BadRequest fhirJson
        | FhirBundle ->
            builder
            |> acceptsFhir
            |> produce StatusCodes.Status200OK fhirJson
            |> fhirErrors
        | NdjsonStream ->
            builder
            |> fun b -> b.Produces(StatusCodes.Status200OK, responseType = typeof<string>, contentType = ndjson)
            |> produce StatusCodes.Status400BadRequest fhirJson
            |> produce StatusCodes.Status404NotFound fhirJson
        | BulkAccepted ->
            builder
            |> fun b ->
                b.Produces(StatusCodes.Status202Accepted, responseType = typeof<obj>, contentType = "application/json")
            |> produce StatusCodes.Status400BadRequest fhirJson
        | NoContent -> produceEmpty StatusCodes.Status204NoContent builder

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

    // Convenience wrappers (keep call sites readable).
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

    /// SwaggerGen document info + Basic auth + FHIR body schema hint.
    let configureSwagger (options: SwaggerGenOptions) =
        let info = OpenApiInfo(Title = "FhirServer API", Version = "v1")

        info.Description <-
            "Open-source HL7 FHIR R5 server (F# / ASP.NET Core).\n\n"
            + "Bodies use **application/fhir+json** unless noted (NDJSON for `$dump` / bulk files).\n"
            + "Error responses return an **OperationOutcome** with the same content type.\n"
            + "Create → **201**; bulk `$export` kickoff → **202**; soft-deleted read → **410**."

        options.SwaggerDoc("v1", info)

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

        // JsonObject → free-form FHIR resource object in the UI.
        options.MapType<JsonObject>(fun () ->
            let schema = OpenApiSchema()
            schema.Type <- JsonSchemaType.Object
            schema.AdditionalPropertiesAllowed <- true
            schema.Description <- "FHIR resource or OperationOutcome (JSON object)"
            schema)
