# Homebrew .NET on macOS often needs DOTNET_ROOT; no-op elsewhere.
export DOTNET_ROOT ?= /opt/homebrew/opt/dotnet/libexec
export PATH := /opt/homebrew/opt/dotnet/bin:$(PATH)

PROJECT := src/FhirServer
URL     := http://localhost:5050

E2E := tests/FhirServer.E2E

GEN := generator/FhirFsGen
GEN_OUT := generator/out/r5
PKG_SCHEMAS := packages/FhirServer.Schemas
PKG_VALIDATION := packages/FhirServer.Validation

.PHONY: help restore tools build run api watch clean format format-check lint test test-e2e test-metadata db-up db-down generate generate-r5-all generate-todo sync-schemas

SYNC_SCHEMAS = \
	mkdir -p $(PKG_SCHEMAS)/Data $(PKG_SCHEMAS)/Data/profiles $(PKG_SCHEMAS)/Generated $(PKG_VALIDATION)/Generated && \
	cp -f $(GEN_OUT)/schemas/*.schema.json $(PKG_SCHEMAS)/Data/ && \
	(cp -f $(GEN_OUT)/schemas/profiles/*.schema.json $(PKG_SCHEMAS)/Data/profiles/ 2>/dev/null || true) && \
	(cp -f $(GEN_OUT)/profiles-index.json $(PKG_SCHEMAS)/Data/profiles-index.json 2>/dev/null || echo '{}' > $(PKG_SCHEMAS)/Data/profiles-index.json) && \
	cp -f $(GEN_OUT)/valuesets.json $(PKG_SCHEMAS)/Data/valuesets.json && \
	cp -f $(GEN_OUT)/GeneratedResourceCatalog.fs $(PKG_SCHEMAS)/Generated/GeneratedResourceCatalog.fs && \
	cp -f $(GEN_OUT)/GeneratedSchemaValidator.fs $(PKG_VALIDATION)/Generated/GeneratedSchemaValidator.fs

help: ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-14s\033[0m %s\n", $$1, $$2}'

restore: ## Restore NuGet packages
	dotnet restore FhirServer.slnx

tools: ## Restore local .NET tools (Fantomas, FSharpLint)
	dotnet tool restore

build: restore ## Build the API
	dotnet build $(PROJECT) --no-restore

test test-e2e: ## Run E2E tests (requires make db-up)
	dotnet test $(E2E) --verbosity minimal

format: tools ## Format F# sources with Fantomas
	dotnet fantomas $(PROJECT) tests/FhirServer.E2E packages

format-check: tools ## Check formatting without writing
	dotnet fantomas check $(PROJECT)

lint: tools ## Run FSharpLint
	dotnet fsharplint lint $(PROJECT)/FhirServer.fsproj

run: ## Start the FHIR API (http://localhost:5050)
	dotnet run --project $(PROJECT)

api: run ## Alias for `make run`

watch: ## Start the API with hot reload
	dotnet watch --project $(PROJECT) run

clean: ## Remove build artifacts
	dotnet clean $(PROJECT)
	rm -rf $(PROJECT)/bin $(PROJECT)/obj

test-metadata: ## GET /metadata (API must be running)
	curl -s $(URL)/metadata | jq .

db-up: ## Start Postgres (compose, port 5432)
	docker compose up -d

db-down: ## Stop Postgres
	docker compose down

sync-schemas: ## Copy generator/out/r5 schemas + validator into packages/
	@$(SYNC_SCHEMAS)
	@echo "Synced $$(ls $(PKG_SCHEMAS)/Data/*.schema.json | wc -l | tr -d ' ') schemas → $(PKG_SCHEMAS)/Data/"

generate: ## Generate R5 Patient schema + validator + valuesets; sync into packages/
	dotnet run --project $(GEN) -- --out $(GEN_OUT)
	@$(SYNC_SCHEMAS)

generate-r5-all: ## Generate all R5 resource schemas and sync into packages/
	dotnet run --project $(GEN) -- --all --out $(GEN_OUT)
	@$(SYNC_SCHEMAS)

generate-todo: ## Rewrite generator/TODO-R5.md checklist from package resources
	dotnet run --project $(GEN) -- --todo
