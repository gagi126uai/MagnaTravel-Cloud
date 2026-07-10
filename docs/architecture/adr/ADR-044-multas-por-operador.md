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

## Addendum T2 (2026-07-10) — dos decisiones de arquitectura que destraban la construcción

Contexto: la especificación contable firmada de T2
(`.claude/agent-memory/travel-agency-accountant-argentina/adr044-t2-operator-deduction-kind-spec.md`)
dejó T2 "apto CON CONDICIONES", con 2 gaps de arquitectura sin resolver (puntos 5 y 6 de esa
memoria). Este addendum los cierra. Código real inspeccionado para decidir:
`BookingCancellationLine.cs`, `FiscalSnapshot.cs`, `SupplierInvoicingMode.cs`,
`SupplierAccountStatementBuilder.cs`, `BookingCancellationService.cs` (sitios donde se construye
`FiscalSnapshot` y donde se asigna `RefundCap`), `PenaltyOwnership.cs`, `CancellationConceptKind.cs`,
`BookingCancellationLineBackfillService.cs`.

### Hecho verificado que cambia el diagnóstico de partida

`FiscalSnapshot.InvoicingModeAtEvent` (el campo que hoy vive en el BC padre) **nunca se asigna en
código de producción**: los 3 sitios reales donde se construye `new FiscalSnapshot { ... }` en
`BookingCancellationService.cs` (líneas 284, 1009 y 1474) no lo setean. Solo aparece asignado en
tests. Es decir: en producción ese campo es SIEMPRE `null`, y `FiscalLiquidationCalculator.cs:61`
ya resuelve esto con el patrón `input.InvoicingModeAtEvent ?? input.Supplier.InvoicingMode` — hoy
el sistema real SIEMPRE lee el modo vivo del `Supplier`. Este hecho cambia el riesgo de la decisión
A: no se trata de "romper un snapshot que funciona", sino de recién construir por primera vez el
snapshot que el diseño original (ADR-009) quiso tener pero nunca cableó.

