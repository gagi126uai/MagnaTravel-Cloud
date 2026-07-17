# Review backend — Tanda 2 (operador impago solo sobre vivos) — 2026-07-17

## Veredicto: APROBADO CON COMENTARIOS — 0 bloqueantes

Verificado (file:line):
- Matriz ResolveServiceDebtBasis correcta (SupplierService.cs:3312-3339): vivo → costo pleno;
  anulado sin cargo → excluido; anulado con cargo → SOLO el cargo, jamás el costo pleno.
- Criterio "anulado" espeja byte a byte ServiceResolutionRules.IsCancelled (vuelo MapFlightStatus,
  resto MapGenericStatus; null-safe).
- "Cargo real" = misma fuente que el extracto del operador (SupplierCancellationCircuitReader.cs:94,
  129,151-152: RetainedDeductionAmount + FacturadaAparte, filtro Aborted idéntico) → no crea otra verdad.
- AttributeSupplierCreditToServicesAsync read-only; el fix FIFO solo cambia el reparto EN PANTALLA
  del crédito ya aplicado (no toca SupplierCreditApplication persistido).
- Único caller de producción: ReservasController.cs:145; el front no suma totales del DTO
  (join por servicio, supplierPaymentStatusLogic.js:33) → sin agregados rotos.
- La superficie agregada CxP (ficha proveedor) ya excluía anulados (CountsForSupplierDebtByType)
  → este cambio ELIMINA una "dos verdades" preexistente entre per-servicio y agregado.
- Sin N+1 (una query con Include, solo si hay anulados); claves (tabla, id) sin colisión entre tipos;
  centinela legacy ServiceId=0 no matchea.
- Coincide con el diseño INV-048-06 (DISENO-implementacion.md:506-526).

Mejoras (aplicadas en la ronda de endurecimiento posterior):
- N1: OutstandingToOperator podía salir negativo (anulado retenido + pago de caja histórico) → Math.Max(0).
- Tests faltantes: multimoneda (USD), vuelo IATA, multi-línea por servicio, BC Drafted con cargo,
  ruta enmascarada see_cost=false.
- N3: BC Drafted con servicio cancelado SÍ cuenta (intencional, espeja el extracto) — fijado con test.

Proceso: el archivo de tests nuevo estaba untracked — verificar git add antes del commit.

No verificado por el revisor: ejecución de tests (los corre el flujo CI), app real end-to-end,
firma fiscal del criterio (cubierta aparte por travel-agency-accountant: VALIDADO,
ver 2026-07-17-t2-criterio-cargo-operador-contador.md).
