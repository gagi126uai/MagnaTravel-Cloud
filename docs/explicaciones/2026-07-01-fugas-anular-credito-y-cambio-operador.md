# Cierre de dos fugas de saldo a favor del operador (anular con crédito + cambio de operador/moneda)

Fecha: 2026-07-01
Estado: HECHO, revisado (3 revisores x 2 rondas), suite 3056/3056, **commiteado `df928c3` y DESPLEGADO** (pipeline 28493980618 success: tests back+front + deploy al VPS). Sin migración.

## En fácil (para Gastón)

Cuando sacás un servicio que ya le pagaste al operador, el sistema tiene que anotar *"el operador me tiene que devolver esta plata"*, no *"tengo saldo a favor para gastar"*. Faltaban tapar dos puertas por donde eso salía mal:

1. **Anular una reserva sin factura devolviéndole el saldo a favor al cliente.**
2. **Cambiar el operador (o la moneda) de un servicio ya pagado.**

En las dos, la plata que el operador te debe devolver se estaba por convertir en saldo a favor tuyo gastable (plata que no existe). Se cerraron las dos. Además se verificó que **borrar** un servicio pagado o **cambiarle el estado a mano** a cancelado ya estaban tapadas de antes.

El freno del cambio de operador quedó **afinado**: NO te molesta cuando tenés cuenta corriente / saldo a favor con el operador (prepago a cuenta), solo frena cuando hay una fuga real (plata pagada por esa reserva sin factura que la respalde).

## Mecanismo de la fuga (técnico)

La caja del operador (`SupplierBalanceByCurrency.Balance` = compras confirmadas − pagos, por moneda) la calcula `SupplierDebtPersister`. El "me tiene que devolver" (Y) se deriva EXCLUSIVAMENTE de las `BookingCancellationLine`. El reconciler `SupplierCreditReconciler` materializa `overpayment = max(0, -(Balance + Multa + Reembolso + Y))` como `SupplierCreditEntry` gastable. Si un servicio pagado deja de contar como compra confirmada SIN crear una `BookingCancellationLine`, la caja queda negativa con Y=0 → el próximo `ReconcileAsync` mintea ese negativo como saldo a favor gastable inexistente.

## Los dos arreglos

### 1. Anular con saldo a favor (reserva sin factura)
`ReservaService.ApplyAnnulWithPaymentsToCreditAsync` cancelaba todos los servicios (vía `ReservaServiceCanceller.CancelAllLiveServicesAsync`) sin crear líneas. Guarda nueva `EnsureReservaAnnulHasReceivableAnchorAsync` (precondición 7 en `LoadValidateAndApplyAnnulWithPaymentsToCreditAsync`, antes de mutar): bloquea (409) si hay pagos al operador sin ancla y sin factura viva.

### 2. Cambio de operador/moneda de un servicio confirmado y pagado
Los 6 `Update*Async` (5 tipados en `BookingService` + genérico en `ReservaService`) mutaban `SupplierId`/`Currency` sin guarda. Guarda nueva `EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync`. Corre cuando (a) cambió operador o moneda, Y (b) el servicio venía contando como compra confirmada del operador saliente (`CountsForSupplierDebtByType`, estado VIEJO capturado antes del `_mapper.Map`).

## La unificación (raíz)

Los TRES candados de la familia comparten ahora un único núcleo: `BookingCancellationService.ComputeUnanchoredOperatorRefundCapAsync` (scope Parcial/Full según el caso, filtrado al servicio). El pool de lo pagado se arma con `SupplierPayments.Where(p => p.ReservaId == reservaId ...)`, que **excluye el prepago a cuenta** (`ReservaId == null`). Por eso:
- Prepago on-account → no entra al pool → RefundCap 0 → NO bloquea (es saldo a favor legítimo; decisión del dueño 2026-06-26).
- Plata imputada a esta reserva por el servicio, sin factura → RefundCap > 0 → BLOQUEA (fuga real).

Primera versión del arreglo 2 usaba la caja GLOBAL del operador; sobre-bloqueaba la cuenta corriente. Gastón pidió afinar → se reemplazó por el núcleo preciso. Se eliminó el helper `OperatorReassignmentGuard.cs`.

## Fix colateral

`Adr021Capa7ContractTests.cs`: un test de reportes fallaba el día 1 del mes (sembraba `CreatedAt = start.AddDays(1)` = día 2 = futuro, que el filtro "ventas del mes hasta hoy" excluía). Cambiado a `start`. Ajeno a la fuga, se coló en el mismo commit.

## Fiscal

Cuenta interna del operador (AP): NO emite al ARCA. Nada fiscal nuevo. La NC/ND al cliente no se tocó.

## Reviews

Dos rondas (versión global + versión afinada), cada una: backend-dotnet-reviewer + security-data-risk-reviewer + data-exposure-reviewer. Todas Approved. Seguridad demostró que el criterio afinado NO reintroduce fuga (todo lo que deja pasar es prepago genuino).

## Pendiente / follow-ups (NO bloquean)

1. **Fase D — la PANTALLA de la cuenta del operador** (los dos números "Le debo / Me tiene que devolver" + bloque de cancelaciones): gate UX con Gastón. Es lo que sigue del norte.
2. **Over-block residual INTENCIONAL**: dos servicios del mismo operador en la misma reserva con pago parcial imputado → mover uno bloquea aunque el otro respalde. Idéntico a cancelar/anular ya en producción (el sistema no ata pagos por servicio). Documentado y consistente.
3. Auditar el intento BLOQUEADO (hoy ningún candado de la familia lo audita) — observabilidad.
4. Tests de bloqueo dedicados para Package/Transfer/Assistance tipados (comparten el helper; hoy solo hotel/genérico/flight tienen test de "no muta").
5. Validar en integración Postgres (transacciones/atomicidad + arranque del host DI) — no corre sin Docker en el entorno local.
6. Menores viejos: multa multimoneda ≠ pago; over-refund vía Allocate no atómico; destino contable del residuo no reembolsado (decisión de negocio).
