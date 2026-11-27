.DEFAULT_GOAL := help

GOPATH_BIN := $(shell go env GOPATH)/bin

# ==============================================================================
# Region: General & Development Toolchain
# ==============================================================================
##@ General & Toolchain

.PHONY: help check-tools proto wt

help: ## Show available make targets grouped by scope
	@awk 'BEGIN {FS = ":.*##"; printf "\nAvailable targets:\n"} /^[a-zA-Z0-9_-]+:.*?##/ { printf "  %-24s %s\n", $$1, $$2 } /^##@/ { printf "\n[%s]\n", substr($$0, 5) } ' $(MAKEFILE_LIST)

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

wt: ## Read-only worktree review summary (WT=<path> for one worktree)
	@./scripts/check-worktree.sh $(if $(WT),$(WT),--all)


# ==============================================================================
# Region: Monorepo Full-Stack Targets (All Services)
# ==============================================================================
##@ Monorepo Full-Stack

.PHONY: build test test-unit test-integration up run down smoke

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

run: up ## Alias for up (start local stack with Docker Compose)

down: ## Stop local stack and remove volumes
	docker compose down -v

smoke: ## Run local end-to-end smoke test against Docker Compose stack
	./scripts/smoke-test.sh


# ==============================================================================
# Region: Order Service Scope (Build, Test, Run & Migrations)
# ==============================================================================
##@ Order Service Scope

.PHONY: build-order-service test-order-service run-order-service run-order \
        run-infra down-order-service down-order migrate-order-service \
        migrate-order migrate-add-order

build-order-service: ## Build .NET OrderService solution
	dotnet build services/order-service/OrderService.sln --nologo

test-order-service: ## Run all unit tests for OrderService
	dotnet test services/order-service/OrderService.sln --nologo --filter "TestCategory=Unit" --collect:"XPlat Code Coverage"

run-order-service: ## Start order-service and all dependencies (SQL Server, Redpanda, WebhookService)
	docker compose up -d --build order-service

run-order: run-order-service ## Alias for run-order-service

run-infra: ## Start only storage and messaging infrastructure (SQL Server, Redpanda, Kafka UI)
	docker compose up -d sqlserver redpanda redpanda-init kafka-ui

down-order-service: ## Stop order-service and its dependent containers
	docker compose stop order-service webhook-service mock-webhook redpanda-init redpanda sqlserver

down-order: down-order-service ## Alias for down-order-service

# Database Migrations:
# - In Docker Compose (`make run-order`), migrations apply automatically on startup
#   via Database__ApplyMigrationsOnStartup=true in docker-compose.yml.
# - On Local Host CLI: dotnet-ef (v8.0.13) is pinned locally via .config/dotnet-tools.json.
#   No global tool installation (`dotnet tool install -g`) is required.
#   Run `dotnet tool restore` if setting up on a new machine.

migrate-order-service: ## Apply pending EF Core migrations to local SQL Server
	APP_ENV=local dotnet ef database update \
		--project services/order-service/src/OrderService.Infrastructure \
		--startup-project services/order-service/src/OrderService.Grpc

migrate-order: migrate-order-service ## Alias for migrate-order-service

migrate-add-order: ## Scaffold new EF Core migration (usage: make migrate-add-order NAME=AddField)
	@if [ -z "$(NAME)" ]; then echo "Error: NAME is required. Example: make migrate-add-order NAME=AddOrderPriority"; exit 1; fi
	APP_ENV=local dotnet ef migrations add $(NAME) \
		--project services/order-service/src/OrderService.Infrastructure \
		--startup-project services/order-service/src/OrderService.Grpc \
		--output-dir Persistence/Migrations


# ==============================================================================
# Region: Cloud & IaC Validation
# ==============================================================================
##@ Cloud & IaC Validation

.PHONY: k8s-validate tf-validate

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
