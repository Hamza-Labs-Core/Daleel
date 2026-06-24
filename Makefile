# Daleel — developer & ops shortcuts.
# Usage: make <target>   (run `make help` for the list)

SOLUTION      := Daleel.sln
WEB_PROJECT   := src/Daleel.Web/Daleel.Web.csproj
IMAGE         := ghcr.io/hamza-labs-core/daleel
TAG           ?= latest

.DEFAULT_GOAL := help
.PHONY: help build test docker deploy setup-vps run secrets

help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

build: ## dotnet build the whole solution (Release, -warnaserror)
	dotnet build $(SOLUTION) -c Release -warnaserror

test: ## dotnet test the whole solution
	dotnet test $(SOLUTION) -c Release

docker: ## Build the production Docker image (tag: $(TAG))
	docker build -t $(IMAGE):$(TAG) .

run: ## Run the web app locally (http://localhost:5000)
	dotnet run --project $(WEB_PROJECT)

deploy: ## Run the deploy script (pull image, restart, health-check, rollback)
	./deploy/deploy.sh $(TAG)

setup-vps: ## VPS setup is automatic — the deploy workflow bootstraps the box
	./deploy/setup.sh

secrets: ## Create placeholder GitHub repo secrets (see deploy/create-secrets.sh)
	./deploy/create-secrets.sh
