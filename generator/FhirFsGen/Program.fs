open System
open System.IO
open FhirFsGen
open FhirFsGen.Ir

module Cli =

    type Options =
        { PackageDir: string option
          CacheDir: string
          OutDir: string
          Resources: string list
          All: bool
          RefreshTodo: bool
          SchemaOnly: bool }

    let private usage () =
        """
FhirFsGen — generate FHIR Schema JSON + Firely-free F# validator from R5 StructureDefinitions.

Source: official package hl7.fhir.r5.core@5.0.0 (packages.fhir.org). No Firely / Hl7.Fhir.* deps.

Usage:
  dotnet run --project generator/FhirFsGen -- [options]

Options:
  --out <dir>           Output directory (default: generator/out/r5)
  --cache <dir>         Package cache (default: generator/cache)
  --package-dir <dir>   Use an already-extracted package/ folder
  --resource <Name>     Generate one resource (repeatable). Default: Patient
  --all                 Generate every concrete resource in the package
  --schema-only         Write schemas/<Type>.schema.json only (parallel-safe)
  --todo                Rewrite generator/TODO-R5.md from existing schemas on disk
  --help                Show this help
"""

    let parse (args: string[]) : Options =
        let rec loop i acc =
            if i >= args.Length then
                acc
            else
                match args.[i] with
                | "--help"
                | "-h" ->
                    printf "%s" (usage ())
                    Environment.Exit 0
                    acc
                | "--out" when i + 1 < args.Length ->
                    loop (i + 2) { acc with OutDir = args.[i + 1] }
                | "--cache" when i + 1 < args.Length ->
                    loop (i + 2) { acc with CacheDir = args.[i + 1] }
                | "--package-dir" when i + 1 < args.Length ->
                    loop (i + 2) { acc with PackageDir = Some args.[i + 1] }
                | "--resource" when i + 1 < args.Length ->
                    loop (i + 2) { acc with Resources = acc.Resources @ [ args.[i + 1] ] }
                | "--all" -> loop (i + 1) { acc with All = true }
                | "--todo" -> loop (i + 1) { acc with RefreshTodo = true }
                | "--schema-only" -> loop (i + 1) { acc with SchemaOnly = true }
                | other -> failwith $"Unknown argument '{other}'. Use --help."

        let defaults =
            { PackageDir = None
              CacheDir = Path.Combine("generator", "cache")
              OutDir = Path.Combine("generator", "out", "r5")
              Resources = []
              All = false
              RefreshTodo = false
              SchemaOnly = false }

        let opts = loop 0 defaults

        if opts.All then opts
        elif opts.Resources = [] && not opts.RefreshTodo then
            { opts with Resources = [ "Patient" ] }
        else
            opts

