# ADR-044 — Rediseño integral de multas de operador (multa por servicio/operador)

**Estado: ACEPTADO por Gaston (2026-07-10).** Obra en curso, arranca por T0.

> Proceso: 6 investigaciones paralelas (modelo actual, circuito de plata, bandeja AFIP, dominio
> agencias con fuentes, patrones ERP con docs de fabricante, diagnóstico de 4 reservas en prod) →
> propuesta v1 → desafío doble: software-architect-reviewer ("Changes required", B1-B3 + M1-M4) y
> travel-agency-accountant-argentina (objeciones fiscales P3/P4/P2) → esta versión incorpora TODAS
> las correcciones. Detalle de los desafíos:
> `.claude/agent-memory/software-architect-reviewer/rediseno-multas-operador-review-round1.md` y
> `.claude/agent-memory/travel-agency-accountant-argentina/nd-pass-through-si-emite-nd-correccion-2026-07-10.md`.

## Decisiones finales de Gaston (2026-07-10, cierran la ronda de preguntas)
1. **Diferencia de cambio (plano tesorería): la asume el CLIENTE por default** (se le carga la
   multa convertida al TC del día del cargo), configurable por agencia.
2. **La pantalla "Pendientes AFIP" se DESARMA**: resolución en la ficha + monitor pasivo de
   comprobantes con nombre claro dentro de Facturación.
3. **Anulación parcial: AL FINAL de la obra** (T5), sobre el modelo nuevo ya probado.
4. **Confirmado por Gaston: la regla "multa trasladada al cliente sale como Nota de Débito" SÍ
   está hablada con su contador** — se construye encima sin re-consulta.

Fecha: 2026-07-10. Autor: orquestador (Fable), sobre 6 informes de investigación (modelo actual,
circuito de plata, bandeja AFIP, dominio agencias, patrones ERP con fuentes de fabricante, y
diagnóstico de 4 reservas en prod). Decisiones de negocio ya cerradas por el dueño (NO reabrir):
la multa puede venir RETENIDA del reembolso o FACTURADA APARTE según el operador (soportar ambas);
casi siempre se traslada al cliente (absorber existe pero no se optimiza); "conexión con operadores"
= (a) movimiento en su cuenta corriente + (b) multa por servicio/operador con su moneda + (c) ida y
vuelta documentado; esta obra es la prioridad #1.

## Diagnóstico condensado (base fáctica)

- El modelo YA tiene líneas por servicio/operador (`BookingCancellationLine`: SupplierId,
  PenaltyAmount, PenaltyStatus, PenaltyCurrency, RefundCap, DebitNoteInvoiceId) — ADR-025.
- PERO toda la capa visible/fiscal es SINGULAR: `BookingCancellation.PenaltyAmountAtEvent` /
  `PenaltyCurrencyAtEvent` (un monto, una moneda), multa imputada SOLO al operador principal
  (`AllocateConfirmedPenaltyToLinesAsync` filtra por bc.SupplierId), UN paso de multa en la ficha,
  UNA ND al cliente.
- La multa NETEA el RefundCap del operador (baja "me tiene que devolver") pero NO existe como
  movimiento tipificado visible en el extracto del operador, no hay documento del operador
  (ND/factura del proveedor), no hay registro de comunicación.
- Patrón ERP canónico (verificado SAP/D365/NetSuite/Odoo): penalidad del proveedor = movimiento AP
  contra ese proveedor (subsequent debit si factura aparte; refund por el neto si retiene);
  espejo al cliente = customer debit memo en AR; granularidad SIEMPRE por proveedor/línea;
  FX delta = resultado por diferencia de cambio separado del margen; e-invoice fallida = estado
  sobre el documento + retry + monitor pasivo (NO bandeja de decisión).
- Dominio agencias (verificado): multi-operador y anulación parcial son EL CASO COMÚN; reembolso
  esperado ≠ recibido siempre; deducciones deben tipificarse (fee/impuesto/retención fiscal).
- Bandeja "Pendientes AFIP" actual: 3 listas distintas bajo una URL, 2 títulos duplicados,
  los 3 tipos pueden quedar eternos (solo 1 tiene alertas), 1 endpoint reconcilia estados en el GET.
