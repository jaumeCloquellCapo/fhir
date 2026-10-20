module FhirServer.E2E.Endpoints.SearchAdvancedTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

/// Advanced search e2e — fixtures are request-body JSON as a client would POST them.
type SearchAdvancedTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    let mustCreate (path: string) (json: string) =
        task {
            let! status, body, _ = postJson client path json

            if status <> HttpStatusCode.Created then
                failwith (sprintf "%s create failed HTTP %d: %s" path (int status) body)

            let id = propString body "id" |> Option.defaultValue ""

            if String.IsNullOrWhiteSpace id then
                failwith (sprintf "%s create returned no id: %s" path body)

            return id, body
        }

    let bundleEntryIds (body: string) =
        use doc = JsonDocument.Parse(body)
        let mutable entry = Unchecked.defaultof<JsonElement>

        if not (doc.RootElement.TryGetProperty("entry", &entry)) then
            []
        else
            entry.EnumerateArray()
            |> Seq.choose (fun e ->
                let mutable res = Unchecked.defaultof<JsonElement>
                let mutable idEl = Unchecked.defaultof<JsonElement>

                if e.TryGetProperty("resource", &res) && res.TryGetProperty("id", &idEl) then
                    Some(idEl.GetString())
                else
                    None)
            |> Seq.toList

    let assertContainsId (body: string) (id: string) =
        Assert.True(
            bundleEntryIds body |> List.contains id,
            sprintf "Expected bundle to contain id %s. Body=%s" id (body.Substring(0, min 500 body.Length))
        )

    let assertNotContainsId (body: string) (id: string) =
        Assert.False(
            bundleEntryIds body |> List.contains id,
            sprintf "Expected bundle NOT to contain id %s. Body=%s" id (body.Substring(0, min 500 body.Length))
        )

    let enc (s: string) = Uri.EscapeDataString s

    [<Fact>]
    member _.``family:exact matches only exact family``() : Task =
        task {
            let! garciaId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "use": "official", "family": "Garcia", "given": ["Ana", "Maria"] }],
                      "gender": "female",
                      "birthDate": "1988-03-12",
                      "identifier": [{ "system": "urn:oid:2.16.724.4.10.10.1", "value": "NIS-Garcia-Ana" }]
                    }
                    """

            let! _, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "use": "official", "family": "GarciaExacto", "given": ["Luis"] }],
                      "gender": "male",
                      "birthDate": "1991-07-01",
                      "identifier": [{ "system": "urn:oid:2.16.724.4.10.10.1", "value": "NIS-GarciaExacto" }]
                    }
                    """

            let! status, body = get client "/Patient?family%3Aexact=Garcia"
            assertStatus HttpStatusCode.OK status
            assertContainsId body garciaId
            Assert.DoesNotContain("GarciaExacto", body)
        }

    [<Fact>]
    member _.``gender:missing=true finds patients without gender``() : Task =
        task {
            let! withId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "HasGender", "given": ["Pedro"] }],
                      "gender": "male"
                    }
                    """

            let! noId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "NoGender", "given": ["Lucia"] }]
                    }
                    """

            let! status, body = get client "/Patient?gender%3Amissing=true&family=NoGender"
            assertStatus HttpStatusCode.OK status
            assertContainsId body noId
            assertNotContainsId body withId
        }

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

    [<Fact>]
    member _.``_filter and not on patient demographics``() : Task =
        task {
            let! matchId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Garcia", "given": ["Ana"] }],
                      "gender": "female",
                      "birthDate": "1988-03-12"
                    }
                    """

            let! maleGarciaId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Garcia", "given": ["Luis"] }],
                      "gender": "male",
                      "birthDate": "1990-01-05"
                    }
                    """

            let! otherFamilyId, _ =
                mustCreate
                    "/Patient"
                    """
                    {
                      "resourceType": "Patient",
                      "name": [{ "family": "Martinez", "given": ["Sofia"] }],
                      "gender": "female",
                      "birthDate": "1995-12-20"
                    }
                    """

            let filterAnd = enc "family eq Garcia and gender eq female"
            let! andStatus, andBody = get client (sprintf "/Patient?_filter=%s" filterAnd)
            assertStatus HttpStatusCode.OK andStatus
            assertContainsId andBody matchId
            assertNotContainsId andBody maleGarciaId
            assertNotContainsId andBody otherFamilyId

            let filterNot = enc "not (gender eq male)"

            let! notStatus, notBody =
                get client (sprintf "/Patient?family%%3Aexact=Garcia&_filter=%s" filterNot)

            assertStatus HttpStatusCode.OK notStatus
            assertContainsId notBody matchId
            assertNotContainsId notBody maleGarciaId
        }
