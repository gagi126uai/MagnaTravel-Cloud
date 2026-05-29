#!/usr/bin/env bash
# run-tests-fc13.sh
#
# Corre los tests de FC1.3 Fase 1 + Fase 2 (F2.1..F2.6a) + regresion FC1.2
# (facturacion normal / NC total) para confirmar que no la rompio.
#
# Actualizado 2026-05-28: suma los tests de F2.5 (multimoneda: ArcaCurrencyMapper,
# formato SOAP MonId/MonCotiz, gating del calculator) + F2.6/F2.6a (job de
# reconciliacion de NC parcial colgada + counters).
#
# Tests FC1.3:
# - BookingCancellationServicePartialCreditNoteIntegrationTests (16 tests).
# - ForceBridgeCallbackEndpointTests (7 tests).
# - PartialCreditNoteE2ETests (3 tests).
# - FiscalLiquidationBackfillIntegrationTests (11 tests, F2.1).
# - InvoiceCurrencyAndArcaIdempotencyIntegrationTests (4 tests, F2.2 Etapa 0).
# - EnqueuePartialCreditNoteIntegrationTests (6 tests, F2.2 Etapa 4).
# - ProcessPartialCreditNoteJobIntegrationTests (10 tests, F2.2 Etapa 5: el job
#   de emision real + idempotencia + recovery).
# - AfipServiceTotalsOverrideTests (8 tests, F2.2 Etapa 5: cuadre ARCA exacto).
# - BookingCancellationServiceF2_3IntegrationTests (9 tests, F2.3: branch flag
#   ON/OFF, cascade no-cascade, guard multimoneda, sum mismatch, idempotencia).
# - AfipServicePartialCreditNoteReversalTests (3 tests unit, F2.3: discriminador
#   parcial vs total + Payment reversal con OriginalPaymentId=null).
# - BookingCancellationServiceHelpersTests (2 tests unit, F2.3: GetDominantAlicuotaId
#   tira en lista vacia + devuelve dominante con items).
# - PartialCreditNoteReconciliationIntegrationTests (3 tests, Fase 3: integracion
#   Postgres real de la bandeja de reconciliacion de NC parciales. Cubre el
#   concurrency token xmin del caso + los dos CHECK SQL crudos chk_pcnr_status y
#   chk_pcnr_resolved_consistency, que InMemory no ejercita).
# Regresion FC1.2 (que el fix del cuadre no rompio la facturacion normal / NC total):
# - InvoiceServiceFilteringAndAnnulmentTests, AfipServiceCascadeReceiptVoidTests,
#   InvoiceServiceRetryIdempotencyTests.
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
FILTER='FullyQualifiedName~BookingCancellationServicePartialCreditNoteIntegrationTests|FullyQualifiedName~ForceBridgeCallbackEndpointTests|FullyQualifiedName~PartialCreditNoteE2ETests|FullyQualifiedName~FiscalLiquidationBackfillIntegrationTests|FullyQualifiedName~InvoiceCurrencyAndArcaIdempotencyIntegrationTests|FullyQualifiedName~EnqueuePartialCreditNoteIntegrationTests|FullyQualifiedName~ProcessPartialCreditNoteJobIntegrationTests|FullyQualifiedName~AfipServiceTotalsOverrideTests|FullyQualifiedName~BookingCancellationServiceF2_3IntegrationTests|FullyQualifiedName~AfipServicePartialCreditNoteReversalTests|FullyQualifiedName~BookingCancellationServiceHelpersTests|FullyQualifiedName~InvoiceServiceFilteringAndAnnulmentTests|FullyQualifiedName~AfipServiceCascadeReceiptVoidTests|FullyQualifiedName~InvoiceServiceRetryIdempotencyTests|FullyQualifiedName~ArcaCurrencyMapperTests|FullyQualifiedName~AfipServiceMonedaSoapFormatTests|FullyQualifiedName~FiscalLiquidationCalculatorTests|FullyQualifiedName~PartialCreditNotePostingReconciliationJobTests|FullyQualifiedName~PartialCreditNoteReconciliationIntegrationTests'

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
echo "[4/4] Corriendo tests FC1.3 (Fase 1 + Fase 2 hasta Etapa 5) + regresion FC1.2 (facturacion normal / NC total)..."
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