- Bug real encontrado (F-2026-1038): `ApplyTotalCreditNoteReversalAsync` crea el asiento de
  reversión por -ImporteTotal SIN verificar cobro real previo → factura emitida-sin-cobrar y
  anulada = deuda fantasma permanente ($726.000 en prod, usuario "test"). Fix de código +
  reparación de datos necesarios. (3 de las 4 reservas reportadas eran proyección vieja del bug
  de moneda ya reparado; se autocorrigen con el vigía.)

## Propuesta

### P1 — La multa vive en la LÍNEA (servicio/operador), no en la anulación
- Promover `BookingCancellationLine` a fuente de verdad única de la multa: monto, moneda ISO,
  estado (Estimated/Confirmed/Waived), **forma de cobro** nueva: `Retenida` (default) |
  `FacturadaAparte`, referencia al documento del operador (número/adjunto), notas.
- `BookingCancellation.PenaltyAmountAtEvent`/`PenaltyCurrencyAtEvent` quedan como agregados
  derivados SOLO para compatibilidad fiscal del snapshot del documento emitido; dejan de ser
  entrada de datos.
- El paso de la multa en la ficha pasa de "UN cartel" a "una fila por operador con multa
  pendiente" (misma familia visual actual, repetida por operador; si hay 1 sola, se ve igual
  que hoy — cero fricción en el caso simple).
- Confirmar multa = por operador (monto+moneda+forma de cobro+concepto). Waive = por operador.

### P2 — La multa ES un movimiento en la cuenta del operador
- `Retenida`: el extracto del operador muestra el reembolso esperado ORIGINAL, la multa como
  movimiento tipificado que lo reduce, y el neto a recibir. Conciliación esperado vs recibido
  por operador (hoy el neteo existe pero invisible).
- `FacturadaAparte`: el operador devuelve el total; la multa entra como DEUDA NUESTRA hacia el
  operador (cuenta a pagar, ADR-041), con su documento del proveedor referenciado. Se paga por
  el circuito de pagos a proveedor existente.
- Tipificación de la deducción del operador: **campo NUEVO `OperatorDeductionKind`**
  (administrativeFee | tax | withholding | other) — NO extender `CancellationConceptKind`, que es
  un enum fiscal firmado por contador matriculado (ADR-013 R4) con OTRO eje de clasificación
  (corrección B3). Retención fiscal NO baja el crédito del cliente. Requiere sign-off contable
  del campo nuevo antes de construir. Gatear el circuito "FacturadaAparte" por `InvoicingMode`
  (reseller vs intermediario) — hoy no se gatea y es bug latente conocido.

### P3 — Espejo al cliente: ND por FACTURA ORIGINAL (patrón ADR-042 real) [CORREGIDO B1]
- El traslado al cliente genera Notas de Débito agrupadas **POR FACTURA ORIGINAL activa** (no por
  moneda): cada ND hereda MonId/MonCotiz de SU factura y se asocia por CbtesAsoc — exactamente
  como ADR-042 hizo con las NC. La moneda de la ND es la de la factura del cliente (espacio
  ARCA), NO la moneda en que el operador retuvo (`PenaltyCurrency`, ISO): son espacios distintos
  y el cruce pasa por el criterio de conversión de P4. Cada renglón de la ND referencia la multa
  de origen (trazabilidad margen servicio a servicio).
- El guard actual que manda a revisión manual el mismatch de moneda ("ARREGLO 2", 2026-06-24) se
  REEMPLAZA por este diseño (conversión explícita con TC definido), no se rodea.
- `AlicuotaIvaId` de la ND: dejar de hardcodear 0% — parametrizar por concepto y condición
  fiscal del emisor (multi-condición RI/Mono/Exento). Necesita confirmación de contador para RI.
- Cada multa por operador decide su traslado: tal cual (default) | + fee de gestión | absorber.
  Absorber = sin documento, margen menor, registrado.
- Corrección post-emisión (el operador cambia el monto después de la ND): reusar el mecanismo ya
  firmado de ND/NC complementaria (R5, 2026-06-01) — no inventar uno nuevo.
