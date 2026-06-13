# Cancelación parcial / multi-operador — FIXES PENDIENTES (retomar acá)

Esta rama (`wip/adr025-cancelacion-parcial`, commit base `0d9cdf5`) tiene la cancelación parcial + multi-operador CONSTRUIDA (1698/1698 unit verde, migración Adr028_M1) pero la revisión de seguridad dejó **Changes Required**. NO mergear a main hasta cerrar lo de abajo, re-revisar con `security-data-risk-reviewer` y dejar la suite verde.

Spec de referencia: `docs/architecture/adr/ADR-025-cancelacion-parcial-y-multioperador.md` (DT.3.1, §3.2, resoluciones B1/B2).

El path TOTAL/multi-operador (`DraftAsync`) y el reparto de refund (`OperatorRefundService`) están OK (backend Approved). NO tocar eso. El problema está en `CancelServiceAsync` (BookingCancellationService.cs ~350-420), que se construyó como ATAJO: solo flipea el `Status` del servicio.

## Plan de fix (ejecutar con backend-dotnet-senior, luego re-review security)

### SEC-B1 (CRÍTICO, fiscal/seguridad) — el candado CAE/voucher se saltea
`CancelServiceAsync` NO consulta `MutationGuards`. El path normal de cambio de status en BookingService SÍ consulta `GetBookingMutationBlockReasonAsync` (BookingService.cs:554/812/1072/1307/1579) y bloquea cuando la reserva/servicio tiene factura CAE viva o voucher Issued (CODE-04/05). Sin eso, se puede tirar abajo la venta de un servicio YA FACTURADO con CAE vivo → ConfirmedSale cae por debajo del ImporteTotal de la factura sin emitir NC → divergencia fiscal irreversible.
**FIX:** en `CancelServiceAsync`, ANTES de marcar el servicio, consultar el mismo guard (`MutationGuards.GetBookingMutationBlockReasonAsync`/`GetServiceMutationBlockReasonAsync` según el tipo). Si hay factura CAE viva o voucher Issued → NO bajar el saldo silenciosamente: lanzar el bloqueo (InvalidOperationException con mensaje claro) o rutear a revisión manual, alineado con el path normal. Replicar el patrón exacto del guard que ya usa BookingService.

### SEC-B1b + backend M-A — el parcial no deja rastro ni anchor de refund
`CancelServiceAsync` no crea ninguna `BookingCancellation` ni `BookingCancellationLine` → `Scope.Partial` es dead-code, y un servicio parcial PAGADO al operador no tiene línea contra la cual imputar el refund (contradice §3.2 del ADR).
**FIX:** el parcial debe crear (o reusar) una `BookingCancellation` padre para esa reserva y una `BookingCancellationLine` con `Scope=Partial` para el servicio cancelado (SupplierId, ServiceTable, ServiceId, Currency, LineSaleAmount, clasificación de penalidad). Reusar `BuildCancellationLinesAsync` o el mismo armado que el path total, en modo parcial (una línea para ESE servicio). Así el refund del operador encuentra su anchor y el evento es trazable. Y armar el BORRADOR de NC para revisión manual (DT.3.1) — SIN auto-emitir, sin descongelar el flag.

### SEC-B2 + backend — RefundCap nunca se setea (siempre 0)
Grep: cero asignaciones a `RefundCap` salvo el backfill. Se lee en OperatorRefundService.cs:581/592 y BookingCancellationLine.cs:125 pero `BuildCancellationLinesAsync` (~3499-3507) y el backfill no lo asignan → siempre 0 → el cap por operador es decorativo y `RefundStatus=Settled` no se alcanza.
**FIX:** al construir cada `BookingCancellationLine` (total y el nuevo parcial), setear `RefundCap` = lo pagado al operador por ese servicio − penalidad de esa línea (fórmula ADR/INV-126). Verificar que `DistributeReceivedRefundToOperatorLines` use el cap real.

### M-B / INV-118 — coherencia de moneda del refund (es plata)
`OperatorRefundService` (~310-319) valida la moneda del refund contra `bc.FiscalSnapshot.CurrencyAtEvent` en vez de `line.Currency` → en multi-operador con monedas mixtas podría aceptar/rechazar en la moneda equivocada.
**FIX:** validar/imputar contra `line.Currency` de las líneas del operador. Si el cambio es amplio/riesgoso, dejar mono-operador idéntico y cubrir el multimoneda; documentar lo que se haga.

## Tests a agregar (además de mantener verdes los 13 de Adr025PartialAndMultiOperatorCancellationTests)
- Cancelar servicio parcial bajo factura CAE viva / voucher Issued → BLOQUEA (o rutea a manual), NO baja el saldo silenciosamente.
- El parcial crea una `BookingCancellation` + `Line` con Scope=Partial.
- `RefundCap` efectivo (≠ 0, = pagado − penalidad) y `Settled` alcanzable.
- Refund de un servicio parcial PAGADO al operador se imputa contra su línea.
- INV-118 multimoneda multi-operador (refund en la moneda correcta de la línea).

## NO tocar
- Path total/DraftAsync (anda). Authz del endpoint (ok). Emisión fiscal automática (sigue manual). Flag NC parcial (NO descongelar).

## Después de los fixes
1. `dotnet test src/TravelApi.Tests --filter "Category!=Integration"` verde.
2. Re-review con `security-data-risk-reviewer` (cierra SEC-B1/B1b/B2) + opcional backend-reviewer.
3. Integración Postgres real del Adr028_M1 + backfill (convención dura del repo — solo se valida en Postgres).
4. Merge a main.
5. Diferencia de cambio (decisión #4): el asiento contable lo define `accounting-expert-argentina` (DT.6 solo dejó el enganche); + firma del contador (Q-F1/Q-F2/plazo).
