# 2026-06-18 — Barrido sistemático de errores de lógica (10 arreglos)

Gastón pidió dejar de apagar incendios de a uno y **arreglar TODOS los errores de lógica que existen
y él no estaba viendo, de una sola vez**. Se hizo una auditoría sistemática (4 investigadores en paralelo
sobre plata/saldo, pagos/cobranza, máquina de estados, y cancelaciones/facturación) que encontró 10
errores reales, todos verificados en el código. Se arreglaron los 10, cada uno con tests, y el lote pasó
por revisión de backend + seguridad (**Approved, 0 bloqueantes**). Suite Unit: 1906/1906.

## Los 10 arreglos

1. **`0b2084c` — "Pagada y figura que debe"** (el que reportó Gastón). Los carteles "Pagada"/"con deuda"
   y el bloqueo de cierre pedían saldo EXACTAMENTE cero. Una reserva pagada de más (saldo a favor) o con
   un centavo de redondeo figuraba con deuda. Ahora usan el criterio canónico con tolerancia de redondeo.
2. **`1ab2842` — No facturar reservas canceladas/finalizadas.** El control de "qué se puede facturar" era
   una lista negativa que dejaba pasar Cancelada y Finalizada → se podía emitir un CAE real sobre una
   venta anulada. Ahora es lista positiva (solo Confirmada/En viaje/A liquidar). Las notas de
   crédito/débito siguen permitidas sobre canceladas. **Ver "Decisión pendiente" abajo.**
3. **`c003ec2` — Reserva colgada esperando devolución del operador.** Quedaba para siempre en
   "esperando devolución" salvo que el cliente usara su saldo a favor. Ahora cierra sola cuando el
   operador devuelve todo lo esperado (decisión del dueño: automático). No toca el saldo a favor.
4. **`449b6db` — Estado de servicio leído por "palabra suelta".** "A confirmar"/"sin emitir"/
   "desconfirmado" se leían como Confirmado (Contains). Ahora ancla al inicio (StartsWith).
5. **`ab72d6e` — Tope de reembolso duplicado.** Cancelar dos servicios del mismo operador por separado
   inflaba el tope → se podía acreditar al cliente más de lo recibido del operador. Ahora descuenta lo ya
   asignado.
6. **`41dd502` — El sobrepago no iba al bolsillo del cliente** por el cobro de la pantalla vieja ni al
   restaurar un cobro. Se unificó la conversión en un helper compartido (un solo lugar para los 3 caminos).
7. **`5b0d76a` — Recibos con número duplicado.** Numeración por Count()+1 sin índice único → dos
   simultáneos podían repetir número. Ahora índice único + reintento atómico. **Necesita chequeo pre-deploy
   (abajo).**
8. **`b14bcc7` — El lead no se ganaba solo.** Solo se marcaba "Ganado" en la transición manual; la
   auto-confirmación, el job y el revert no lo disparaban. Ahora un hook idempotente cubre todos los caminos.
9. (incluido en `41dd502`) **Restaurar un cobro** no reconstruía el saldo a favor.
10. **`0c89624` — Deshacer un saldo a favor aplicado a otra reserva (FC4).** No había vuelta atrás por
    código (solo a mano en la base). Se agregó la reversa (backend + endpoint) con guards de plata. La
    pantalla/botón queda como follow-up (gate UX).

## Decisión pendiente (de negocio, no bloqueante) — del arreglo #2
Al pasar a lista positiva, una reserva **ya Finalizada (Closed)** ya **no se puede facturar directo**
(antes la lista negativa la dejaba). Esto está alineado con lo que la auditoría (ADR-033 Parte C, D-B5)
ya había decidido: **una factura tardía sobre una reserva finalizada se hace reabriéndola a "A liquidar"
primero** (pendiente firma del contador). Hoy no tiene impacto práctico (una reserva se finaliza sin
deuda). A confirmar con Gastón/contador si está OK ese camino (reabrir a "A liquidar" para facturar tarde).

## Pendientes para el deploy (los corre Gastón)
- **Migración `Adr034_M1`** (índice único de número de recibo): ANTES de aplicarla, correr el chequeo de
  duplicados (está en el header de la migración):
  `SELECT "ReceiptNumber", COUNT(*) FROM "PaymentReceipts" GROUP BY "ReceiptNumber" HAVING COUNT(*) > 1;`
  Debe dar 0 filas. Si no, renumerar/anular los duplicados a mano (decisión fiscal) antes de aplicar.
- **Integración Postgres** (`run-tests-all` en el VPS): valida atomicidad/transacciones que InMemory no
  cubre (cierre por refund, reversa FC4, índice único real, conversión de sobrepago).
- Desplegar código + migración juntos.

## Follow-ups anotados (no bloqueantes)
- Pantalla/botón para la reversa FC4 (gate UX).
- Tests extra sugeridos por seguridad: ownership y flag-OFF en la reversa FC4; concurrencia xmin (Postgres).
- Borde latente (no alcanzable hoy): si una reserva tuviera varias cancelaciones en "esperando refund", el
  cierre automático mira un solo BC; revisar cuando se complete la cancelación parcial multi-BC.
