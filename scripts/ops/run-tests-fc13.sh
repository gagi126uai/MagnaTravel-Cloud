#!/usr/bin/env bash
# run-tests-fc13.sh
#
# Corre los 26 tests integration + E2E nuevos de FC1.3 Fase 1 (commit 93c1b58).
#
# Tests:
# - BookingCancellationServicePartialCreditNoteIntegrationTests (16 tests, ~1047 lineas).
# - ForceBridgeCallbackEndpointTests (7 tests, ~563 lineas).
# - PartialCreditNoteE2ETests (3 tests, ~425 lineas).
#
# Requisitos en el VPS:
# - .NET 8 SDK instalado (dotnet --version retorna >= 8.0).
# - Docker daemon corriendo (docker info retorna OK). TestContainers levanta
#   un Postgres efimero por test.
#
# Como usar:
#   ssh user@vps
#   cd /path/al/MagnaTravel-Cloud
#   git pull
#   bash scripts/ops/run-tests-fc13.sh
#
# Si falla por falta de dotnet o docker, el script lo dice claro.
# El output completo queda en test-results-fc13.log para mandar a Claude.

set -uo pipefail

cd "$(dirname "$0")/../.."

LOG_FILE="test-results-fc13.log"
FILTER='FullyQualifiedName~BookingCancellationServicePartialCreditNoteIntegrationTests|FullyQualifiedName~ForceBridgeCallbackEndpointTests|FullyQualifiedName~PartialCreditNoteE2ETests'

echo "== run-tests-fc13.sh =="
echo "Fecha: $(date)"
echo "Branch: $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
echo "HEAD: $(git rev-parse HEAD 2>/dev/null | head -c 7)"
echo ""

# 1) Pre-check: dotnet SDK 8+
echo "[1/4] Chequeando .NET SDK..."
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: 'dotnet' no esta instalado. Instalar .NET 8 SDK en el VPS." | tee -a "$LOG_FILE"
  echo "  Ubuntu: sudo apt-get install -y dotnet-sdk-8.0" | tee -a "$LOG_FILE"
  exit 1
fi
DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "?")
echo "  dotnet version: $DOTNET_VERSION"
if [[ ! "$DOTNET_VERSION" =~ ^8\. ]]; then
  echo "WARN: .NET SDK no es 8.x (es $DOTNET_VERSION). Los tests pueden fallar."
fi

# 2) Pre-check: Docker daemon
echo ""
echo "[2/4] Chequeando Docker daemon..."
if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: 'docker' no esta instalado. TestContainers requiere Docker." | tee -a "$LOG_FILE"
  exit 1
fi
if ! docker info >/dev/null 2>&1; then
  echo "ERROR: Docker daemon no esta corriendo. Iniciarlo con 'sudo systemctl start docker'." | tee -a "$LOG_FILE"
  exit 1
fi
echo "  docker info OK"

# 3) Build proyecto de tests
echo ""
echo "[3/4] Compilando proyecto de tests..."
if ! dotnet build src/TravelApi.Tests/TravelApi.Tests.csproj --nologo -v q 2>&1 | tail -10 | tee -a "$LOG_FILE"; then
  echo "ERROR: el build fallo. Revisar log arriba." | tee -a "$LOG_FILE"
  exit 1
fi

# 4) Run tests focales
echo ""
echo "[4/4] Corriendo 26 tests FC1.3 (BookingCancellationService + ForceBridgeCallback + E2E)..."
echo "  Filter: $FILTER"
echo "  Log completo: $LOG_FILE"
echo ""

# Redirigimos todo a log + tee a consola.
{
  echo ""
  echo "===== TEST RUN $(date -u +%Y-%m-%dT%H:%M:%SZ) ====="
  echo ""
} >> "$LOG_FILE"

dotnet test src/TravelApi.Tests/TravelApi.Tests.csproj \
  --no-build \
  --nologo \
  --filter "$FILTER" \
  --logger "console;verbosity=normal" 2>&1 | tee -a "$LOG_FILE"

EXIT_CODE=${PIPESTATUS[0]}

echo ""
echo "==============================="
echo "Exit code: $EXIT_CODE"
echo "Log completo: $LOG_FILE ($(wc -l < "$LOG_FILE") lineas)"
echo ""
echo "Para pasar a Claude: copy del archivo $LOG_FILE o pegar el resumen final."
echo ""

exit "$EXIT_CODE"
