#!/usr/bin/env bash
# run-tests-all.sh
#
# Corre la BATERIA COMPLETA de tests del backend (todas las clases del proyecto
# TravelApi.Tests: unit + integracion). Pensado para correr en el VPS, que tiene
# Docker (los tests de integracion levantan un Postgres efimero via TestContainers).
#
# Como usar:
#   ssh user@vps
#   cd /ruta/al/MagnaTravel-Cloud
#   git pull
#   bash scripts/ops/run-tests-all.sh
#
# Modo SOLO-UNIT (sin Docker, mas rapido): EXCLUYE las clases de integracion
# (cualquier FQN que contenga "Integration") y SALTEA el pre-check de Docker:
#   UNIT_ONLY=1 bash scripts/ops/run-tests-all.sh
#
# El output completo queda en test-results-all.log.

set -uo pipefail

cd "$(dirname "$0")/../.."

LOG_FILE="test-results-all.log"

# UNIT_ONLY=1 -> excluye integracion (sin Docker). Sin definir -> corre TODO.
UNIT_ONLY="${UNIT_ONLY:-0}"
if [[ "$UNIT_ONLY" == "1" ]]; then
  FILTER_ARGS=(--filter 'FullyQualifiedName!~Integration')
  echo "== MODO UNIT_ONLY: se EXCLUYEN las clases de integracion y se SALTEA el pre-check de Docker =="
else
  FILTER_ARGS=()
fi

echo "== run-tests-all.sh =="
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

# 2) Pre-check: Docker daemon (requerido por las clases de integracion via TestContainers).
echo ""
if [[ "$UNIT_ONLY" == "1" ]]; then
  echo "[2/4] (UNIT_ONLY) Pre-check de Docker SALTEADO — no se corren tests de integracion."
else
  echo "[2/4] Chequeando Docker daemon (para los tests de integracion con Postgres real)..."
  if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: 'docker' no esta instalado. TestContainers lo necesita para los tests de" | tee -a "$LOG_FILE"
    echo "  integracion. Instalar Docker o correr con UNIT_ONLY=1 para solo los unit." | tee -a "$LOG_FILE"
    exit 1
  fi
  if ! docker info >/dev/null 2>&1; then
    echo "ERROR: Docker daemon no esta corriendo. Iniciarlo con 'sudo systemctl start docker'." | tee -a "$LOG_FILE"
    exit 1
  fi
  echo "  docker info OK"
fi

# 3) Build del proyecto de tests
echo ""
echo "[3/4] Compilando proyecto de tests..."
if ! dotnet build src/TravelApi.Tests/TravelApi.Tests.csproj --nologo -v q 2>&1 | tail -10 | tee -a "$LOG_FILE"; then
  echo "ERROR: el build fallo. Revisar log arriba." | tee -a "$LOG_FILE"
  exit 1
fi

# 4) Run de TODA la bateria
echo ""
echo "[4/4] Corriendo la bateria completa de tests..."
echo "  Log completo: $LOG_FILE"
echo ""

{
  echo ""
  echo "===== TEST RUN $(date -u +%Y-%m-%dT%H:%M:%SZ) ====="
  echo ""
} >> "$LOG_FILE"

dotnet test src/TravelApi.Tests/TravelApi.Tests.csproj \
  --no-build \
  --nologo \
  "${FILTER_ARGS[@]}" \
  --logger "console;verbosity=normal" 2>&1 | tee -a "$LOG_FILE"

EXIT_CODE=${PIPESTATUS[0]}

echo ""
echo "==============================="
echo "Exit code: $EXIT_CODE  (0 = todo verde)"
echo "Log completo: $LOG_FILE ($(wc -l < "$LOG_FILE") lineas)"
echo ""
echo "Para pasar a Claude: pegar el resumen final (Passed/Failed/Total) o el archivo $LOG_FILE."
echo ""

exit "$EXIT_CODE"