- Verificar antes de construir: el código se autodeclara con una "regla fiscal cerrada (firmada)"
  sobre pass-through-emite-ND cuya firma/fecha no se pudo rastrear — confirmar con Gaston/contador.

### P4 — Diferencia de cambio: DOS planos separados [CORREGIDO por contador] — PRERREQUISITO de P3
- **Plano fiscal (comprobante)**: el TC de la ND/NC es SIEMPRE el congelado de la factura
  original (regla cerrada y firmada) — NUNCA se recotiza el documento. La conversión
  multa-del-operador-en-USD → cargo-al-cliente-en-la-moneda-de-su-factura usa un TC definido y
  auditable (TC real por fecha, norte BNA/ADR-011; hasta que exista, TC manual con
  justificación como hoy).
- **Plano tesorería/cuenta corriente**: el delta entre lo retenido por el operador y lo cargado
  al cliente se registra como ajuste tipificado de diferencia de cambio (no "descuadre"), en la
  cuenta corriente, SIN tocar comprobantes. Config por agencia: la asume el cliente (default)
  o la absorbe la agencia.
- P4 se construye JUNTO con P3 (fusionados en la misma tanda): P3 sin P4 no puede automatizar
  el caso típico "operador retiene USD, cliente facturado en ARS".

### P5 — Fin de la bandeja "Pendientes AFIP" como pantalla de decisión
- Los 3 contenidos actuales se redistribuyen:
  1. Cargos de cancelación → ya viven en la ficha (hecho 2026-07-08); la solapa queda como
     LISTA PASIVA con nombre claro ("Multas por resolver") o se funde en el monitor.
  2. NC por revisar (revisión manual) → misma lista pasiva + aviso con vencimiento RG 4540
     (ya existe el job de alertas; sumar el "vence en X días" visible).
  3. Recibos por regularizar → se mantiene (tiene acciones reales), renombrada para no duplicar
     título, CON alertas de antigüedad (hoy no tiene ninguna).
- Un solo "monitor de comprobantes" pasivo (estado + reintento + link a ficha), estilo cockpit
  ERP. Sacar la reconciliación de estados del GET a un job.
- Nombre de menú: dejar de llamarlo "Pendientes AFIP" (jerga); propuesta: "Comprobantes" o
  dentro de Facturación.

### P6 — Arreglos de plata inmediatos (van primero, antes del rediseño) [ESPECIFICADO B2]
- Fix: la reversión económica de una NC solo debe revertir PLATA REALMENTE COBRADA, en
  **AMBOS caminos** (NC total Y NC parcial). Fórmula del "cobrado real" de una factura:
  suma de Payments vivos de cobro imputados a esa factura (considerando
  ImputedAmount/ImputedCurrency de pagos cruzados ADR-021, y los puentes de crédito FC4 aunque
  no tengan RelatedInvoiceId — definir el criterio de imputación con el código real a la vista),
  MENOS los reversals previos ya emitidos sobre la misma factura (NC parciales repetidas).
  Cap = max(0, ese neto). Si 0 → no crear reversal. Tests obligatorios de los 4 casos: pagos
  parciales múltiples, pagos cruzados ARS↔USD, puente/sobrepago FC4, NC parciales sucesivas.
  OJO simetría: capear de menos invierte el bug (saldo a favor fantasma) — los tests deben
  cubrir ambas direcciones.
- Reparación de datos para F-2026-1038 (y barrido por otros casos iguales: facturas anuladas
  con reversal sin cobro real detrás) — misma mecánica de migración con backup que anoche,
  validada contra prod ANTES de pushear (lección 2026-07-09).
- Verificar que las otras 3 reservas quedaron en 0 tras el vigía.

### P7 — Anulación parcial (un servicio, no toda la reserva)
- Confirmado como operación diaria del rubro. Diseñarla dentro de este modelo (la línea ya
  existe: anular una línea = su NC parcial + su multa + su reembolso), construirla como fase
  final de la obra (la más grande).

