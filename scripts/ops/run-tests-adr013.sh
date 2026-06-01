#!/usr/bin/env bash
# run-tests-adr013.sh
#
# Corre los tests de las features mas recientes (no cubiertas por run-tests-fc13.sh):
#   - ADR-013: Nota de Debito por penalidad en cancelacion (gating, mapeo CbteTipo).
#   - Rediseno de estados de reserva (Vendida / A liquidar) + barrido Fase D.
#   - Flags operativos expuestos en el panel admin (EnableSoldToSettleStates /
#     EnableCancellationDebitNote).
#
# Clases incluidas (todas UNITARIAS: EF Core InMemory + Moq, NO necesitan Docker
# ni Postgres — corren igual en el VPS o en cualquier maquina con el SDK):
#   - CancellationDebitNoteGatingTests   (gating de emision de ND + disyuncion anti-doble-cobro)
#   - InvoiceComprobanteHelpersTests     (fix del CbteTipo: factura/ND/NC -> ND correcta)
#   - OperationalFinanceSettingsFlagsTests (panel de flags: patch-like + validacion cruzada GR-013)
#   - ReservaSoldToSettleStatesTests     (matrices de transicion + gate de balance al cerrar)
#   - FaseDStateSetTests                 (conjuntos de estados Sold/ToSettle en los modulos)
#
# NOTA: los tests de INTEGRACION end-to-end de la ND (emision real contra Postgres
# via TestContainers) TODAVIA NO existen — quedan como tarea de QA. Cuando se
# agreguen, sumar su filtro aca y el bloque de pre-check de Docker (ver
# run-tests-fc13.sh como referencia).
#
# Como usar:
#   ssh user@vps
#   cd /ruta/al/MagnaTravel-Cloud
#   git pull
#   bash scripts/ops/run-tests-adr013.sh
#
# El output completo queda en test-results-adr013.log.

set -uo pipefail

cd "$(dirname "$0")/../.."

LOG_FILE="test-results-adr013.log"
FILTER='FullyQualifiedName~CancellationDebitNoteGatingTests|FullyQualifiedName~InvoiceComprobanteHelpersTests|FullyQualifiedName~OperationalFinanceSettingsFlagsTests|FullyQualifiedName~ReservaSoldToSettleStatesTests|FullyQualifiedName~FaseDStateSetTests'

echo "== run-tests-adr013.sh =="
echo "Fecha: $(date)"
echo "Branch: $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
echo "HEAD: $(git rev-parse HEAD 2>/dev/null | head -c 7)"
echo ""

# 1) Pre-check: dotnet SDK 8+
echo "[1/3] Chequeando .NET SDK..."
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

# 2) Build del proyecto de tests
echo ""
echo "[2/3] Compilando proyecto de tests..."
if ! dotnet build src/TravelApi.Tests/TravelApi.Tests.csproj --nologo -v q 2>&1 | tail -10 | tee -a "$LOG_FILE"; then
  echo "ERROR: el build fallo. Revisar log arriba." | tee -a "$LOG_FILE"
  exit 1
fi

# 3) Run tests focales (unitarios, sin Docker)
echo ""
echo "[3/3] Corriendo tests de ADR-013 + estados de reserva + panel de flags..."
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
