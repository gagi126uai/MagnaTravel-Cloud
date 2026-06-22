# ADR-037 — Desacople de facturación (carril de facturación derivado, estilo ERP)

- Estado: Aceptado (backend implementado)
- Fecha: 2026-06-21
- Autor: backend-dotnet-senior (implementación) sobre decisión del dueño
- Relacionado: [ADR-033](ADR-033-cobro-desacoplado-del-estado.md) (cobro desacoplado del estado), [ADR-035](ADR-035-politica-capacidades-por-estado-y-cobro-multimoneda.md) (política única de capacidades), [ADR-036](ADR-036-prepago-puro-eliminar-a-liquidar-en-viaje-inmutable-gate-pago.md) (prepago puro)

## Contexto

Hasta ADR-036 la **factura de venta** estaba atada al estado de la reserva: solo se podía emitir en `Confirmed` (Confirmada), y para facturar una reserva ya `Closed` (Finalizada) había que **"reabrir para facturar"** (revert `Closed → ToSettle`, luego eliminado). Esto es lo contrario a cómo trabajan los ERP de referencia (SAP, Odoo): ahí el documento fiscal se **desacopla** del estado operativo, y el "estado de facturación" es un **carril separado y derivado** que se calcula de las facturas vivas, no se persiste ni se confunde con el ciclo de vida.

Gran parte del desacople ya existía por ADR-033 (cobro) y por el cuadre de facturación:

- El **carril de cobro** ya está derivado: `ReservaCollectionStatus.Derive(...)` calcula `ConDeuda/SaldoAFavor/Saldado` desde el saldo por moneda, sin persistir.
- La **cuenta de facturado** ya es pura: `ReservaInvoicingCuadreCalculator.Calculate(vendido, lines)` suma facturas + ND, resta NC, contando solo comprobantes vivos (CAE aprobado, no anulados). Alimenta `FacturadoNeto` y `DisponibleParaFacturar`.
- Las **NC/ND** ya no tienen gate de estado: corregir/anular una factura de una reserva `Closed/Cancelled` funciona sin reabrir.

Lo que faltaba era: (1) un **carril de estado de facturación** explícito (espejo del de cobro), (2) relajar el candado para emitir la factura de venta en cualquier estado firme no-anulado, y (3) eliminar el "reabrir para facturar".

## Decisión

### 1. Carril de facturación DERIVADO (no persistido)

Se agrega `ReservaInvoicingStatus.Derive(decimal vendido, decimal facturadoNeto)` (clase pura, espejo de `ReservaCollectionStatus`), con tres valores:

- `NotInvoiced` ("Sin facturar"): `facturadoNeto <= epsilon`.
- `PartiallyInvoiced` ("Facturada en parte"): `epsilon < facturadoNeto < vendido - epsilon`.
- `FullyInvoiced` ("Facturada total"): `facturadoNeto >= vendido - epsilon` (incluye over-invoicing).

Misma tolerancia de centavo (`epsilon = 0.005`) que `ReservaCollectionStatus`.

- **"Total" = por MONTO** (decisión del dueño H1): `facturadoNeto >= vendido`. NO existe vínculo factura↔servicio en el modelo, así que no se puede medir "por servicio". La única verdad disponible es el monto. Facturar de más también cuenta como `FullyInvoiced` (no hay un cuarto valor "excedido"; el aviso de exceso ya lo da el cuadre con `Disponible` negativo).
- **Escalar v1** (decisión del dueño H4): `facturadoNeto`/`vendido` son escalares (suman ARS + USD), consistentes con lo que el front ya muestra en el cuadre. Un carril por moneda queda como follow-up cuando el cuadre exponga el facturado por moneda.

Se expone como `ReservaDto.InvoicingStatus` (detalle) y `ReservaListDto.InvoicingStatus` (listado, calculado en una query agrupada por reserva sin N+1).

### 2. Factura de venta facturable en `{Confirmed, Traveling, Closed}` (H2 = A, conservador)

Se amplía la allow-list `ReservaCapabilityPolicy.InvoiceableStatuses` de `{Confirmed}` a `{Confirmed, Traveling, Closed}`. Esto **revierte** la restricción de ADR-036 ("en viaje no se factura").

