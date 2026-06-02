#!/usr/bin/env bash
# run-tests-adr013.sh
#
# Corre los tests de las features mas recientes (no cubiertas por run-tests-fc13.sh):
#   - ADR-013: Nota de Debito por penalidad en cancelacion (gating, mapeo CbteTipo).
#   - Rediseno de estados de reserva (Vendida / A liquidar) + barrido Fase D.
#   - Flags operativos expuestos en el panel admin (EnableSoldToSettleStates /
#     EnableCancellationDebitNote).
#
# Clases UNITARIAS incluidas (EF Core InMemory + Moq, NO necesitan Docker ni Postgres):
#   - CancellationDebitNoteGatingTests   (gating de emision de ND + disyuncion anti-doble-cobro)
#   - CancellationDebitNoteCaptureTests  (ADR-013: captura de clasificacion + 3 guardas)
#   - CancellationDeferredPenaltyTests   (ADR-014: confirm-penalty diferido, precondiciones,
#                                         exactly-once por marca, 4-eyes, bandeja, plazo)
#   - InvoiceComprobanteHelpersTests     (fix del CbteTipo: factura/ND/NC -> ND correcta)
#   - OperationalFinanceSettingsFlagsTests (panel de flags: patch-like + validacion cruzada GR-013)
#   - ReservaSoldToSettleStatesTests     (matrices de transicion + gate de balance al cerrar)
#   - FaseDStateSetTests                 (conjuntos de estados Sold/ToSettle en los modulos)
#
# Clase de INTEGRACION incluida (Postgres real via TestContainers — REQUIERE Docker):
#   - CancellationDebitNoteDeferredIntegrationTests (ADR-013/014 §6: lo que InMemory NO puede
#     validar — commit propio de la marca Confirmed, concurrency token xmin en confirm-penalty,
#     persistencia + vinculo de la Invoice ND real, anti-doble-cobro re-evaluado en runtime
#     contra OperatorRefundAllocations reales, y el CHECK fiscalsnapshot_consistent).
#
# IMPORTANTE: por la clase de integracion, este script por defecto requiere Docker corriendo
# (TestContainers levanta un Postgres efimero).
#
# Como usar:
#   ssh user@vps
#   cd /ruta/al/MagnaTravel-Cloud
#   git pull
#   bash scripts/ops/run-tests-adr013.sh
#
# Modo SOLO-UNIT (sin Docker): si no tenes Docker (o solo queres los unit, mas rapido), corre
#   UNIT_ONLY=1 bash scripts/ops/run-tests-adr013.sh
# Eso EXCLUYE del filtro la clase de integracion (CancellationDebitNoteDeferredIntegrationTests)
# y SALTEA el pre-check de Docker. Util en la maquina de dev (sin Docker) para feedback rapido.
# El default (sin la variable) corre unit + integracion y exige Docker.
#
# El output completo queda en test-results-adr013.log.

set -uo pipefail

cd "$(dirname "$0")/../.."

LOG_FILE="test-results-adr013.log"

# Las clases UNITARIAS (EF Core InMemory + Moq, NO necesitan Docker).
UNIT_FILTER='FullyQualifiedName~CancellationDebitNoteGatingTests|FullyQualifiedName~CancellationDebitNoteCaptureTests|FullyQualifiedName~CancellationDeferredPenaltyTests|FullyQualifiedName~InvoiceComprobanteHelpersTests|FullyQualifiedName~OperationalFinanceSettingsFlagsTests|FullyQualifiedName~ReservaSoldToSettleStatesTests|FullyQualifiedName~FaseDStateSetTests'
# La clase de INTEGRACION (Postgres real via TestContainers -> requiere Docker).
INTEGRATION_FILTER='FullyQualifiedName~CancellationDebitNoteDeferredIntegrationTests'

# UNIT_ONLY=1 -> solo unit (sin Docker). Cualquier otro valor / sin definir -> unit + integracion.
UNIT_ONLY="${UNIT_ONLY:-0}"
if [[ "$UNIT_ONLY" == "1" ]]; then
  FILTER="$UNIT_FILTER"
  echo "== MODO UNIT_ONLY: se EXCLUYE $INTEGRATION_FILTER y se SALTEA el pre-check de Docker =="
else
  FILTER="$UNIT_FILTER|$INTEGRATION_FILTER"
fi

echo "== run-tests-adr013.sh =="
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

# 2) Pre-check: Docker daemon (requerido por CancellationDebitNoteDeferredIntegrationTests,
#    que levanta un Postgres efimero via TestContainers). Se SALTEA con UNIT_ONLY=1.
echo ""
if [[ "$UNIT_ONLY" == "1" ]]; then
  echo "[2/4] (UNIT_ONLY) Pre-check de Docker SALTEADO — no se corren tests de integracion."
else
  echo "[2/4] Chequeando Docker daemon (para los tests de integracion de la ND)..."
  if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: 'docker' no esta instalado. TestContainers requiere Docker para la clase de" | tee -a "$LOG_FILE"
    echo "  integracion CancellationDebitNoteDeferredIntegrationTests. Instalar Docker o" | tee -a "$LOG_FILE"
    echo "  correr con UNIT_ONLY=1 para solo los unit." | tee -a "$LOG_FILE"
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

# 4) Run tests focales (unit + integracion de la ND)
echo ""
echo "[4/4] Corriendo tests de ADR-013/014 + estados de reserva + panel de flags..."
echo "  Filter: $FILTER"
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
