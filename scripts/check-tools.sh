#!/usr/bin/env bash
set -euo pipefail

MISSING_REQUIRED=0

get_version() {
  local tool="$1"
  case "$tool" in
    dotnet)
      dotnet --version 2>/dev/null | head -n 1
      ;;
    go)
      go version 2>/dev/null | awk '{print $3}'
      ;;
    docker)
      docker --version 2>/dev/null | sed -E 's/^Docker version ([^,]+).*/\1/'
      ;;
    kubectl)
      kubectl version --client 2>/dev/null | grep -m1 'Client Version:' | awk '{print $3}' || kubectl version --client --short 2>/dev/null | head -n 1
      ;;
    az)
      az version --output tsv 2>/dev/null | head -n 1 | awk '{print $1}' || az --version 2>/dev/null | head -n 1
      ;;
    terraform)
      terraform version 2>/dev/null | head -n 1 | awk '{print $2}'
      ;;
    helm)
      helm version --short 2>/dev/null | head -n 1
      ;;
    gh)
      gh --version 2>/dev/null | head -n 1 | awk '{print $3}'
      ;;
    k6)
      k6 version 2>/dev/null | head -n 1 | awk '{print $2}'
      ;;
    protoc)
      protoc --version 2>/dev/null | head -n 1 | awk '{print $2}'
      ;;
    *)
      echo "installed"
      ;;
  esac
}

print_header() {
  local title="$1"
  echo ""
  echo "$title"
  printf "%-14s %-12s %-24s %s\n" "TOOL" "STATUS" "VERSION" "NOTES"
  printf "%-14s %-12s %-24s %s\n" "--------------" "------------" "------------------------" "------------------------------"
}

check_tool() {
  local tool="$1"
  local required="$2"
  local hint="$3"

  if command -v "$tool" >/dev/null 2>&1; then
    local ver
    ver="$(get_version "$tool" || true)"
    ver="${ver:-installed}"
    printf "%-14s %-12s %-24s %s\n" "$tool" "OK" "$ver" ""
  else
    if [[ "$required" == "true" ]]; then
      printf "%-14s %-12s %-24s %s\n" "$tool" "MISSING" "-" "Required for Phase 1 ($hint)"
      MISSING_REQUIRED=1
    else
      printf "%-14s %-12s %-24s %s\n" "$tool" "OPTIONAL" "-" "Needed in Phase 2-5 ($hint)"
    fi
  fi
}

echo "Mini-Kubernetes Toolchain Check"

print_header "Phase 1 Required Tools:"
check_tool "dotnet"  "true" "brew install --cask dotnet-sdk"
check_tool "go"      "true" "brew install go"
check_tool "docker"  "true" "brew install --cask docker"
check_tool "kubectl" "true" "brew install kubectl"

print_header "Phase 2-5 Optional Tools (needed later):"
check_tool "az"        "false" "brew install azure-cli"
check_tool "terraform" "false" "brew install terraform"
check_tool "helm"      "false" "brew install helm"
check_tool "gh"        "false" "brew install gh"
check_tool "k6"        "false" "brew install k6"
check_tool "protoc"    "false" "brew install protobuf"

echo ""
if [[ "$MISSING_REQUIRED" -ne 0 ]]; then
  echo "Result: FAILED - One or more Phase 1 required tools are missing."
  exit 1
else
  echo "Result: OK - All Phase 1 required tools are installed."
  exit 0
fi