- NO se incluye `InManagement` (servicios sin resolver: facturar ahí sería emitir CAE por algo que el operador podría rechazar), ni `Quotation`/`Budget` (pre-venta), ni los estados **anulados** `Cancelled/Lost/PendingOperatorRefund`.
- `InvoiceService.ActiveInvoicingStatuses` y el guard de `CreateAsync` ya delegan en esta const / en `CanInvoiceSale`, así que el cambio se propaga a la bandeja y al guard server-side automáticamente. La bandeja de facturación ahora incluye reservas `Traveling`/`Closed` sin factura (antes desaparecían al avanzar de estado), lo cual es el comportamiento correcto.

**Tensión con ADR-036 resuelta**: la factura de VENTA se permite en `Traveling`/`Closed`; lo que sigue **inmutable** en `Traveling` es el resto (servicios, pasajeros, datos, cobro), porque esos siguen bloqueados por `ServiceEditableStatuses` (sin `Traveling`) y por `EvaluateRegisterPayment` (que bloquea `Traveling`). Emitir factura NO muta la reserva. Hay un test de coherencia que bloquea esta tensión explícitamente (`Traveling_InvoiceAllowed_ButEditAndCollectionStillBlocked_ADR037`).

### 3. Se elimina el "reabrir para facturar"

- El revert `Closed → Traveling` se **conserva**, pero SOLO como "deshacer un cierre prematuro" (decisión H3), NUNCA para facturar. Facturar tarde ya no necesita reabrir: se factura directo desde `Closed` (`CanInvoiceSale = Allowed` en `Closed`).
- El campo `canReopenForInvoicing` no se emitía (el front usaba un fallback); no hay campo que quitar del backend.

### 4. Flag `IsWithinUnpaidAlertWindow` (aviso "Debe — no viaja")

Se agrega `ReservaDto.IsWithinUnpaidAlertWindow`, calculado server-side con `ReservaUnpaidAlertWindow.IsWithin(...)` (clase pura). True cuando: notificaciones habilitadas (`EnableUpcomingUnpaidReservationNotifications`) Y deuda del cliente (`Balance > 0`) Y la fecha de SALIDA (`StartDate`, decisión H5) cae en `[hoy ... hoy + UpcomingUnpaidReservationAlertDays]`. Reusa **exactamente** la misma config y regla de ventana que el job nocturno (`OperationalFinanceMonitorService`), para que el flag del DTO y la notificación nunca diverjan.

## Placeholder fiscal (requiere firma del contador)

Facturar tarde: el sistema **ya** emite con fecha de hoy y datos fiscales actuales (flujo ARCA existente, no se tocó). Un experto fiscal recomendó a futuro guardar un **"snapshot fiscal al vender"** (condición frente a IVA, tipo de comprobante, etc., congelados al momento de la venta) para que una factura emitida tarde refleje la situación fiscal correcta. Eso es trabajo posterior.

> **Gate del contador**: la conducta fiscal de "facturar tarde" (emitir con fecha y datos actuales una venta firme de hace días/semanas, posiblemente ya en viaje o finalizada) **requiere firma de un contador matriculado antes de producción**. Este ADR habilita el flujo en el software; la validez fiscal del comprobante tardío es decisión profesional, no del sistema.

## Migración

**Ninguna.** El carril es derivado (no hay columna), el flag es calculado y la allow-list es código. No hay cambio de esquema.

## Consecuencias

- El front (lote separado, con gate UX): quitar el botón "Reabrir para facturar", mostrar el carril de facturación, habilitar "Facturar" en `Closed`/`Traveling`, y usar `IsWithinUnpaidAlertWindow` para el aviso "Debe — no viaja". El backend queda listo (`CanInvoiceSale = true` en `Closed`/`Traveling`, `InvoicingStatus` e `IsWithinUnpaidAlertWindow` en el DTO).
- La bandeja de facturación crece (ahora incluye `Traveling`/`Closed` sin factura). Esto es deseado.
- Riesgo fiscal de facturar tarde: pendiente de firma del contador (ver arriba).
