namespace FhirServer.Infrastructure.Http

open System
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

    /// Attach tags, summary, description and Produces metadata.
    let document
        (tags: string[])
        (summary: string)
        (description: string)
        (produces: string list)
        (endpoint: RouteHandlerBuilder)
        =
        let mutable builder =
            endpoint
                .WithTags(tags)
                .WithSummary(summary)
                .WithDescription(description)

        for contentType in produces do
            builder <- builder.Produces(StatusCodes.Status200OK, responseType = typeof<obj>, contentType = contentType)

        builder

    let documentFhir tags summary description endpoint =
        document tags summary description [ fhirJson ] endpoint

    let documentNdjson tags summary description endpoint =
        document tags summary description [ ndjson ] endpoint

    let documentJson tags summary description endpoint =
        document tags summary description [ "application/json" ] endpoint

    let documentText tags summary description endpoint =
        document tags summary description [ "text/plain" ] endpoint

    /// SwaggerGen document info + Basic auth scheme (matches Auth middleware).
    let configureSwagger (options: SwaggerGenOptions) =
        let info = OpenApiInfo(Title = "FhirServer API", Version = "v1")

        info.Description <-
            "Open-source HL7 FHIR R5 server (F# / ASP.NET Core). "
            + "Bodies use application/fhir+json unless noted (NDJSON for $dump / bulk files)."

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
            requirement.Add(OpenApiSecuritySchemeReference("basic", document), Array.Empty<string>())
            requirement)
