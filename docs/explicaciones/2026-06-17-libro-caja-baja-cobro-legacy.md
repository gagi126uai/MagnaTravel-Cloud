# 2026-06-17 — La caja se descuadraba al borrar/editar un cobro por la pantalla vieja

## El problema (plata)
El sistema lleva un "Libro de Caja": cada cobro que mueve plata deja un asiento; cuando se anula un
cobro, NO se borra el asiento, se escribe un **contra-asiento** (la misma plata, al revés), así el neto
queda en cero y la historia no se reescribe. Esa es la fuente de verdad de la caja.

Había **dos caminos** para dar de baja o editar un cobro de cliente:
- El **camino nuevo** (pantalla de Cobranzas / `/api/payments`): escribía bien el contra-asiento.
- El **camino viejo** (botón de pagos dentro de la reserva, `DELETE`/`PUT /api/reservas/{id}/payments/{pid}`,
  solo Admin): al **borrar** un cobro **no escribía el contra-asiento**, y al **editar** el monto
  **no re-sincronizaba la caja**. Resultado: la caja quedaba **inflada** (al borrar) o con el **monto viejo**
  (al editar). Un descuadre silencioso de plata.

## El arreglo
- Saqué la mecánica de la reversa a un único lugar compartido: `CashLedgerPaymentReversal`
  (`src/TravelApi.Infrastructure/Reservations/`). Así los dos caminos escriben el contra-asiento igual.
- El camino nuevo ahora usa ese lugar único (sin cambiar lo que hacía).
- El camino viejo, al **borrar** un cobro que movió caja, escribe el contra-asiento, en la misma operación.
- El camino viejo, al **editar** el monto/método/fecha de un cobro que movió caja, revierte el asiento
  viejo y escribe uno nuevo con los datos nuevos (igual que el camino nuevo). El neto del libro queda en
  el monto nuevo.

Todo en la misma transacción que la baja/edición (se confirman o se caen juntos). Sin migración, sin
llaves. Un saldo a favor (puente) no entra acá: no mueve caja y ya estaba bloqueado.

## Por qué apareció la fuga gemela del "editar"
El arreglo del **borrar** lo encontró la auditoría previa. Al revisarlo, los dos revisores (backend y
seguridad) detectaron, por separado, que **editar** el monto por la misma pantalla vieja tenía
exactamente el mismo problema, 70 líneas más abajo. Se arregló en la misma tanda.

## Tests
- `src/TravelApi.Tests/Unit/ReservaServiceDeletePaymentLedgerTests.cs` (3):
  - borrar por la pantalla vieja → asiento original revertido + contra-asiento, neto 0;
  - cobro viejo sin asiento (anterior al Libro de Caja) → no crashea, no inventa reversa, igual borra;
  - editar el monto (100 → 150) → viejo revertido + contra-asiento + asiento nuevo, neto = 150.
- Suite Unit completa en verde: 1855/1855. Build limpio.

## Revisión
- `backend-dotnet-reviewer`: **Approved with comments** (0 bloqueantes). Encontró la fuga gemela del editar.
- `security-data-risk-reviewer`: **Approved with comments** (0 bloqueantes). Confirmó atomicidad e
  idempotencia del borrado; encontró la misma fuga gemela.
- El arreglo del **editar** es un calco exacto del camino nuevo ya probado (`PaymentService.UpdatePaymentAsync`),
  que es la referencia que ambos revisores señalaron como correcta, con su propio test de neto.

## Pendiente / a tener en cuenta
- **Integración Postgres en el VPS** (la corre Gastón): valida la atomicidad real y el índice único parcial
  del Libro de Caja, que InMemory no replica.
- Menores no bloqueantes (anotados, no urgentes): el borrado viejo no tiene candado anti doble-borrado como
  el nuevo (no es explotable: la reversa es no-op en la segunda pasada, la caja no se duplica); el borrado
  viejo no deja un evento de negocio propio (el rastro de quién/cuándo igual queda en el contra-asiento).
