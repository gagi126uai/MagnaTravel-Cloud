# 2026-06-24 — Lote chico: admin directo + bugs de anulación

Investigación previa (ERP + auditoría de código + fiscal) en
`2026-06-24-investigacion-proveedor-aprobaciones-admin.md` (resumen) y memorias de sesión.
Acá va lo CONSTRUIDO en este lote. Lo grande (pantalla del proveedor, módulo de proveedores
como cuentas por pagar, PDF de la ND, limpieza de Solicitudes) quedó parqueado con diseño.

## 1. El Admin hace todo directo, con rastro de auditoría

Problema: el dueño es el único usuario (rol Admin). Quedaban 3 acciones donde el sistema le
exigía "doble firma" (4-eyes) que él mismo se auto-aprobaba → fricción sin control real.

Fix: el Admin **saltea** la doble firma en esos 3 gates, pero **siempre** deja un evento de
auditoría `AdminSelfAuthorized` (con motivo, monto, moneda, usuario) — el control compensatorio
que reemplaza al 4-eyes para el contador. Reversible por rol: el día que haya varios admins se
reactiva la doble firma por policy, sin tocar código.

Gates abiertos (solo para rol Admin, resuelto server-side, nunca por un flag del request):
- Reintegro de saldo al operador (`ClientRefundReversal`, `ClientCreditService`).
- Confirmar la multa del operador sin respaldo documental (`ConfirmPenaltyAsync`).
- Forzar una cancelación con override (`ConfirmAsync` path `IsAdminOverride`).

NO se tocó: el bypass de permisos/ownership del Admin (ya era total). NO se debilitó ningún
gate fiscal que no se pidió: el gate de NC parcial de servicios no-Hotel (`INV-FC1.3-007`) sigue
exigiendo respaldo aun para Admin (test de blindaje agregado).

Seguridad: revisado, **Approved** (sin bloqueantes). Cada bypass deja rastro; ningún no-admin
puede saltear.

## 2. Al anular el viaje completo, los servicios quedan "Cancelado"

Problema: al anular toda la reserva, los servicios seguían "Confirmados" (solo se marcaban al
cancelar un servicio suelto). Por eso una reserva en devolución mostraba servicios "Confirmada"
y parecía trabada.

Fix: `CancelAllReservaServicesAsync` marca los 6 tipos de servicio como Cancelado (vuelos con
código IATA "UN", resto con `WorkflowStatuses.Cancelado`) al confirmar la anulación, atómico e
idempotente. Criterio ERP confirmado: el servicio queda Cancelado; el "esperando reembolso del
operador" es estado de la RESERVA (`PendingOperatorRefund`), no del servicio. No se inventó un
estado nuevo de servicio.

## 3. Chip "Esperando reembolso"

El estado `PendingOperatorRefund` se mostraba crudo en listados. Se mapeó en `ReservaStatusBadge`
con label "Esperando reembolso" (rosa).

## 4. Moneda de la multa del operador

Problema: al confirmar la multa no se indicaba la moneda (al operador se le paga en USD; la
multa puede ser USD o ARS).

Fix: selector ARS/USD en el panel de confirmar multa (default USD) + campo `PenaltyCurrency` en
`ConfirmPenaltyRequest` y columna en `BookingCancellationLine` (migración `Adr038_M1`, aditiva,
backfill = moneda de la línea). **Es solo REGISTRO**: NO cambia la moneda en que se emite la ND
al cliente (eso sigue como hoy; el wire FX de la ND es follow-up que requiere firma del contador).
La pantalla aclara esto con un hint. Doc cruzada entre `PenaltyCurrency` (ISO: USD/ARS) y el
preexistente `PenaltyCurrencyAtEvent` (ARCA: DOL/PES) para que no se cableen cruzados.

## Verificación
- Backend: build 0 errores; unit 2411/2411 (los Integration requieren Postgres → VPS).
- Frontend: build de Vite OK; lógica pura 37/37.
- Revisores: seguridad Approved; backend Changes Required → cerrado con los 2 tests de blindaje
  de los bypass de admin; frontend Approved (hint de copy agregado).

## Pendiente
- Gate de Gastón: deploy (migración `Adr038_M1` se suma a la cola `Adr036_M2` + `Adr037_M1`;
  validar Up/Down + backfill en Postgres en el VPS).
- Contador: re-preguntar si la ND pass-through al cliente es compatible con el criterio firmado
  el 1/6 (no reabre la decisión; confirma compatibilidad). Único bloqueante fiscal.
- Parqueado con diseño: pantalla de la pata del proveedor, módulo de proveedores como cuentas
  por pagar, PDF de la ND, limpieza de "Solicitudes".
