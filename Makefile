.DEFAULT_GOAL := help

.PHONY: help check-tools proto build test test-unit test-integration up down smoke k8s-validate tf-validate

GOPATH_BIN := $(shell go env GOPATH)/bin

help: ## Show available make targets
	@echo "Available targets:"
	@grep -E '^[a-zA-Z0-9_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-18s %s\n", $$1, $$2}'

check-tools: ## Verify local toolchain prerequisites
	@./scripts/check-tools.sh

proto: ## Generate Go gRPC stubs from proto/ definitions (C# stubs generated via Grpc.Tools during dotnet build)
	@mkdir -p services/webhook-service/gen
	@PATH="$$PATH:$(GOPATH_BIN)" protoc \
		-I proto \
		--go_out=services/webhook-service/gen \
		--go_opt=paths=source_relative \
		--go-grpc_out=services/webhook-service/gen \
		--go-grpc_opt=paths=source_relative \
		proto/webhook/v1/webhook.proto
	@echo "Generated Go stubs under services/webhook-service/gen/webhook/v1"

build: ## Build .NET and Go services under services/
	@found=0; \
	if [ -d "services" ]; then \
		for sln in $$(find services -maxdepth 2 -name '*.sln' 2>/dev/null); do \
			found=1; \
			echo "Building .NET solution: $$sln"; \
			dotnet build "$$sln" --nologo; \
		done; \
		for gomod in $$(find services -maxdepth 2 -name 'go.mod' 2>/dev/null); do \
			found=1; \
			dir=$$(dirname "$$gomod"); \
			echo "Building Go module: $$dir"; \
			(cd "$$dir" && go build ./... && go vet ./...); \
		done; \
	fi; \
	if [ "$$found" -eq 0 ]; then \
		echo "No .NET (.sln) or Go (go.mod) services found under services/ yet."; \
	fi

test: ## Run all unit and integration tests for .NET and Go services
	@found=0; \
	if [ -d "services" ]; then \
		for sln in $$(find services -maxdepth 2 -name '*.sln' 2>/dev/null); do \
			found=1; \
			echo "Testing .NET solution: $$sln"; \
			dotnet test "$$sln" --nologo --collect:"XPlat Code Coverage"; \
		done; \
		for gomod in $$(find services -maxdepth 2 -name 'go.mod' 2>/dev/null); do \
			found=1; \
			dir=$$(dirname "$$gomod"); \
			echo "Testing Go module: $$dir"; \
			(cd "$$dir" && go test -race -coverprofile=coverage.out ./... && go tool cover -func=coverage.out | tail -n 1); \
		done; \
	fi; \
	if [ "$$found" -eq 0 ]; then \
		echo "No .NET (.sln) or Go (go.mod) services found under services/ yet."; \
	fi

test-unit: ## Run unit tests only (no Docker daemon required)
	@for sln in $$(find services -maxdepth 2 -name '*.sln' 2>/dev/null); do \
		echo "Unit testing .NET solution: $$sln"; \
		dotnet test "$$sln" --nologo --filter "TestCategory=Unit" --collect:"XPlat Code Coverage"; \
	done
	@for gomod in $$(find services -maxdepth 2 -name 'go.mod' 2>/dev/null); do \
		dir=$$(dirname "$$gomod"); \
		echo "Unit testing Go module: $$dir"; \
		(cd "$$dir" && go test -short -race -coverprofile=coverage.out ./...); \
	done

test-integration: ## Run Docker container-scoped integration tests
	@for sln in $$(find services -maxdepth 2 -name '*.sln' 2>/dev/null); do \
		echo "Integration testing .NET solution: $$sln"; \
		dotnet test "$$sln" --nologo --filter "TestCategory=Integration"; \
	done

up: ## Start local stack with Docker Compose
	docker compose up -d --build

down: ## Stop local stack and remove volumes
	docker compose down -v

smoke: ## Run local end-to-end smoke test against Docker Compose stack
	./scripts/smoke-test.sh

k8s-validate: ## Validate Kubernetes kustomize overlays (staging & production)
	@found=0; \
	for env in staging production; do \
		dir="deploy/k8s/overlays/$$env"; \
		if [ -d "$$dir" ]; then \
			found=1; \
			echo "Validating Kubernetes overlay: $$dir"; \
			kubectl kustomize "$$dir" >/dev/null; \
		else \
			echo "Overlay not present yet: $$dir (skipping)"; \
		fi; \
	done; \
	if [ "$$found" -eq 0 ]; then \
		echo "No Kubernetes overlays found under deploy/k8s/overlays/ yet."; \
	fi

tf-validate: ## Format-check and validate Terraform configurations
	@if [ ! -d "infra/terraform" ]; then \
		echo "infra/terraform directory not present yet — skipping Terraform validation."; \
	else \
		echo "Checking Terraform formatting under infra/terraform..."; \
		terraform fmt -check -recursive infra/terraform; \
		for tfdir in $$(find infra/terraform -name '*.tf' -exec dirname {} \; | sort -u); do \
			echo "Validating Terraform directory: $$tfdir"; \
			terraform -chdir="$$tfdir" validate; \
		done; \
	fi
