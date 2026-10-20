module FhirServer.E2E.Endpoints.SearchAdvancedTests

open System
open System.Net
open System.Threading.Tasks
open Xunit
open FhirServer.E2E
open FhirServer.E2E.HttpAssert

type SearchAdvancedTests() =
    let factory = new FhirApiFactory()
    let client = factory.CreateClient()

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            factory.Dispose()

    [<Fact>]
    member _.``family:exact matches only exact family``() : Task =
        task {
            let! _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"GarciaExact","given":["Ana"]}]}"""

            let! _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"GarciaExacto","given":["Luis"]}]}"""

            let! status, body = get client "/Patient?family%3Aexact=GarciaExact"
            assertStatus HttpStatusCode.OK status
            Assert.Contains("GarciaExact", body)
            Assert.DoesNotContain("GarciaExacto", body)
        }

    [<Fact>]
    member _.``gender:missing=true finds patients without gender``() : Task =
        task {
            let! _, withGender, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"HasGender"}],"gender":"male"}"""

            let! _, noGender, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoGender"}]}"""

            let withId = propString withGender "id" |> Option.defaultValue ""
            let noId = propString noGender "id" |> Option.defaultValue ""

            let! status, body = get client "/Patient?gender%3Amissing=true"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(noId, body)
            Assert.DoesNotContain($"\"id\":\"{withId}\"", body)
        }

    [<Fact>]
    member _.``birthdate ge filters patients``() : Task =
        task {
            let! stOld, oldBody, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Old"}],"birthDate":"1980-01-01"}"""

            if stOld <> HttpStatusCode.Created then
                failwith ("old create failed " + string (int stOld) + " " + oldBody)

            let! stYoung, young, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Young"}],"birthDate":"2000-06-15"}"""

            if stYoung <> HttpStatusCode.Created then
                failwith ("young create failed " + string (int stYoung) + " " + young)

            let youngId = propString young "id" |> Option.defaultValue ""

            let! status, body = get client "/Patient?birthdate=ge1990-01-01"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(youngId, body)
            Assert.Contains("Young", body)
            Assert.DoesNotContain("\"family\":\"Old\"", body)
        }

    [<Fact>]
    member _.``value-quantity filters observations``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Qty"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _, hi, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":120,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":80,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let hiId = propString hi "id" |> Option.defaultValue ""

            let! status, body =
                get client "/Observation?value-quantity=gt100|http://unitsofmeasure.org|mm[Hg]"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(hiId, body)
            Assert.DoesNotContain("\"value\":80", body)
        }

    [<Fact>]
    member _.``_has Observation patient code finds patients``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"HasObs"}]}"""

            let! _, other, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"NoObs"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""
            let otherId = propString other "id" |> Option.defaultValue ""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"9999-1"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! status, body =
                get client "/Patient?_has:Observation:patient:code=http://loinc.org|9999-1"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(patientId, body)
            Assert.Contains("HasObs", body)
            Assert.DoesNotContain(otherId, body)
        }

    [<Fact>]
    member _.``multi-hop subject.organization.name finds observations``() : Task =
        task {
            let! _, org, _ =
                postJson
                    client
                    "/Organization"
                    """{"resourceType":"Organization","name":"AcmeChainClinic"}"""

            let! _, otherOrg, _ =
                postJson
                    client
                    "/Organization"
                    """{"resourceType":"Organization","name":"OtherClinic"}"""

            let orgId = propString org "id" |> Option.defaultValue ""
            let otherOrgId = propString otherOrg "id" |> Option.defaultValue ""

            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    $"""{{"resourceType":"Patient","name":[{{"family":"Chain"}}],"managingOrganization":{{"reference":"Organization/{orgId}"}}}}"""

            let! _, otherPatient, _ =
                postJson
                    client
                    "/Patient"
                    $"""{{"resourceType":"Patient","name":[{{"family":"Other"}}],"managingOrganization":{{"reference":"Organization/{otherOrgId}"}}}}"""

            let patientId = propString patient "id" |> Option.defaultValue ""
            let otherPatientId = propString otherPatient "id" |> Option.defaultValue ""

            let! _, matchObs, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"1111-1"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"1111-2"}}]}},"subject":{{"reference":"Patient/{otherPatientId}"}}}}"""

            let matchId = propString matchObs "id" |> Option.defaultValue ""

            let! status, body = get client "/Observation?subject.organization.name=AcmeChainClinic"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(matchId, body)
            Assert.DoesNotContain("1111-2", body)
        }

    [<Fact>]
    member _.``nested _has Organization Patient Observation finds orgs``() : Task =
        task {
            let! _, org, _ =
                postJson
                    client
                    "/Organization"
                    """{"resourceType":"Organization","name":"NestedHasOrg"}"""

            let! _, otherOrg, _ =
                postJson
                    client
                    "/Organization"
                    """{"resourceType":"Organization","name":"NoNestedHas"}"""

            let orgId = propString org "id" |> Option.defaultValue ""
            let otherOrgId = propString otherOrg "id" |> Option.defaultValue ""

            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    $"""{{"resourceType":"Patient","name":[{{"family":"NestedHas"}}],"managingOrganization":{{"reference":"Organization/{orgId}"}}}}"""

            let! _, otherPatient, _ =
                postJson
                    client
                    "/Patient"
                    $"""{{"resourceType":"Patient","name":[{{"family":"NoHas"}}],"managingOrganization":{{"reference":"Organization/{otherOrgId}"}}}}"""

            let patientId = propString patient "id" |> Option.defaultValue ""
            let otherPatientId = propString otherPatient "id" |> Option.defaultValue ""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"7777-7"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8888-8"}}]}},"subject":{{"reference":"Patient/{otherPatientId}"}}}}"""
            let! status, body =
                get
                    client
                    "/Organization?_has:Patient:organization:_has:Observation:patient:code=http://loinc.org|7777-7"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(orgId, body)
            Assert.Contains("NestedHasOrg", body)
            Assert.DoesNotContain(otherOrgId, body)
        }

    [<Fact>]
    member _.``birthdate ap matches nearby dates``() : Task =
        task {
            let! _, near, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"ApNear"}],"birthDate":"1990-01-02"}"""

            let! _, far, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"ApFar"}],"birthDate":"2000-01-01"}"""

            let nearId = propString near "id" |> Option.defaultValue ""
            let farId = propString far "id" |> Option.defaultValue ""

            let! status, body = get client "/Patient?birthdate=ap1990-01-01"
            assertStatus HttpStatusCode.OK status
            Assert.Contains(nearId, body)
            Assert.DoesNotContain(farId, body)
        }

    [<Fact>]
    member _.``value-quantity ap matches within ten percent``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"QtyAp"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _, near, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":105,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let! _, far, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8867-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":50,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let nearId = propString near "id" |> Option.defaultValue ""
            let farId = propString far "id" |> Option.defaultValue ""

            let! status, body =
                get client "/Observation?value-quantity=ap100|http://unitsofmeasure.org|mm[Hg]"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(nearId, body)
            Assert.DoesNotContain(farId, body)
        }

    [<Fact>]
    member _.``code below and above use prefix hierarchy``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"TokHier"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _, child, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://example.org/hier","code":"A.B.C"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! _, parent, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://example.org/hier","code":"A.B"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let! _, other, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://example.org/hier","code":"X.Y"}}]}},"subject":{{"reference":"Patient/{patientId}"}}}}"""

            let childId = propString child "id" |> Option.defaultValue ""
            let parentId = propString parent "id" |> Option.defaultValue ""
            let otherId = propString other "id" |> Option.defaultValue ""

            let! belowStatus, belowBody =
                get client "/Observation?code%3Abelow=http://example.org/hier|A.B"

            assertStatus HttpStatusCode.OK belowStatus
            Assert.Contains(childId, belowBody)
            Assert.Contains(parentId, belowBody)
            Assert.DoesNotContain(otherId, belowBody)

            let! aboveStatus, aboveBody =
                get client "/Observation?code%3Aabove=http://example.org/hier|A.B.C"

            assertStatus HttpStatusCode.OK aboveStatus
            Assert.Contains(parentId, aboveBody)
            Assert.Contains(childId, aboveBody) // :above includes self
            Assert.DoesNotContain(otherId, aboveBody)
        }

    [<Fact>]
    member _.``composite code-value-quantity matches both components``() : Task =
        task {
            let! _, patient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"Composite"}]}"""

            let patientId = propString patient "id" |> Option.defaultValue ""

            let! _, matchObs, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8480-6"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":120,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let! _, wrongCode, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8462-4"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":120,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let! _, wrongQty, _ =
                postJson
                    client
                    "/Observation"
                    $"""{{"resourceType":"Observation","status":"final","code":{{"coding":[{{"system":"http://loinc.org","code":"8480-6"}}]}},"subject":{{"reference":"Patient/{patientId}"}},"valueQuantity":{{"value":80,"system":"http://unitsofmeasure.org","code":"mm[Hg]"}}}}"""

            let matchId = propString matchObs "id" |> Option.defaultValue ""
            let wrongCodeId = propString wrongCode "id" |> Option.defaultValue ""
            let wrongQtyId = propString wrongQty "id" |> Option.defaultValue ""

            let! status, body =
                get
                    client
                    "/Observation?code-value-quantity=http://loinc.org|8480-6$gt100|http://unitsofmeasure.org|mm[Hg]"

            assertStatus HttpStatusCode.OK status
            Assert.Contains(matchId, body)
            Assert.DoesNotContain(wrongCodeId, body)
            Assert.DoesNotContain(wrongQtyId, body)
        }

    [<Fact>]
    member _.``_filter and/or/not on patient params``() : Task =
        task {
            let! _, matchPatient, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"FilterGarcia","given":["Ana"]}],"gender":"female"}"""

            let! _, otherGender, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"FilterGarcia","given":["Luis"]}],"gender":"male"}"""

            let! _, otherFamily, _ =
                postJson
                    client
                    "/Patient"
                    """{"resourceType":"Patient","name":[{"family":"FilterOther"}],"gender":"female"}"""

            let matchId = propString matchPatient "id" |> Option.defaultValue ""
            let otherGenderId = propString otherGender "id" |> Option.defaultValue ""
            let otherFamilyId = propString otherFamily "id" |> Option.defaultValue ""

            let! andStatus, andBody =
                get client "/Patient?_filter=family%20eq%20FilterGarcia%20and%20gender%20eq%20female"

            assertStatus HttpStatusCode.OK andStatus
            Assert.Contains(matchId, andBody)
            Assert.DoesNotContain(otherGenderId, andBody)
            Assert.DoesNotContain(otherFamilyId, andBody)

            let! notStatus, notBody =
                get client "/Patient?_filter=not%20(gender%20eq%20male)&family=FilterGarcia"

            assertStatus HttpStatusCode.OK notStatus
            Assert.Contains(matchId, notBody)
            Assert.DoesNotContain(otherGenderId, notBody)
        }
