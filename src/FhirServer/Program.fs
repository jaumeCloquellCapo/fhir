namespace FhirServer

module Program =

    [<EntryPoint>]
    let main args =
        let app = AppHost.create args
        app.Run()
        0