También verificado: el mismo problema de granularidad ("dato por operador, congelado a nivel BC
padre único") ya existió antes para OTRO eje — `PenaltyOwnership` (BC padre, "quién se queda la
multa") — y YA se resolvió en ADR-025 moviendo ese eje a `BookingCancellationLine.ConceptKind`
(por línea). Las decisiones de abajo repiten ese mismo movimiento ya probado en este repo, no
inventan un patrón nuevo.

### Decisión A — Snapshot de `InvoicingMode` A NIVEL LÍNEA, con fallback vivo (no lectura en vivo pura)

**Recomendación única**: agregar `BookingCancellationLine.SupplierInvoicingModeAtEvent`
(`SupplierInvoicingMode?`, nullable), asignado UNA vez al construir la línea (mismo momento en que
hoy se fija el default de `ConceptKind`), copiando `Supplier.InvoicingMode` vigente en ese instante.
Toda lectura de gate usa `line.SupplierInvoicingModeAtEvent ?? line.Supplier.InvoicingMode` —
idéntico idiom al ya usado en `FiscalLiquidationCalculator.cs:61`.

**Por qué esta y no lectura en vivo pura**: una vez que ya hubo movimientos de plata reales sobre
una línea (`PenaltyRetained`, `RefundReceived`, `OperatorDeductionInvoiced`), si el admin cambia
`Supplier.InvoicingMode` después, una lectura 100% en vivo reinterpretaría el extracto histórico
sin que haya cambiado ningún dato real — mismo riesgo de integridad de auditoría que justificó el
`FiscalSnapshot` original. El snapshot por línea evita eso y es coherente con la filosofía "snapshot
al momento del evento" que ya rige el resto del módulo.

**Por qué no rompe nada y no exige backfill bloqueante**: como el campo equivalente a nivel padre
nunca estuvo poblado en producción, toda línea existente (histórica o recién creada antes de este
cambio) queda con el nuevo campo en `null` → cae al fallback vivo → **comportamiento idéntico al
de hoy, cero regresión**. Backfill opcional (no bloqueante): extender
`BookingCancellationLineBackfillService` para estampar `SupplierInvoicingModeAtEvent` en las líneas
sintéticas que ya crea, por prolijidad, no por necesidad.

**Migración esperada**: `Adr044_M_T2a_AddSupplierInvoicingModeAtEventToBookingCancellationLine` —
una columna nullable, sin backfill obligatorio, sin migración de datos.

**Uso del gate**: antes de aceptar cualquier deducción sobre una línea (Decisión B) o cualquier
circuito `FacturadaAparte`, el servicio valida
`line.SupplierInvoicingModeAtEvent ?? line.Supplier.InvoicingMode == SupplierInvoicingMode.CommissionOnly`
→ si es `CommissionOnly`, bloquear y enrutar a revisión manual con el mismo
`ReviewRequiredReason`/patrón que ya usa `IsCommissionOnlyLiquidation` del lado cliente (gap 4 de
la memoria de T2, hoy confirmado ausente en `SupplierCancellationCircuitReader.cs`).

### Decisión B — Cargos de operador 1:N por línea (tabla hija), no escalar
> **Corregida (2026-07-10) tras rechazo del software-architect-reviewer.** La versión inicial usaba
> UN solo agregado derivado (`PenaltyAmount = SUM(Kind!=Withholding)`) que rompía la invariante
> `RefundCap + PenaltyAmount == capBeforePenalty` en 3 sitios de plata. Esta versión separa el eje
> cliente del eje retención de caja (B1), acota la moneda (B2), fija la semántica de columna física
> (B3) y renombra la entidad para evitar colisión con el `DeductionKind` existente (M1). Todo
> verificado contra código.

**Recomendación única**: tabla hija nueva `BookingCancellationLineOperatorCharge` (N filas por
`BookingCancellationLine`), NO extender los campos escalares existentes de la línea.

**M1 — por qué NO se llama `...Deduction`**: ya existe `DeductionKind`
(`src/TravelApi.Domain/Entities/DeductionKind.cs`, ADR-002/FC1) usado en `OperatorRefundService.cs`
para tipificar lo que el operador retiene AL DEVOLVER FONDOS en un refund (`AdministrativeFee=1`,
`CancellationPenalty=3`, `IvaWithholding=10`, ...). Reusar el nombre `Deduction`/`DeductionKind`
para el eje NUEVO (multa por operador en la cancelación) generaría colisión conceptual y de
autocompletado en un área de plata. Por eso la entidad se llama `BookingCancellationLineOperatorCharge`
y su enum de tipo `OperatorChargeKind` (no `OperatorDeductionKind`). Dejar comentario cruzado en
ambos enums (`DeductionKind` ←→ `OperatorChargeKind`) aclarando que son ejes distintos.

**Por qué 1:N y no escalar-con-`Kind`-simple**: el contador CONFIRMÓ (no es hipótesis) que un
operador RI real aplica cargo administrativo Y retención fiscal SIMULTÁNEOS sobre la misma
cancelación, y que la retención NUNCA debe confundirse con el cargo administrativo (una es crédito
fiscal de la agencia, la otra es pérdida real que baja el `RefundCap`). Con un campo escalar, esos
dos montos de distinta naturaleza fiscal quedarían forzados a un solo `Kind` por línea — o se
mezclan mal (viola la regla dura del contador), o se fuerza a partir el mismo servicio en dos líneas
sintéticas (rompe la semántica de `ServiceId`/agregación por operador de `RefundCap`). Diferir esto
a "backlog" en un área fiscal/plata para un caso que el contador ya dijo que ES real, no edge case,
es la opción insegura. Construir la tabla hija ahora evita la migración dolorosa de después.

**No es sobreconstrucción**: modela exactamente la multiplicidad que el contador ya verificó, con el
mismo tipo de tabla hija que el proyecto ya usa (líneas de cancelación, allocations).

**Modelo de datos**:
```
BookingCancellationLineOperatorCharge
  Id (int, PK)
  PublicId (Guid)
  BookingCancellationLineId (FK -> BookingCancellationLine, cascade delete, igual que Line->BC)
  Kind (OperatorChargeKind: AdministrativeFee=0 default | Tax=1 | Withholding=2 | Other=3)
  CollectionMode (PenaltyCollectionMode: Retenida=0 default | FacturadaAparte=1)
  Amount (decimal)
  Currency (string, MaxLength 3 — default = Line.Currency A SECAS [re-review 2026-07-10]: el
    default anterior (PenaltyCurrency ?? Currency) violaba el CHECK B2 cuando PenaltyCurrency
    difiere de la moneda de la línea. Un charge Retenida DEBE estar en Line.Currency porque
    RetainedDeductionAmount se resta de RefundCap, que está en Line.Currency — restar USD de un
    cap ARS sería mezclar unidades. La retención genuinamente cross-currency (operador retiene
    USD sobre línea ARS) NO se modela como charge que netea: igual que hoy, donde
    AllocateConfirmedPenaltyToLinesAsync solo netea líneas cuya Currency == penaltyCurrency
    (BookingCancellationService.cs:6496-6509) y el cruce de monedas se resuelve en T3/P4.)
  DocumentRef (string?, MaxLength acorde al resto)
  Notes (string?)
  ConfirmedByUserId / ConfirmedByUserName (auditoría, mismo patrón que el resto del módulo)
  ConfirmedAt (DateTime)
  CreatedAt (DateTime)

  CHECK chk_..._documentref_required_when_invoiced:
     CollectionMode <> FacturadaAparte OR DocumentRef IS NOT NULL
     (FacturadaAparte exige el documento del proveedor; mismo patrón que el CHECK de FiscalSnapshot)

  CHECK chk_..._currency_matches_line   [B2]:
     Currency = Line.Currency  (o Line.PenaltyCurrency cuando exista)
     Como un CHECK SQL no puede cruzar tablas en Postgres, se implementa como VALIDACIÓN DE
     SERVICIO EQUIVALENTE en el punto de escritura (mismo lugar que crea el charge), rechazando
     una moneda de charge distinta de la de su línea. Documentado acá como invariante dura: un
     charge SIEMPRE va en la moneda de su línea; nunca se mezclan ARS+USD dentro de la misma línea.
```

**B1 — DOS agregados derivados con nombre y semántica distintos (crítico, plata)**. La versión
inicial derivaba un solo `PenaltyAmount = SUM(Kind!=Withholding)`, lo que ROMPÍA la invariante
`RefundCap + PenaltyAmount == capBeforePenalty` que sostienen 3 sitios verificados:
`ReverseConfirmedPenaltyFromLinesAsync` (`BookingCancellationService.cs:6607-6616`, restaura el cap
sumando `PenaltyAmount` completo → con una charge `FacturadaAparte` devolvería al cap plata que nunca
se restó), `SupplierCancellationCircuitReader.cs:118` (etiquetaría como "Multa retenida por el
operador" plata que el operador nunca retuvo) y `OperatorRefundReadModelService.cs:290-300`
(`capBeforePenalty` sobreestimado). Por eso se separan DOS agregados derivados, ambos columnas
físicas de la línea:

- `Line.PenaltyAmount` = **eje CLIENTE** (ND / ADR-013): `SUM(charges con Kind != Withholding)`.
  Withholding es crédito fiscal interno de la agencia, nunca llega al cliente. Sin cambio de nombre;
  P3/P4 lo siguen leyendo tal cual en T3.
- `Line.RetainedDeductionAmount` (**NUEVO**) = **eje CAJA/RefundCap**:
  `SUM(charges con Kind != Withholding AND CollectionMode == Retenida)`. Es lo ÚNICO que resta del
  `RefundCap`. Withholding no resta (crédito fiscal, no pérdida); `FacturadaAparte` no resta (el
  operador devuelve el bruto, se cobra por AP).

Cambios puntuales en los 3 sitios (parte del alcance de T2):
- `AllocateConfirmedPenaltyToLinesAsync`: `RefundCap = capBeforePenalty − RetainedDeductionAmount`
  (hoy resta `PenaltyAmount`).
- `ReverseConfirmedPenaltyFromLinesAsync` (`:6607-6616`): restaura `RefundCap += RetainedDeductionAmount`
  (hoy suma `PenaltyAmount`). Con esto la invariante `RefundCap + RetainedDeductionAmount ==
  capBeforePenalty` se conserva exacta y el reverse nunca devuelve al cap plata `FacturadaAparte`
  o `Withholding` que jamás se restó.
- `SupplierCancellationCircuitReader.cs:118`: la línea "Multa retenida por el operador"
  (`PenaltyRetained`) usa `RetainedDeductionAmount`, no `PenaltyAmount` — solo pinta lo que el
  operador REALMENTE retuvo. Las charges `FacturadaAparte` se pintan como su propia línea de deuda AP
  (`OperatorChargeInvoiced`), no como retención.
- `OperatorRefundReadModelService.cs:290-300`: reconstruye `capBeforePenalty` con
  `RetainedDeductionAmount`.

**B3 — semántica de columna física (explícito)**. `Line.PenaltyAmount` y `Line.RetainedDeductionAmount`
siguen siendo COLUMNAS FÍSICAS PERSISTIDAS (NO propiedades calculadas sobre la colección de charges).
Se reescriben ÚNICAMENTE dentro de la misma transacción/`SaveChanges` que crea o modifica charges de
esa línea (en el método de servicio que hoy escribe `PenaltyAmount`). JAMÁS se recalculan por lectura
ni por un job que barra la colección: EF devuelve una colección vacía si el `Include` falta, y un
recálculo perezoso pondría en 0 las multas confirmadas históricas EN SILENCIO. Como la fuente de
verdad son las columnas físicas, el backfill legacy es genuinamente OPCIONAL (ver abajo): sin
charges hijas, los escalares ya persistidos siguen mandando y el comportamiento es el de hoy.

**`RefundCap` cambia de fórmula, no de lugar**: sigue siendo columna física persistida (fuente de
verdad, igual que hoy), pero se calcula restando `RetainedDeductionAmount` en lugar de `PenaltyAmount`
(ver B1).

**El caso simple sigue siendo 2 clics**: la pantalla actual (monto + moneda + concepto) no cambia
para 1 sola deducción — el backend crea UNA charge `Kind=AdministrativeFee` (default legacy, confirmado
por contador) `CollectionMode=Retenida`, transparente, y en la misma transacción escribe
`PenaltyAmount` y `RetainedDeductionAmount` (que coinciden en el caso Fee+Retenida). "Agregar otro
cargo de este operador" es acción secundaria OPCIONAL, no se muestra ni se pregunta por default.

**Migración esperada**: `Adr044_M_T2b_AddBookingCancellationLineOperatorCharges` — tabla nueva + FK +
índice por `BookingCancellationLineId` + los 2 CHECK; y `Adr044_M_T2c_AddRetainedDeductionAmountToLine`
(columna física nueva en la línea, default 0, backfill de líneas confirmadas legacy =
`RetainedDeductionAmount = PenaltyAmount` cuando `PenaltyStatus=Confirmed`, porque hoy TODA multa
confirmada es Fee retenida → los dos agregados coinciden para el histórico). Sin token de concurrencia
propio (la línea ya usa `xmin`). Backfill de charges OPCIONAL, no bloqueante: para líneas
`PenaltyStatus=Confirmed` y `PenaltyAmount>0`, sintetizar una charge legacy
(`Kind=AdministrativeFee`, `CollectionMode=Retenida`, `Amount=PenaltyAmount`) — mismo patrón
idempotente que `BookingCancellationLineBackfillService`.

**Qué queda para T3 (no se toca acá)**: la emisión de ND al cliente (P3/P4) sigue leyendo el agregado
escalar `Line.PenaltyAmount`/`ConceptKind` sin cambios; el desglose Fee/Tax/Withholding es
información NUEVA para el circuito del operador (T2), no cambia cómo se factura al cliente.

**Testing obligatorio antes de mergear T2** (nombrando los 3 sitios de plata):
1. Fee + Withholding en la misma línea → `RefundCap` descuenta solo la Fee; `Withholding` no reduce
   `RefundCap` ni el crédito del cliente.
2. Fee (Retenida) + otro cargo `FacturadaAparte` → `RefundCap` descuenta solo la Fee retenida; la
   `FacturadaAparte` genera deuda AP con `DocumentRef` y NO baja el cap.
3. **Invariante B1**: tras `AllocateConfirmedPenaltyToLinesAsync`,
   `RefundCap + RetainedDeductionAmount == capBeforePenalty` para mezclas Retenida+FacturadaAparte y
   Retenida+Withholding.
4. **Reverse (`ReverseConfirmedPenaltyFromLinesAsync`, `:6607-6616`)** con charges mixtas
   (Retenida+FacturadaAparte y Retenida+Withholding): restaura EXACTAMENTE `RetainedDeductionAmount`
   al cap, nunca la parte `FacturadaAparte`/`Withholding`; el cap vuelve a su valor previo al Allocate.
5. **`SupplierCancellationCircuitReader.cs:118`**: "Multa retenida" refleja `RetainedDeductionAmount`;
   una charge `FacturadaAparte` aparece como línea de deuda AP (`OperatorChargeInvoiced`), no como
   retención; una `Withholding` no aparece como retención.
6. **`OperatorRefundReadModelService.cs:290-300`**: `capBeforePenalty` reconstruido usa
   `RetainedDeductionAmount` (no sobreestima con `PenaltyAmount`).
7. **B2 moneda**: rechazar/validar una charge con moneda distinta de la de su línea (2 monedas en la
   misma línea no se suman en un escalar).
8. Gate `CommissionOnly` (Decisión A) bloquea/enruta a revisión manual cualquier charge sobre esa línea.
9. Línea legacy sin charges hijas (solo escalares) se comporta byte-idéntico a hoy;
   `SupplierInvoicingModeAtEvent` null cae al fallback vivo del `Supplier` sin excepción.

**Rollback**: las 3 migraciones son aditivas (columna nullable/tabla nueva + columna con default 0),
sin migrar datos destructivamente — `Down()` estándar (drop columna / drop tabla). El backfill de
`RetainedDeductionAmount = PenaltyAmount` es reconstruible (no destruye el escalar de origen).
