module FhirServer.E2E.Endpoints.SearchDateQuantityTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert
open FhirServer.E2E.SearchTestHelpers

/// Date / quantity search: `ge`, `ap`, `value-quantity`.
type SearchDateQuantityTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let mustCreate = SearchTestHelpers.mustCreate client

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``birthdate ge filters patients``() : Task =
        task {
            let! _, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Vega", "given": ["Carmen"] }],
                      "gender": "female",
                      "birthDate": "1980-01-01"
                    }
                    """

            let! youngId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Ruiz", "given": ["Diego"] }],
                      "gender": "male",
                      "birthDate": "2000-06-15"
                    }
                    """

            let! status, body = get client "/Patient?birthdate=ge1990-01-01&family=Ruiz"
            assertStatus HttpStatusCode.OK status
            assertContainsId body youngId
            Assert.DoesNotContain("\"family\":\"Vega\"", body)
        }

    [<Fact>]
    member _.``value-quantity filters heart rate observations``() : Task =
        task {
            let! patientId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Pulso", "given": ["Elena"] }],
                      "gender": "female",
                      "birthDate": "1975-11-20"
                    }
                    """

            let! hiId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "category": [{{
                        "coding": [{{
                          "system": "http://terminology.hl7.org/CodeSystem/observation-category",
                          "code": "vital-signs",
                          "display": "Vital Signs"
                        }}]
                      }}],
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "8867-4",
                          "display": "Heart rate"
                        }}],
                        "text": "Heart rate"
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-06-01T10:00:00Z",
                      "valueQuantity": {{
                        "value": 120,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! loId, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "category": [{{
                        "coding": [{{
                          "system": "http://terminology.hl7.org/CodeSystem/observation-category",
                          "code": "vital-signs"
                        }}]
                      }}],
                      "code": {{
                        "coding": [{{ "system": "http://loinc.org", "code": "8867-4", "display": "Heart rate" }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-06-01T11:00:00Z",
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
                        "/Observation?patient=%s&value-quantity=gt100|http://unitsofmeasure.org|mm[Hg]"
                        patientId)

            assertStatus HttpStatusCode.OK status
            assertContainsId body hiId
            assertNotContainsId body loId
        }

    [<Fact>]
    member _.``birthdate ap matches nearby dates``() : Task =
        task {
            let! nearId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "ApNear", "given": ["Tina"] }],
                      "gender": "female",
                      "birthDate": "1990-01-02"
                    }
                    """

            let! farId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "ApFar", "given": ["Toni"] }],
                      "gender": "male",
                      "birthDate": "2000-01-01"
                    }
                    """

            let! status, body = get client "/Patient?birthdate=ap1990-01-01&family=ApNear"
            assertStatus HttpStatusCode.OK status
            assertContainsId body nearId

            let! statusFar, bodyFar = get client "/Patient?birthdate=ap1990-01-01&family=ApFar"
            assertStatus HttpStatusCode.OK statusFar
            assertNotContainsId bodyFar farId
        }

    [<Fact>]
    member _.``value-quantity ap matches systolic within ten percent``() : Task =
        task {
            let! patientId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "QtyAp", "given": ["Nuria"] }],
                      "gender": "female"
                    }
                    """

            let! nearId, _ =
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
                      "effectiveDateTime": "2024-01-15T12:00:00Z",
                      "valueQuantity": {{
                        "value": 105,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! farId, _ =
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
                      "effectiveDateTime": "2024-01-15T13:00:00Z",
                      "valueQuantity": {{
                        "value": 50,
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
                        "/Observation?patient=%s&value-quantity=ap100|http://unitsofmeasure.org|mm[Hg]"
                        patientId)

            assertStatus HttpStatusCode.OK status
            assertContainsId body nearId
            assertNotContainsId body farId
        }