## Orden de obra propuesto (tandas) [AJUSTADO M1/M2]
1. **T0 — plata**: P6 (fix reversal capeado al cobrado real, ambos caminos + reparación de datos
   validada contra prod antes de pushear + verificación de las 4 reservas).
2. **T1 — modelo**: P1 multa por línea end-to-end backend. Alcance HONESTO (no es "solo
   backend"): hay 6+ sitios que escriben los agregados del BC (confirmar/waive/revert/clasificar/
   inferir), cada uno se migra a la línea con test propio. El DTO de situación de multa pasa a
   LISTA ya en T1 y la ficha renderiza un panel por elemento (mapa trivial del componente
   actual) — así el multi-operador queda visible desde T1 en forma mínima y no hay "olvido
   silencioso"; el pulido UX real es T4 con gate de Gaston. Incluye: fix del bug VIVO de
   imputación solo-al-operador-principal (M2), test byte-idéntico del agregado derivado para BCs
   legacy mono-operador con línea sintética ServiceId=0 (M3), y el gate de estado del BC padre se
   mantiene al confirmar por línea (M4).
3. **T2 — operador**: P2 (extracto/cuenta del operador con la multa tipificada
   `OperatorDeductionKind` nuevo + forma de cobro retenida/facturada aparte, gateada por
   InvoicingMode). Sign-off contable del campo nuevo.
4. **T3 — cliente**: P3+P4 FUSIONADOS (ND por factura original multi-multa + conversión con TC
   definido + ajuste de diferencia de cambio en tesorería + IVA parametrizado). Si el TC BNA
   real no está construido aún, TC manual con justificación como hoy.
5. **T4 — ficha/UX**: paso de multa por operador pulido (gate UX con Gaston), monitor de
   comprobantes y fin de la bandeja (P5).
6. **T5 — anulación parcial** (P7): diseño detallado + construcción.
Cada tanda con los gates de siempre (backend/frontend/seguridad/exposición + fiscal donde toque).

## Preguntas abiertas para el dueño (única ronda, al final del desafío)
1. Diferencia de cambio: ¿default "la asume el cliente al TC del día de devolución"? (config).
2. ND al cliente: ¿agrupar por moneda (recomendado) o una ND por operador?
3. ¿Matamos el nombre/menú "Pendientes AFIP" y queda "Comprobantes" dentro de Facturación?
4. Anulación parcial al final de la obra (recomendado) ¿o antes?

## Seguimientos anotados por los reviewers de T0 (no bloquean T0; cerrar antes de su fase)
- **Lock por (reserva, moneda) en la reversión económica**: el cap del reversal lee los Payments
  sin lock; con 2 NCs de la misma reserva+moneda aprobadas por AFIP casi a la vez podría
  excederse lo cobrado. Hoy improbable (un solo usuario), pero T5/anulación parcial multiplica
  las NCs por reserva → cerrar ANTES de T5 (pg_advisory_xact_lock o transacción serializable,
  patrón de ADR-042) + test de concurrencia estilo Adr042MultiInvoiceConcurrency.
- **`matchedPayment` de la NC total no filtra por moneda** (matchea solo por monto exacto):
  ARS 1000 vs USD 1000 podría voidear el recibo equivocado. Preexistente; unificar con el
  criterio currency-aware del cap en T1.
- **Reserva interna 22 (dato de prueba)**: cobro vivo de 2.030 ARS sin imputación contra factura
  USD → cruce de monedas inconsistente que el vigía reportará; juzgar a mano o borrar la reserva
  de prueba (regla 2026-07-10: datos falsos no ameritan ingeniería fina).
- **`RelatedInvoiceId` nunca se llena en cobros reales** (solo LinkedInvoiceId informativo):
  deuda técnica preexistente; si algún día se puebla, el cap podría afinarse a nivel factura.

## Qué NO cambia (para no romper lo sano)
- NC por factura en su moneda (ADR-042), candado de coherencia de moneda de la ND, snapshot
  fiscal al momento del evento, saldo a favor por moneda, circuito "esperando reembolso" +
  registrar reembolso recibido (se enriquece con la multa visible, no se reemplaza).