[<EntryPoint>]
let main args =
    let opts = Cli.parse args
    let fhirVersion = PackageCache.defaultPackageVersion

    let repoRoot =
        let cwd = Directory.GetCurrentDirectory()

        if Directory.Exists(Path.Combine(cwd, "generator")) then
            cwd
        else
            let start = DirectoryInfo(AppContext.BaseDirectory)

            let rec find (d: DirectoryInfo) =
                if isNull d then cwd
                elif Directory.Exists(Path.Combine(d.FullName, "generator")) then
                    d.FullName
                else
                    find d.Parent

            find start

    let abs (p: string) =
        if Path.IsPathRooted p then p
        else Path.GetFullPath(Path.Combine(repoRoot, p))

    let cacheDir = abs opts.CacheDir
    let outDir = abs opts.OutDir
    let todoPath = Path.Combine(repoRoot, "generator", "TODO-R5.md")
    let schemasDir = Path.Combine(outDir, "schemas")
    let profilesDir = Path.Combine(schemasDir, "profiles")

    let packageDir =
        match opts.PackageDir with
        | Some d -> abs d
        | None ->
            PackageCache.ensurePackage
                cacheDir
                PackageCache.defaultPackageId
                PackageCache.defaultPackageVersion

    printfn "Package: %s" packageDir
    let allResourceTypes = StructureLoader.listConcreteResourceTypes packageDir
    let allComplexTypes = StructureLoader.listConcreteComplexTypes packageDir
    let allProfiles = StructureLoader.listConstraintProfiles packageDir
    let resourceTypeSet = Set.ofList allResourceTypes

    let schemaNamesOnDisk () =
        if not (Directory.Exists schemasDir) then
            Set.empty
        else
            Directory.EnumerateFiles(schemasDir, "*.schema.json")
            |> Seq.map (fun f -> Path.GetFileNameWithoutExtension(f).Replace(".schema", ""))
            |> Set.ofSeq

    let generateOne (name: string) =
        printfn "Generating %s..." name
        let sd = StructureLoader.loadStructureDefinition packageDir name
        let schema = SnapshotToIr.fromStructureDefinition fhirVersion sd
        let schemaPath = Path.Combine(schemasDir, $"{name}.schema.json")
        EmitSchemaJson.writeResource schema schemaPath
        printfn "  -> %s (%d top-level elements, kind=%s)" schemaPath schema.Elements.Count schema.Kind
        schema

    let generateProfile (pref: ConstraintProfileRef) =
        printfn "Generating profile %s..." pref.Id
        let sd = StructureLoader.loadStructureDefinition packageDir pref.Id
        let schema = SnapshotToIr.fromStructureDefinition fhirVersion sd
        let schemaPath = Path.Combine(profilesDir, $"{pref.Id}.schema.json")
        EmitSchemaJson.writeResource schema schemaPath
        printfn "  -> %s (url=%s, type=%s)" schemaPath schema.Url schema.TypeName
        schema

    if opts.RefreshTodo && not opts.All && opts.Resources = [] then
        let doneSet = schemaNamesOnDisk () |> Set.filter resourceTypeSet.Contains
        EmitFsharp.writeTodoMarkdown allResourceTypes doneSet todoPath
        printfn "Wrote %s (%d / %d)" todoPath doneSet.Count allResourceTypes.Length
        0
    else
        let targets =
            if opts.All then allResourceTypes
            else opts.Resources |> List.distinct

        Directory.CreateDirectory schemasDir |> ignore

        let generatedResources = targets |> List.map generateOne

        if opts.SchemaOnly then
            printfn "Schema-only done (%d file(s))." generatedResources.Length
            0
        else
            // Always emit concrete complex datatypes so the validator can resolve nested types.
            let generatedDatatypes = allComplexTypes |> List.map generateOne

            // Core constraint profiles (indexed by canonical URL at runtime).
            Directory.CreateDirectory profilesDir |> ignore
            let generatedProfiles = allProfiles |> List.map generateProfile

            let loadSchemaByName (name: string) =
                let sd = StructureLoader.loadStructureDefinition packageDir name
                SnapshotToIr.fromStructureDefinition fhirVersion sd

            let resourceSchemasOnDisk =
                schemaNamesOnDisk ()
                |> Set.filter resourceTypeSet.Contains
                |> Set.toList
                |> List.sort
                |> List.map (fun name ->
                    match generatedResources |> List.tryFind (fun s -> s.TypeName = name) with
                    | Some s -> s
                    | None -> loadSchemaByName name)

            let datatypeSchemas =
                allComplexTypes
                |> List.map (fun name ->
                    match generatedDatatypes |> List.tryFind (fun s -> s.TypeName = name) with
                    | Some s -> s
                    | None -> loadSchemaByName name)

            let profileIndexEntries =
                generatedProfiles
                |> List.map (fun s ->
                    let fileName =
                        match allProfiles |> List.tryFind (fun p -> p.Url = s.Url) with
                        | Some p -> $"{p.Id}.schema.json"
                        | None ->
                            match s.Url.LastIndexOf('/') with
                            | -1 -> $"{s.TypeName}.schema.json"
                            | i -> $"{s.Url.Substring(i + 1)}.schema.json"

                    s.Url, fileName)

            EmitSchemaJson.writeProfilesIndex
                profileIndexEntries
                (Path.Combine(outDir, "profiles-index.json"))

            let manifest =
                { FhirVersion = fhirVersion
                  GeneratedAtUtc = DateTime.UtcNow.ToString("o")
                  Resources = resourceSchemasOnDisk }

            EmitSchemaJson.writeManifest manifest (Path.Combine(outDir, "manifest.json"))
            EmitFsharp.writeCatalog manifest (Path.Combine(outDir, "GeneratedResourceCatalog.fs"))
            EmitValidator.writeSchemaValidatorFs (Path.Combine(outDir, "GeneratedSchemaValidator.fs"))

            let bindingUrls =
                ValueSetLoader.collectRequiredBindings (
                    resourceSchemasOnDisk @ datatypeSchemas @ generatedProfiles
                )

            let valueSets = ValueSetLoader.resolveMany packageDir bindingUrls
            EmitSchemaJson.writeValueSets valueSets (Path.Combine(outDir, "valuesets.json"))
            printfn "ValueSets: %d resolved / %d required bindings" valueSets.Length bindingUrls.Length
            printfn "Datatypes: %d complex-type schemas" datatypeSchemas.Length
            printfn "Profiles: %d constraint schemas" generatedProfiles.Length

            let doneSet = Set.ofList (resourceSchemasOnDisk |> List.map (fun s -> s.TypeName))
            EmitFsharp.writeTodoMarkdown allResourceTypes doneSet todoPath

            printfn "Done. Output: %s" outDir
            printfn "TODO: %s (%d / %d)" todoPath doneSet.Count allResourceTypes.Length
            0
