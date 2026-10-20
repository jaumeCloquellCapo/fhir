namespace FhirServer.Application

/// Compatibility facade — prefer ResourceCrud / ResourceSearch / … modules for new code.
module ResourceHandlers =

    let read = ResourceCrud.read
    let vread = ResourceCrud.vread
    let create = ResourceCrud.create
    let update = ResourceCrud.update
    let delete = ResourceCrud.delete
    let validateRaw = ResourceCrud.validateRaw
    let conditionalCreate = ResourceCrud.conditionalCreate
    let conditionalUpdate = ResourceCrud.conditionalUpdate
    let applyJsonPatch = ResourceCrud.applyJsonPatch

    let search = ResourceSearch.search
    let observationLastN = ResourceSearch.observationLastN

    let history = ResourceHistory.history
    let historyFromQuery = ResourceHistory.historyFromQuery

    let patientEverything = ResourceOperations.patientEverything
    let patientDocument = ResourceOperations.patientDocument
    let dump = ResourceOperations.dump

    let executeBundle = ResourceBundle.executeBundle
