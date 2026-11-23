.DEFAULT_GOAL := help

.PHONY: help check-tools proto build test up down k8s-validate tf-validate

help: ## Show available make targets
	@echo "Available targets:"
	@grep -E '^[a-zA-Z0-9_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-15s %s\n", $$1, $$2}'

check-tools: ## Verify local toolchain prerequisites
	@./scripts/check-tools.sh

proto: ## Generate gRPC stubs from proto/ definitions
	@if [ ! -d "proto" ] || [ -z "$$(find proto -name '*.proto' 2>/dev/null)" ]; then \
		echo "proto/ directory not yet created or contains no .proto files — skipping gRPC stub generation."; \
	else \
		echo "Proto files detected under proto/. Run protoc generation commands here once configured."; \
	fi

build: ## Build .NET and Go services under services/
	@found=0; \
	if [ -d "services" ]; then \
		for csproj in $$(find services -name '*.csproj' 2>/dev/null); do \
			found=1; \
			echo "Building .NET project: $$csproj"; \
			dotnet build "$$csproj"; \
		done; \
		for gomod in $$(find services -name 'go.mod' 2>/dev/null); do \
			found=1; \
			dir=$$(dirname "$$gomod"); \
			echo "Building Go module: $$dir"; \
			(cd "$$dir" && go build ./...); \
		done; \
	fi; \
	if [ "$$found" -eq 0 ]; then \
		echo "No .NET (.csproj) or Go (go.mod) services found under services/ yet."; \
	fi

test: ## Run tests for .NET and Go services under services/
	@found=0; \
	if [ -d "services" ]; then \
		for csproj in $$(find services -name '*.csproj' 2>/dev/null); do \
			found=1; \
			echo "Testing .NET project: $$csproj"; \
			dotnet test "$$csproj"; \
		done; \
		for gomod in $$(find services -name 'go.mod' 2>/dev/null); do \
			found=1; \
			dir=$$(dirname "$$gomod"); \
			echo "Testing Go module: $$dir"; \
			(cd "$$dir" && go test ./...); \
		done; \
	fi; \
	if [ "$$found" -eq 0 ]; then \
		echo "No .NET (.csproj) or Go (go.mod) services found under services/ yet."; \
	fi

up: ## Start local stack with Docker Compose
	docker compose up -d --build

down: ## Stop local stack and remove volumes
	docker compose down -v

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
