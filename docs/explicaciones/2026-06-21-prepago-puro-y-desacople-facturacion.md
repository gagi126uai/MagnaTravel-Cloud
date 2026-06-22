# 2026-06-21 — Prepago puro (ADR-036) + Desacople de facturación (ADR-037)

Sesión de rediseño de fondo del ciclo de vida de la reserva, pedida por Gastón.
Dos bloques grandes, ambos commiteados en `main` (sin pushear, sin desplegar).

## Por qué se hizo

Gastón marcó que "el sistema no respeta ningún ERP" y, sobre todo, que había un
**malentendido de vocabulario y de modelo**:

- **"Cancelar" ≠ "Anular".** En su rubro, *cancelar* = el cliente paga el total (saldar);
  *anular* = dejar sin efecto el viaje. El sistema usaba "cancelar" para deshacer, mal.
- **"A liquidar" no existe.** Fue un invento de un rediseño viejo. En su negocio **todo es
  prepago**: el cliente paga 100% y la agencia paga 100% al operador **antes** del viaje.
  No hay liquidación posterior.

## ADR-036 — Prepago puro

1. **Eliminado el estado "A liquidar" (ToSettle)** del ciclo. Migración `Adr036_M1` re-mapea
   las reservas que estuvieran en ese estado (saldada → Finalizada; con deuda → Confirmada) y
   recrea el CHECK constraint con 10 valores. (Validar contra Postgres real antes de desplegar.)
2. **"En viaje" = solo lectura total.** No se edita (ni con autorización), no se cobra, no se
   factura. Se tapó además un hueco: las asignaciones pasajero↔servicio ahora también respetan
   el estado.
3. **Candado de pago para viajar.** Para pasar a "En viaje" el **cliente** tiene que estar 100%
   pagado (candado duro, en el pase manual y en el automático). El **operador** es solo aviso
   (no traba; la deuda al operador hoy es por proveedor, no por reserva). Queda pendiente un
   casillero "pagado al operador" por servicio para endurecerlo después.
4. **Una reserva con plata viva no se da de baja simple**: solo se "Anula" por el camino formal
   (Nota de Crédito total + Nota de Débito por la multa). El flujo fiscal firmado por el contador
   no se tocó.
5. **Vocabulario y pantallas**: "Cancelar/Cancelada" → "Anular/Anulada" donde significa deshacer.
   Las reservas Anulada/Perdida quedan como pantalla de solo lectura (un cartel chico, sin
   botones). Los servicios de una reserva deshecha dicen "Anulado".

## ADR-037 — Desacople de facturación (estilo SAP/Odoo/NetSuite/Dynamics)

Gastón pidió que la facturación funcione "como en los ERP grandes". Validado con un experto:
la factura es un documento con vida propia y el "estado de facturación" es un **carril aparte**,
no algo atado al estado de la reserva. Gran parte ya existía (el cobro ya estaba desacoplado por
ADR-033, y el cálculo de "cuánto facturé" ya existía).

- **Carril de facturación derivado**: "Sin facturar / Facturada en parte / Facturada total",
  calculado al vuelo desde las facturas vivas (no se guarda ningún campo nuevo).
- **Se factura en cualquier estado no anulado** (Confirmada, En viaje, Finalizada). Decisión de
  Gastón: la factura de venta se habilita **desde Confirmada** (no antes).
- **Muere "Reabrir para facturar"**: ya no hace falta reabrir/destrabar nada; se factura directo
  desde Finalizada. (Supersede la decisión P1=B de ADR-036.)
- **Pantallas**: chip "Factura: …" siempre visible junto al chip de cobro; el botón "Emitir
  factura" se oculta cuando la reserva ya está facturada del todo; el aviso "Debe — no viaja"
  ahora respeta la ventana de días de aviso ya configurada.

### Nota fiscal (pendiente de firma del contador)

Facturar en un mes posterior ("facturar tarde") emite con fecha de hoy y datos fiscales actuales.
Lo prolijo a futuro es guardar una "foto fiscal" al momento de vender. **El contador debe firmar**
en qué período se declara el IVA al facturar tarde y hasta cuántos meses de atraso se toleran,
antes de que esto opere en producción. No se implementó snapshot fiscal en esta tanda.

## Estado y pendientes

- HEAD `main` = `d02afdc`. Commits: `db5b572` (ADR-036), `bf6bac0` (ADR-037 backend),
  `5b8888a` (ADR-037 front), `d02afdc` (limpieza post-review).
- Backend 2266/2266, front 711/711, build verde. Reviews backend + seguridad + frontend: Approved.
- **Pendiente**: push + deploy (lo hace Gastón; validar la migración `Adr036_M1` contra Postgres
  real antes); firma del contador para "facturar tarde"; feature "pagado al operador por servicio";
  follow-up del chip de facturación en el listado.
