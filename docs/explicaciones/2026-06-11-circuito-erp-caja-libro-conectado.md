# 2026-06-11 — Circuito ERP de plata conectado (Libro de Caja) — ADR-022

## Qué pidió Gastón

"Terminar toda la cadena de conexiones entre reservas / proveedores / clientes / caja porque lo veo desconectado; que funcione de verdad como un ERP (la parte del negocio y la lógica)."

## Qué estaba cortado (diagnóstico verificado en código)

1. **La caja era un espejo, no un libro**: tesorería unía al vuelo cobros + pagos a proveedor + movimientos manuales en cada consulta. Sin registro propio, sin arqueo posible, y editar/borrar un pago cambiaba el pasado sin rastro.
2. **Servicios genéricos no actualizaban la deuda del proveedor**: el cálculo los incluía, pero el saldo cacheado del proveedor quedaba desactualizado porque ReservaService nunca disparaba el recálculo (solo los servicios tipados vía BookingService).
3. **Pagos a proveedor sin imputación**: bajaban la deuda global sin decir de qué reserva eran → conciliación por expediente imposible.
4. **Movimientos manuales solo en pesos** (Currency hardcodeado ARS) y podían duplicar hechos que ya tienen puerta propia.
5. **Tesorería y dashboard calculaban AR/AP por caminos distintos** (números que podían no cerrar); tesorería no mostraba cuentas por pagar.
6. **Cuenta del cliente**: suma al vuelo del saldo escalar, sin detalle por moneda ni saldo a favor visible.

## Decisiones de negocio de Gastón (2026-06-11, vía preguntas con recomendación)

1. **NO pago a cuenta del cliente**: todo cobro sigue imputado a UNA reserva.
2. **Pago a proveedor: imputado a reserva concreta** como caso normal + opción explícita de **anticipo a cuenta** (sin reserva). Legacy con ReservaId null tolerado.
3. **Saldo a favor del cliente = UN bolsillo por moneda** (cancelación + sobrepago, unificado sobre ClientCreditEntry).
4. **Arqueo/apertura/cierre de caja: fase posterior** (el libro queda preparado).
5. **Sobrepago** → el excedente pasa al bolsillo del cliente; la reserva queda en 0.
6. **Ocultar TODO lo que sea costo** a usuarios sin permiso ver-costos (pagos a proveedor + devoluciones de operador + totales de salida de caja).

## Qué se construyó (ADR-022, backend completo)

**Regla madre**: todo hecho económico genera UN asiento persistido e inmutable en el Libro de Caja (`CashLedgerEntry`), en su moneda real, atado a su origen (cobro / pago a proveedor / movimiento manual — los de cancelación entran vía su movimiento manual existente, sin doble conteo). Nada se borra: se anula con contra-asiento (reversa). Los saldos son derivados; el libro es la fuente de verdad de la caja.

