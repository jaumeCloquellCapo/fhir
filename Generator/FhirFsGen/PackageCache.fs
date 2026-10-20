namespace FhirFsGen

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open System.Net.Http

/// Downloads and extracts official FHIR NPM packages (no Firely).
module PackageCache =

    let defaultPackageId = "hl7.fhir.r5.core"
    let defaultPackageVersion = "5.0.0"

    let private registryUrl (packageId: string) (version: string) =
        $"https://packages.fhir.org/{packageId}/{version}"

    let private extractTgz (tgzPath: string) (extractDir: string) =
        Directory.CreateDirectory extractDir |> ignore
        use fs = File.OpenRead tgzPath
        use gzip = new GZipStream(fs, CompressionMode.Decompress)
        TarFile.ExtractToDirectory(gzip, extractDir, overwriteFiles = true)

    let ensurePackage (cacheRoot: string) (packageId: string) (version: string) =
        let extractDir = Path.Combine(cacheRoot, $"{packageId}-{version}")
        let packageDir = Path.Combine(extractDir, "package")
        let tgzPath = Path.Combine(cacheRoot, $"{packageId}-{version}.tgz")

        Directory.CreateDirectory cacheRoot |> ignore

        if not (Directory.Exists packageDir) then
            if not (File.Exists tgzPath) then
                printfn "Downloading %s@%s ..." packageId version
                use client = new HttpClient()
                client.Timeout <- TimeSpan.FromMinutes 5.0

                let bytes =
                    client.GetByteArrayAsync(registryUrl packageId version).GetAwaiter().GetResult()

                File.WriteAllBytes(tgzPath, bytes)
                printfn "  saved %s (%d bytes)" tgzPath bytes.Length

            if Directory.Exists extractDir then
                Directory.Delete(extractDir, true)

            extractTgz tgzPath extractDir

        if not (Directory.Exists packageDir) then
            failwith $"Package folder missing after extract: {packageDir}"

        packageDir
