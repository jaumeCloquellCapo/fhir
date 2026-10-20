module FhirServer.E2E.Endpoints.SearchCodeCompositeTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert
open FhirServer.E2E.SearchTestHelpers

/// Token hierarchy (`below`/`above`) and composite `code-value-quantity`.
type SearchCodeCompositeTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let mustCreate = SearchTestHelpers.mustCreate client

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``code below and above use hierarchical prefixes``() : Task =
        task {
            let! patientId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "TokHier", "given": ["Raul"] }],
                      "gender": "male"
                    }
                    """

            let! childId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://snomed.info/sct",
                          "code": "38341003.1",
                          "display": "Hypertensive disorder - essential"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }}
                    }}
                    """

            let! parentId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://snomed.info/sct",
                          "code": "38341003",
                          "display": "Hypertensive disorder"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }}
                    }}
                    """

            let! otherId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://snomed.info/sct",
                          "code": "44054006",
                          "display": "Diabetes mellitus type 2"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }}
                    }}
                    """

            let! belowStatus, belowBody =
                get
                    client
                    (sprintf
                        "/Observation?patient=%s&code%%3Abelow=http://snomed.info/sct|38341003"
                        patientId)

            assertStatus HttpStatusCode.OK belowStatus
            assertContainsId belowBody childId
            assertContainsId belowBody parentId
            assertNotContainsId belowBody otherId

            let! aboveStatus, aboveBody =
                get
                    client
                    (sprintf
                        "/Observation?patient=%s&code%%3Aabove=http://snomed.info/sct|38341003.1"
                        patientId)

            assertStatus HttpStatusCode.OK aboveStatus
            assertContainsId aboveBody parentId
            assertContainsId aboveBody childId
            assertNotContainsId aboveBody otherId
        }

    [<Fact>]
    member _.``composite code-value-quantity matches systolic over 100``() : Task =
        task {
            let! patientId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Composite", "given": ["Laura"] }],
                      "gender": "female",
                      "birthDate": "1970-02-14"
                    }
                    """

            let! matchId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "8480-6",
                          "display": "Systolic blood pressure"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-03-01T08:00:00Z",
                      "valueQuantity": {{
                        "value": 120,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! wrongCodeId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "8462-4",
                          "display": "Diastolic blood pressure"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-03-01T08:01:00Z",
                      "valueQuantity": {{
                        "value": 120,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! wrongQtyId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "8480-6",
                          "display": "Systolic blood pressure"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-03-01T08:02:00Z",
                      "valueQuantity": {{
                        "value": 80,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! status, body =
                get
                    client
                    (sprintf
                        "/Observation?patient=%s&code-value-quantity=http://loinc.org|8480-6$gt100|http://unitsofmeasure.org|mm[Hg]"
                        patientId)

            assertStatus HttpStatusCode.OK status
            assertContainsId body matchId
            assertNotContainsId body wrongCodeId
            assertNotContainsId body wrongQtyId
        }
