module FhirServer.E2E.Endpoints.SearchHasChainTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert
open FhirServer.E2E.SearchTestHelpers

/// Reverse include (`_has`) and multi-hop chains.
type SearchHasChainTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()
    let mustCreate = SearchTestHelpers.mustCreate client

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``_has Observation patient code finds patients with hemoglobin``() : Task =
        task {
            let! patientId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "HasObs", "given": ["Ines"] }],
                      "gender": "female"
                    }
                    """

            let! otherId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "NoObs", "given": ["Jorge"] }],
                      "gender": "male"
                    }
                    """

            let! _, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "718-7",
                          "display": "Hemoglobin [Mass/volume] in Blood"
                        }}],
                        "text": "Hemoglobin"
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-05-10T08:30:00Z"
                    }}
                    """

            let! status, body =
                get client "/Patient?_has:Observation:patient:code=http://loinc.org|718-7"

            assertStatus HttpStatusCode.OK status
            assertContainsId body patientId
            assertNotContainsId body otherId
        }

    [<Fact>]
    member _.``multi-hop subject.organization.name finds hospital observations``() : Task =
        task {
            let! hospitalId, _ =
                mustCreate
                    "/Organization"
                    """
                    {
                      "resourceType": "Organization",
                      "active": true,
                      "name": "Hospital Clinic de Barcelona",
                      "type": [{
                        "coding": [{
                          "system": "http://terminology.hl7.org/CodeSystem/organization-type",
                          "code": "prov",
                          "display": "Healthcare Provider"
                        }]
                      }]
                    }
                    """

            let! clinicId, _ =
                mustCreate
                    "/Organization"
                    """
                    {
                      "resourceType": "Organization",
                      "active": true,
                      "name": "CAP Les Corts",
                      "type": [{
                        "coding": [{
                          "system": "http://terminology.hl7.org/CodeSystem/organization-type",
                          "code": "prov"
                        }]
                      }]
                    }
                    """

            let! patientId, _ =
                mustCreate
                    "/Patient"
                    $"""
                    {{
                      "resourceType": "Patient",
                      "name": [{{ "family": "Serra", "given": ["Marta"] }}],
                      "gender": "female",
                      "birthDate": "1992-04-18",
                      "managingOrganization": {{ "reference": "Organization/{hospitalId}" }}
                    }}
                    """

            let! otherPatientId, _ =
                mustCreate
                    "/Patient"
                    $"""
                    {{
                      "resourceType": "Patient",
                      "name": [{{ "family": "Pons", "given": ["Oriol"] }}],
                      "gender": "male",
                      "birthDate": "1985-09-03",
                      "managingOrganization": {{ "reference": "Organization/{clinicId}" }}
                    }}
                    """

            let! matchId, _ =
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
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "8480-6",
                          "display": "Systolic blood pressure"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-08-12T09:15:00Z",
                      "valueQuantity": {{
                        "value": 128,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! otherObsId, _ =
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
                      "subject": {{ "reference": "Patient/{otherPatientId}" }},
                      "effectiveDateTime": "2024-08-12T09:15:00Z",
                      "valueQuantity": {{
                        "value": 82,
                        "unit": "mmHg",
                        "system": "http://unitsofmeasure.org",
                        "code": "mm[Hg]"
                      }}
                    }}
                    """

            let! status, body =
                get client (sprintf "/Observation?subject.organization.name=%s" (enc "Hospital Clinic"))

            assertStatus HttpStatusCode.OK status
            assertContainsId body matchId
            assertNotContainsId body otherObsId
        }

    [<Fact>]
    member _.``nested _has finds org with glucose observation``() : Task =
        task {
            let! orgId, _ =
                mustCreate
                    "/Organization"
                    """
                    {
                      "resourceType": "Organization",
                      "active": true,
                      "name": "Hospital del Mar",
                      "type": [{
                        "coding": [{
                          "system": "http://terminology.hl7.org/CodeSystem/organization-type",
                          "code": "prov"
                        }]
                      }]
                    }
                    """

            let! otherOrgId, _ =
                mustCreate
                    "/Organization"
                    """
                    {
                      "resourceType": "Organization",
                      "active": true,
                      "name": "Hospital Sant Pau",
                      "type": [{
                        "coding": [{
                          "system": "http://terminology.hl7.org/CodeSystem/organization-type",
                          "code": "prov"
                        }]
                      }]
                    }
                    """

            let! patientId, _ =
                mustCreate
                    "/Patient"
                    $"""
                    {{
                      "resourceType": "Patient",
                      "name": [{{ "family": "Navarro", "given": ["Paula"] }}],
                      "gender": "female",
                      "managingOrganization": {{ "reference": "Organization/{orgId}" }}
                    }}
                    """

            let! otherPatientId, _ =
                mustCreate
                    "/Patient"
                    $"""
                    {{
                      "resourceType": "Patient",
                      "name": [{{ "family": "Soler", "given": ["Marc"] }}],
                      "gender": "male",
                      "managingOrganization": {{ "reference": "Organization/{otherOrgId}" }}
                    }}
                    """

            let! _, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "2345-7",
                          "display": "Glucose [Mass/volume] in Serum or Plasma"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{patientId}" }},
                      "effectiveDateTime": "2024-07-01T07:00:00Z"
                    }}
                    """

            let! _, _ =
                mustCreate
                    "/Observation"
                    $"""
                    {{
                      "resourceType": "Observation",
                      "status": "final",
                      "code": {{
                        "coding": [{{
                          "system": "http://loinc.org",
                          "code": "2093-3",
                          "display": "Cholesterol [Mass/volume] in Serum or Plasma"
                        }}]
                      }},
                      "subject": {{ "reference": "Patient/{otherPatientId}" }},
                      "effectiveDateTime": "2024-07-01T07:00:00Z"
                    }}
                    """

            let! status, body =
                get
                    client
                    "/Organization?_has:Patient:organization:_has:Observation:patient:code=http://loinc.org|2345-7"

            assertStatus HttpStatusCode.OK status
            assertContainsId body orgId
            assertNotContainsId body otherOrgId
            Assert.Contains("Hospital del Mar", body)
        }