- **Capa 1**: entidad CashLedgerEntry + migración `Adr022_M1_CashLedger` (EF aditiva, sin SQL crudo — lección M2). CHECKs (monto>0, dirección, exactamente-un-origen), índices únicos parciales (un asiento vigente por origen). Currency en ManualCashMovement y ClientCreditEntry. FKs de ClientCreditEntry relajadas a nullable + origen "sobrepago".
- **Capa 2**: asientos cableados en el mismo SaveChanges del hecho en PaymentService (crear/editar/borrar con ciclo marcar-viejo→reversa→nuevo), SupplierService, TreasuryService (manuales), OperatorRefundService (moneda real del refund), ClientCreditService (moneda del crédito padre). Backfill idempotente `CashLedgerBackfillService` en Program.cs (corre tras el de multimoneda, no-abortante).
- **Sobrepago**: PaymentService crea el ClientCreditEntry por el excedente + un Payment puente negativo interno (AffectsCash=false, Method "SaldoAFavor", atado por OriginalPaymentId). Guard B5: créditos sin cancelación no disparan el cierre de BookingCancellation. Limpieza completa: borrar/editar el cobro fuente revierte crédito y puente (o bloquea si el crédito ya se usó); el puente NO es borrable/editable por API ni aparece en el listado de pagos.
- **Fix P1**: `SupplierDebtPersister` sin estado (patrón ReservaMoneyPersister); ReservaService recalcula la deuda del proveedor en alta/edición/borrado de servicios genéricos (proveedor viejo + nuevo en cambio).
- **Imputación P4**: pago a proveedor pide reserva (valida servicios de ese proveedor y tope de deuda por reserva/moneda) o flag explícito `IsAdvanceToAccount`.
- **T3**: movimientos manuales con categoría que duplique cobro de cliente / pago a proveedor → rechazados (puerta única por hecho).
- **Capa 4**: tesorería lee del libro (reversas como filas con signo; totales netean correcto). Enmascarado ver-costos NUEVO en tesorería: pagos a proveedor + refunds de operador + CashOut totales, fail-closed.
- **Capa 7**: `FinancePositionService` = fuente única AR/AP por moneda para dashboard Y tesorería (tesorería ahora incluye cuentas por pagar). Dashboard y tesorería dan el mismo número.
- **Capa 8**: cuenta del cliente con saldo a cobrar por moneda (desde ReservaMoneyByCurrency) + bolsillo de saldo a favor por moneda (ClientCreditEntry activos, cualquier origen). DTOs solo aditivos.
- **Reportes**: el puente interno no ensucia revenue/caja-por-día (filtro AffectsCash).

## Proceso (cadena de agentes)

domain-expert (modelo objetivo + verificación de cortes) → architect (ADR-022) → architect-reviewer round 1 (Changes Required, 4 bloqueantes) → architect fix → re-review round 2 (1 punto B5) → architect cierra B5 → **Ready** → backend-senior en 3 tandas → backend-reviewer (Approved with comments) + security-reviewer round 1 (Changes Required: S1 crédito fantasma) → fix S1+S2+comentarios → security round 2 (S1-bis: puente borrable por API) → fix → security round 3 (**Approved with comments**).

## Estado de pruebas

- Suite unitaria: **1390/1390 verde** (eran 1322 al arrancar; +68 de ADR-022).
- **PENDIENTE OBLIGATORIO antes del deploy**: tests de integración contra Postgres real (Docker/VPS): Up/Down de `Adr022_M1`, índice único parcial bajo el ciclo editar/anular, backfill idempotente real. Es la lección del incidente M2.

## Cambios visibles para Gastón (con el frontend actual, sin tocar pantallas)

- Tesorería: las anulaciones aparecen como filas propias con signo invertido (antes desaparecían). Aparece "cuentas por pagar" en el resumen (API; pantalla lo mostrará tras UX gate).
- Dashboard: el "saldo pendiente" puede cambiar de número (cotizaciones/perdidos ya no cuentan como por-cobrar — ahora es coherente con tesorería).
- Reserva sobrepagada: "Recaudado" muestra lo que el cliente pagó de verdad; el excedente vive en la ficha del cliente.

## Pendientes

1. **Deploy acumulado en main** (VPS sigue atrás): migraciones Adr021_M1 + Adr022_M1 + ambos backfills corren juntos en el contenedor `migrate`. Vigilar como la M4. Antes: correr integración Postgres.
2. **UX gate con Gastón** para las pantallas que muestran lo nuevo: cuentas por pagar en tesorería, bolsillo de saldo a favor en ficha cliente, imputación de pago a proveedor (elegir reserva / anticipo), presentación de anulaciones, y todo el paquete multimoneda capa 7 (mockup v3 ya hecho, pendiente OK).
3. Frontend de todo lo anterior tras el gate.
4. Fase 2 declarada en el ADR: arqueo/apertura/cierre de caja; conciliación facturado-vs-vendido como pantalla; visualización saldo a favor con proveedor por moneda.
5. No-bloqueantes anotados por reviewers: observabilidad del backfill (hoy solo log), concurrencia sin xmin en ClientCreditEntry, LedgerSourceType como etiqueta visible.
