# FHIR fixtures (HL7 official examples)

FHIR R4 example resources from [hl7.org/fhir/R4](https://hl7.org/fhir/R4/), converted to NDJSON for e2e `$import` / `$load`.

| File | Sources |
|------|---------|
| `hl7-patients.ndjson` | patient-example, patient-example-a, patient-example-b |
| `hl7-organizations.ndjson` | organization-example, organization-example-f001-burgers |
| `hl7-observations.ndjson` | observation-example (`subject` rewritten to first Patient) |
| `hl7-mixed.ndjson` | orgs + patients + observation (dependency order) |
