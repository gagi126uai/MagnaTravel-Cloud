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

## Addendum T3b (2026-07-10) — los 3 bloqueantes de arquitectura de "ND al cliente multi-operador/multimoneda"

Contexto: la especificación fiscal de T3
(`.claude/agent-memory/travel-agency-accountant-argentina/adr044-t3-nd-multi-operador-multimoneda-spec.md`)
partió la tanda en T3a (construible ya: 1 factura activa, sin cruce de moneda — en construcción aparte) y
T3b (bloqueada por 3 huecos de arquitectura + 1 firma contable ajena a esta obra). Este addendum cierra los
3 huecos de arquitectura. Código real inspeccionado para decidir: `InvoiceItem.cs`, `InvoiceService.cs`
(resolución de `activeInvoices` en `DraftAsync` y `ResolveAndPreflightInvoicesToAnnulAsync`,
`BookingCancellationService.cs:150-191` y `:7887-7929`), `CreateInvoiceRequest.cs`, `Payment.cs` (bloque
`ExchangeRate`/`ExchangeRateSource`/`ExchangeRateAt`, líneas 40-58), `BookingCancellationLineOperatorCharge.cs`
(T2), `BookingCancellationCreditNote.cs` (ADR-042), `CashLedgerEntry.cs` (ADR-022),
`OperatorRefundReceived.cs`, `ExchangeRateSource.cs`, `ReservaMoneyCalculator.cs`.

El 4to bloqueante de la spec de T3 — **alícuota IVA de pass-through para Responsable Inscripto** — NO se
resuelve acá: es firma pendiente del contador matriculado de Gastón (gap ya anotado en
`nd-pass-through-si-emite-nd-correccion-2026-07-10` y en la Matriz AlicuotaIvaId de la spec de T3). **Gate
humano**: sin esa firma, RI + pass-through sigue ruteando a revisión manual (Mono/Exento y RI+fee-propio NO
están bloqueados por esto, ya tienen regla firmada).

### Decisión 1 — Vínculo servicio -> factura destino: campo MANUAL en el cargo, NO automatizar `SourceServicioReservaId`

**Hecho verificado**: `InvoiceItem.SourceServicioReservaId` (línea 72 de `InvoiceItem.cs`) existe en el
modelo desde FC1.3/ADR-009 pero el grep confirma que **solo se asigna en tests** (`BookingCancellationServicePartialCreditNoteTests.cs`
y 3 archivos de integración más); en producción, cero facturas tienen ese campo poblado. Además apunta
ÚNICAMENTE a la tabla genérica `ServicioReserva`, no a las 5 tablas tipadas (Flight/Hotel/Transfer/
Package/Assistance) que usa `BookingCancellationLine.ServiceTable` — aunque se poblara, no alcanzaría para
resolver el vínculo de las líneas de cancelación reales.

**Hecho verificado sobre cómo se factura hoy**: `InvoiceService`/`CreateInvoiceModal.jsx` arman la factura
100% MANUAL (el usuario tipea/elige items, sin origen estructurado por servicio). `DraftAsync` y
`ResolveAndPreflightInvoicesToAnnulAsync` (`BookingCancellationService.cs:150-191`, `:7887-7929`) resuelven
`activeInvoices` = TODAS las facturas de venta vivas con CAE de la reserva (ADR-042 ya soporta N, típicamente
1 ARS + 1 USD). Facturar es SIEMPRE por-factura-completa-o-parcial-en-monto, nunca por-servicio-estructurado:
no hay ninguna tabla que diga "el servicio X está en la factura Y". Confirma el hallazgo de la spec: con 2+
facturas activas, hoy NO HAY FORMA MECÁNICA de saber a cuál factura pertenece el cargo de un operador.

**Opciones consideradas**:
- **(a) Poblar `SourceServicioReservaId` retroactivamente al facturar** (automatizar el vínculo). Descartada
  para esta tanda: exige tocar el flujo GENERAL de facturación (`CreateInvoiceModal.jsx` + `InvoiceService`),
  no solo cancelaciones; además no resuelve el problema de tipado (5 tablas vs 1 genérica) sin más trabajo. Es
  la automatización "correcta" a mediano plazo pero es una obra aparte, no un requisito de T3b.
- **(b) Heurística por moneda+monto** (adivinar la factura porque su moneda/monto matchea el cargo).
  Descartada: en el caso de 2 facturas de LA MISMA moneda con montos parecidos (ej. 2 facturas ARS por
  splits de pago) la heurística puede fallar en silencio y mandar una ND fiscal a la factura equivocada —
  riesgo fiscal inaceptable para adivinar algo que el usuario ya sabe con certeza.
- **(c) Campo manual nuevo en el cargo, resuelto por el humano en el momento de confirmar/facturar** (la
  spec de T3 ya lo proponía como `OperatorCharge.TargetInvoiceId`). **Recomendada.**

**Decisión**: agregar `BookingCancellationLineOperatorCharge.TargetInvoiceId` (`int?`, FK a `Invoice`,
nullable). Se resuelve así:
- **Caso simple (1 factura activa, el 95%+ de los casos)**: el campo se autocompleta con esa única factura,
  transparente — cero fricción, ningún desplegable visible (mismo principio que el resto del módulo:
  complejidad escondida con defaults).
- **Caso 2+ facturas activas**: el usuario elige la factura destino en un desplegable AL CONFIRMAR el cargo
  del operador (mismo momento donde hoy carga monto+moneda+concepto) — es un dato que el humano YA conoce
  (cuál factura corresponde a qué servicio/moneda) y que el sistema no puede inferir con seguridad fiscal.
  Sin elección expresa, el cargo NO se puede facturar automáticamente (ver fallback).
- **Facturas históricas / cargos sin `TargetInvoiceId`** (legacy, o el usuario no eligió): fallback =
  **revisión manual**, exactamente el mecanismo que ya usa `RouteDebitNoteToManualReviewAsync` (mismo patrón
  que el guard ARREGLO 2 que T3b reemplaza). No se inventa una segunda ruta de resolución: un cargo sin
  factura destino clara nunca dispara ND automática.

**M2 — invariante: todos los cargos `Kind != Withholding` de una MISMA línea comparten `TargetInvoiceId`**.
El campo vive a nivel cargo (no a nivel línea) por simplicidad de modelo, PERO con una regla dura validada en
el servicio al setear: todos los cargos trasladables al cliente de la misma `BookingCancellationLine` deben
apuntar a la misma factura destino (una línea = un servicio = una factura de venta que lo contiene; partir
los cargos de un mismo servicio entre dos facturas del cliente no tiene sentido de negocio y rompería la
trazabilidad servicio->renglón de ND). Los `Withholding` quedan exentos de esta regla porque nunca llegan al
cliente (no emiten renglón de ND — regla dura del contador, T2). La validación rechaza en el punto de escritura
un `TargetInvoiceId` distinto del que ya tienen los otros cargos trasladables de esa línea. Se prefirió esto a
mover el campo a la línea porque mantiene el dato junto al cargo que lo usa y evita un join extra al emitir; el
costo es una validación de servicio simple (más barata que reubicar la columna y su FK).

**Por qué no migrar/backfillear `TargetInvoiceId` para cargos históricos**: los cargos ya confirmados antes
de T3b no tienen forma de resolverse solos (la ambigüedad de origen es justamente lo que no existía
mecanizado). Quedan en `null` -> caen al fallback manual, comportamiento idéntico al guard ARREGLO 2 de hoy —
cero regresión, cero backfill bloqueante.

**Migración esperada**: `Adr044_M_T3b1_AddTargetInvoiceIdToOperatorCharge` — una columna nullable + FK a
`Invoices`, sin backfill de datos.

### Decisión 2 — TC de conversión del cargo: campo nuevo en el cargo, mismo patrón tipado que `Payment`

**Hueco confirmado**: no existe ningún campo hoy para "a qué TC se convierte un cargo del operador en USD
para entrar como renglón de una ND en pesos". Esto es DISTINTO de la regla ya firmada "el TC de la ND es
siempre el congelado de la factura original" (esa es sobre la moneda DEL COMPROBANTE; ésta es sobre la
conversión de un cargo EXTRANJERO embebido dentro de un comprobante en OTRA moneda) — no se reabre la regla
firmada, se cierra el hueco nuevo.

**Patrón ya existente a reusar**: `Payment.cs` (líneas 40-58) ya resuelve exactamente este problema para
pagos cruzados ARS<->USD: `ExchangeRate` (decimal 18,6, convención ARS por 1 USD) + `ExchangeRateSource`
(enum tipado, nullable, nunca `Unset` cuando hay conversión) + `ExchangeRateAt` (fecha del TC aplicado). Es
el mismo idioma que usa `Invoice.ExchangeRateSource`/`ExchangeRateFetchedAt`/`ExchangeRateJustification`. Se
replica idéntico, sin inventar un vocabulario nuevo.

**M1 — CUÁNDO se fija el TC definitivo (CONFIRMADO por Gastón 2026-07-10: lectura (i), TC del DÍA DEL CARGO del
operador)**. La regla de negocio del ADR dice "la diferencia de cambio la asume el cliente al TC del día del
cargo" (Decisión final #1). Había dos lecturas de "día del cargo": (i) el día en que el operador confirma/carga
su cargo, o (ii) el día en que ese cargo se le carga AL CLIENTE (= emisión de la ND). **Gastón confirmó la
lectura (i): el TC definitivo del renglón es el del DÍA EN QUE EL OPERADOR COBRÓ su cargo** — el TC cargado al
confirmar el cargo del operador, NO el del día de emisión de la ND. Consecuencia sobre el modelo:
- Los 4 campos de TC "ESTIMADO" en `OperatorCharge` capturan el **TC del día del cargo del operador** (cargado
  al confirmar/cargar el cargo).
- El **TC DEFINITIVO del renglón se promociona AL EMITIR** copiando el VALOR y la FECHA del estimado (día del
  cargo): NO se recotiza al día de emisión. Viaja al comprobante, a auditoría y al cálculo del delta de
  tesorería (Decisión 3, `RateAtChargeDay`).
- **Consecuencia: se ELIMINA el tope de antigüedad de 48h** (guard F2 del gate fiscal): bajo la lectura (i) el
  TC es legítimamente del día del cargo aunque hayan pasado semanas. La banda de sanidad (TC ≤ 0 o == 1 =
  "default peligroso") SÍ se mantiene (es otra cosa).
- El diseño fue reversible a propósito (los campos estimado/definitivo ya existían): el cambio de (ii)→(i) no
  requirió migración destructiva, solo cambiar qué fecha/valor se promociona.

**Campos (TC ESTIMADO al confirmar)** en `BookingCancellationLineOperatorCharge` (T2):
```
EstimatedExchangeRateToClientInvoiceCurrency  (decimal(18,6)?, misma convencion ARS-por-1-USD que Payment.ExchangeRate)
EstimatedExchangeRateSource                   (ExchangeRateSource?, reusa el enum ya existente, incl. BNA_VendedorDivisa)
EstimatedExchangeRateAt                       (DateTime?, cuando se tomo el TC estimado)
EstimatedExchangeRateJustification            (string? MaxLength 500, obligatoria cuando Source=Manual — mismo INV-120)
```
Todos `null` cuando `Charge.Currency == Invoice(TargetInvoiceId).MonId` (no hay conversión que hacer, caso
mono-moneda de T3a) — comportamiento idéntico a hoy para el caso simple.

**Hogar físico del TC DEFINITIVO** (corrección del re-review 2026-07-10 — la versión anterior decía "reusa los
campos de TC de la Invoice", y eso NO funciona: `InvoiceItem` no tiene campos de TC, y en el caso típico
(cargo USD → ND en ARS) la Invoice tiene `MonId=PES`/`MonCotiz=1` con sus campos de TC vacíos — describen la
valuación del COMPROBANTE, no la conversión de un renglón embebido). El definitivo vive en CAMPOS PROPIOS de
`BookingCancellationLineOperatorCharge`, al lado de los estimados, poblados AL EMITIR la ND:
```
DefinitiveExchangeRateAtNdEmission            (decimal(18,6)?, misma convencion; = TC del DÍA DEL CARGO, M1 (i))
DefinitiveExchangeRateSource                  (ExchangeRateSource?)
DefinitiveExchangeRateAt                      (DateTime?, FECHA DEL CARGO del operador, copiada del estimado — NO la de emision)
DefinitiveExchangeRateJustification           (string? MaxLength 500, obligatoria cuando Source=Manual)
```
El estimado queda intacto como rastro; el definitivo copia su valor y su fecha al emitir. La Decisión 3 (delta
de tesorería) lee el definitivo de acá (columna `RateAtChargeDay` en la fila de ajuste).

**Fórmula del renglón** (al emitir): `MontoRenglonND(moneda_factura) = Charge.Amount(Charge.Currency) x TC_del_día_del_cargo`.
NO se recotiza el TC DEL COMPROBANTE (ese sigue siendo el congelado de la factura original, regla firmada): lo
que se cotiza es la conversión de un cargo extranjero embebido, con el TC del DÍA DEL CARGO del operador
(lectura (i), CONFIRMADO por Gastón 2026-07-10 — no se recotiza al día de emisión).

**Quién lo carga (default vs manual)**: si existe una fuente automática confiable para la fecha de emisión
(norte BNA/ADR-011, hoy NO construido), el sistema propone ese TC como default editable. Mientras esa fuente
no exista (estado actual), el TC definitivo es MANUAL con `Source=Manual` + justificación obligatoria — mismo
criterio ya usado en toda factura extranjera del sistema (INV-120). No se bloquea T3b esperando el TC real: se
construye con el mismo patrón manual-con-justificación que ya rige facturas USD hoy.

**Auditoría**: el TC estimado usa el mismo patrón `ConfirmedByUserId`/`ConfirmedByUserName`/`ConfirmedAt` que ya
tiene el cargo (T2); el TC definitivo hereda la auditoría de emisión de la ND (`IssuedByUserId`/`IssuedByUserName`,
ya existente en `CreateInvoiceRequest.cs`) — "quién fijó el TC fiscal y cuándo" queda cubierto sin campos nuevos.

**Migración esperada**: `Adr044_M_T3b2_AddEstimatedExchangeRateToOperatorCharge` — 4 columnas nullables en
`BookingCancellationLineOperatorCharge`, sin backfill (cargos históricos sin conversión quedan en null,
comportamiento T3a intacto).

**B2 — guard de re-validación de la factura destino AL EMITIR (fiscal)**. `TargetInvoiceId` se fija al confirmar
el cargo, pero la ND sale días después (reintentos, cola async). En el medio la factura destino pudo anularse
(su NC ya emitida), perder vigencia o dejar de ser miembro del `activeInvoices` de la BC. **Antes de emitir la
ND**, el motor re-valida que la factura destino: (a) siga viva con CAE (`AnnulmentStatus != Succeeded &&
!string.IsNullOrEmpty(CAE)`), (b) no sea NC/ND, y (c) siga siendo miembro del `activeInvoices` de la reserva
(mismo conjunto que resuelven `DraftAsync`/`ResolveAndPreflightInvoicesToAnnulAsync`). Si cualquiera falla ->
`RouteDebitNoteToManualReviewAsync` con motivo claro — exactamente el mismo idioma defensivo que ya usa
`EvaluateDebitNoteGating` en todo `TryEmitCancellationDebitNoteAsync` ("ante la duda, NO emitir -> manual"). Sin
este guard, una ND podría salir contra una factura ya anulada = incidente fiscal.

### Decisión 3 — Ajuste de diferencia de cambio de tesorería: registro propio DENTRO del aggregate de cancelación, fuera del calculador canónico y fuera del Libro de Caja

**Cuándo ocurre realmente el delta (verificado con el código, no la intuición)**: la ND traslada el cargo con
el TC definitivo del DÍA DEL CARGO del operador (Decisión 2 / M1 (i)) — ese es el valor en pesos que se le
cobró al cliente por el cargo del operador. El delta de tesorería aparece DESPUÉS, cuando el cargo del operador
se LIQUIDA REALMENTE, y **el momento de liquidación depende de la forma de cobro del cargo**
(`PenaltyCollectionMode`, T2):

- **`Retenida` (default)**: se liquida cuando el operador devuelve el neto — momento
  `OperatorRefundReceived`/su `OperatorRefundAllocation` contra el `BookingCancellation`.
  `OperatorRefundReceived.ExchangeRateAtReceipt` (campo YA EXISTENTE, `OperatorRefundReceived.cs:64`, "TC del
  día en que se recibe") puede diferir del TC definitivo con que salió la ND. Ese es el delta.
- **`FacturadaAparte`**: el operador devuelve el bruto y su cargo entra como DEUDA NUESTRA hacia el operador
  (cuenta a pagar, ADR-041). NUNCA genera un `OperatorRefundAllocation` — lo dice el propio enum de T2
  (`PenaltyCollectionMode.FacturadaAparte`): se paga por el circuito de pagos a proveedor
  (`SupplierPayment`/`SupplierService`). Por eso su delta se registra **al registrar el pago al proveedor del
  documento del cargo** (`SupplierPayment`), usando el TC de ESE pago vs. el TC definitivo de la ND.

Ese es el delta de tesorería: **cuánto valía en pesos el cargo del operador SEGÚN LA ND** vs **cuánto vale
realmente cuando la plata se liquida** (por retención recibida o por pago al proveedor). NO se registra al
emitir la ND (en ese momento el número todavía no existe: no hay TC real de liquidación con el que comparar).

**B1 — el trigger DEBE cubrir ambas formas de cobro (hueco silencioso corregido)**. La versión inicial de este
addendum ligaba el ajuste FX SOLO a `OperatorRefundAllocationId` (FK no-nullable): eso jamás cubría
`FacturadaAparte`, que se liquida por `SupplierPayment` y nunca produce una allocation — el delta de esos cargos
habría quedado sin registrar EN SILENCIO. Fix: la tabla de ajuste admite **dos orígenes alternativos, con la
invariante "exactamente uno no-null"** (mismo patrón `CashLedgerEntry` con sus 5 FKs tipadas):
`OperatorRefundAllocationId` (nullable, origen de los `Retenida`) o `SupplierPaymentId` (nullable, origen de los
`FacturadaAparte`).

**Por qué NO usar `CashLedgerEntry` (ADR-022)**: ese libro modela EXCLUSIVAMENTE hechos económicos que
mueven caja real, con un CHECK "exactamente un origen tipado no-null" entre 5 FKs cerradas (`Payment`,
`SupplierPayment`, `OperatorRefundReceived`, `ClientCreditWithdrawal`, `ManualCashMovement`). El ajuste de
diferencia de cambio de tesorería NO es un movimiento de caja nuevo: no entra ni sale plata adicional por
este hecho, es una RECONCILIACIÓN DE VALUACIÓN entre dos números que ya existen (TC del cargo vs TC de
`OperatorRefundReceived`). Forzarlo en `CashLedgerEntry` exigiría inventar un 6to origen tipado para algo que
no es un ingreso/egreso de caja — y la propia spec del contador dice explícitamente "sin asiento de mayor
formal todavía" (gap de contabilización formal pendiente de firma, fuera de esta tanda).

**Por qué NO usar `Payment` con un `EntryType` nuevo**: `Payment` alimenta directo a
`ReservaMoneyCalculator.AccumulatePayments` (`ReservaMoneyCalculator.cs:148-166`), que suma CUALQUIER pago
vivo (`Status != "Cancelled" && !IsDeleted`) a `TotalPaid` de la moneda imputada, SIN filtrar por
`EntryType`/naturaleza — es el calculador CANÓNICO de "cuánto pagó el cliente", y ya tiene dos usos
distintos conviviendo (`PaymentEntryTypes.Payment` / `CreditNoteReversal`) sin discriminar entre ellos en el
cálculo. Meter un tercer `EntryType` ahí para el ajuste de FX HARÍA que ese número interno de reconciliación
se sume o reste del saldo del cliente de forma automática e implícita, sin que el equipo de contabilidad haya
firmado que eso es correcto — exactamente el riesgo que la spec de T3 marca como "gap más grande, pendiente
de firma". Tocar el calculador canónico para un dato sin contabilización formal aprobada es el camino
inseguro.

**Decisión (registro mínimo, auditable, sin tocar comprobantes ni el calculador canónico)**: tabla hija
nueva `BookingCancellationLineTreasuryFxAdjustment`, 1 fila por `BookingCancellationLineOperatorCharge`
liquidado con conversión (0 o 1 fila; no hay razón de negocio para más de un ajuste por cargo).

```
BookingCancellationLineTreasuryFxAdjustment
  Id (int, PK)
  PublicId (Guid)
  OperatorChargeId (FK -> BookingCancellationLineOperatorCharge, cascade delete)
  -- B1: DOS origenes alternativos, invariante "exactamente uno no-null" (CHECK SQL, patron CashLedgerEntry):
  OperatorRefundAllocationId (int?, FK -> OperatorRefundAllocation)  -- origen de los cargos Retenida
  SupplierPaymentId          (int?, FK -> SupplierPayment)           -- origen de los cargos FacturadaAparte
  RateAtNdEmission (decimal 18,6)           -- TC DEFINITIVO con que salio la ND (Decision 2 / M1)
  RateAtSettlement (decimal 18,6)           -- TC real de la liquidacion:
                                            --   Retenida -> OperatorRefundReceived.ExchangeRateAtReceipt
                                            --   FacturadaAparte -> TC del SupplierPayment
  ChargeAmount (decimal 18,2)               -- copia de Charge.Amount al momento del calculo (foto, no vivo)
  ChargeCurrency (string, MaxLength 3)      -- copia de Charge.Currency
  DeltaAmount (decimal 18,2)                -- (RateAtSettlement - RateAtNdEmission) x ChargeAmount,
                                                positivo = a favor de la agencia, negativo = en contra
  SettlementCurrency (string, MaxLength 3)  -- moneda de la ND/factura del cliente (misma que Decision 2)
  AssumedBy (TreasuryFxAssumedBy: Client=0 default | Agency=1)  -- config al momento del calculo, snapshot
  -- M4: void/reemplazo de la allocation de origen (soft-void ADR-002):
  IsSuperseded (bool, default false)        -- true = su allocation/pago origen fue voideado; esta fila NO cuenta
  SupersededByAdjustmentId (int?, FK self)  -- FK a la fila recalculada con la allocation/pago de reemplazo
  CreatedAt (DateTime)
  Notes (string?, MaxLength 1000)

  CHECK chk_..._exactly_one_origin:
     (OperatorRefundAllocationId IS NOT NULL AND SupplierPaymentId IS NULL)
     OR (OperatorRefundAllocationId IS NULL AND SupplierPaymentId IS NOT NULL)
  UNIQUE INDEX filtrado a IsSuperseded = false por (OperatorChargeId):
     a lo sumo UNA fila VIGENTE por cargo (las superseded no cuentan; historia intacta).
```

**`AssumedBy` es snapshot, no lectura en vivo de la config**: si la agencia cambia la config después, los
ajustes históricos no deben reinterpretarse — mismo criterio de "snapshot al momento del evento" que ya rige
todo el módulo (`FiscalSnapshot`, T2 `SupplierInvoicingModeAtEvent`).

**M4 — void/reemplazo de la allocation de origen (soft-void ADR-002)**. `OperatorRefundAllocation` NO se
borra: se marca `IsVoided` (verificado, `BookingCancellationService.cs:7261` ya filtra `!a.IsVoided`) y
puede reemplazarse por otra allocation (ej. se corrige a qué reserva se imputó un reembolso). Como la fila de
ajuste FX guarda el TC de liquidación de una allocation concreta, si esa allocation se voidea el ajuste queda
sobre un dato muerto. Mecanismo: al voidear la allocation de origen (o el `SupplierPayment`), su fila de
ajuste se marca `IsSuperseded=true` (sale del índice de vigentes, NO se borra — historia intacta, mismo
espíritu que la reversa de `CashLedgerEntry`); cuando llega la allocation/pago de REEMPLAZO, se CALCULA una
fila nueva de ajuste con el nuevo `RateAtSettlement` y se enlaza vía `SupersededByAdjustmentId` a la anterior.
Si no hay reemplazo (la liquidación se anula sin sustituto), la fila queda superseded sin sucesora y el cargo
vuelve a "sin ajuste vigente" (correcto: no hubo liquidación real que comparar). El índice único filtrado
garantiza a lo sumo un ajuste vigente por cargo en todo momento.

**Qué NO hace esta tabla (alcance deliberadamente acotado)**: no genera una ND/NC de corrección
automática, no toca `Invoice`/comprobantes, no participa de `ReservaMoneyCalculator`, no escribe en
`CashLedgerEntry`. Es un registro de auditoría/reconciliación de gestión interna, visible en el extracto del
operador y en la ficha de la cancelación, EXACTAMENTE como la propia spec de T3 lo pide ("dato auditable...
sin asiento de mayor formal todavía").

**M3 — el registro NO se da por terminado sin lector (wiring explícito, paso 6 del orden de obra)**. Una tabla
de ajustes que nadie muestra es plata invisible: el mismo antipatrón que P2 vino a cerrar (la multa que
neteaba el `RefundCap` pero no aparecía en el extracto del operador). El ajuste FX se refleja como **línea
tipificada "Diferencia de cambio"** en el extracto/cuenta del operador (mismo builder que ya pinta la multa
retenida y las líneas de deuda AP — `SupplierCancellationCircuitReader`/`SupplierAccountStatementBuilder`,
familia T2), sumando `DeltaAmount` con su signo (a favor / en contra), y visible también en la ficha de la
cancelación junto al cargo que lo originó. Solo se leen filas VIGENTES (`IsSuperseded = false`). Sin este
lector cableado, T3b no se considera completa.

**Qué pasa si `AssumedBy = Client` (default) y el número es significativo**: por ahora, se REGISTRA
(visible, trazable) pero NO se cobra automáticamente. Trasladarlo al cliente como cargo real exigiría el
mecanismo YA FIRMADO de ND/NC complementaria (R5, 2026-06-01) que la propia spec de T3 referencia para
correcciones post-emisión — es la misma familia de problema (ajustar algo después de que el comprobante
original ya salió) y no amerita un mecanismo nuevo. Automatizar ESE disparo (crear la ND complementaria sola,
sin intervención humana) queda fuera de T3b: requiere criterio de materialidad (¿a partir de qué monto vale
la pena una ND complementaria vs. absorberlo en silencio?) que es una decisión de negocio/contable, no de
arquitectura. Recomendación: en T3b el ajuste queda como dato visible + acción manual opcional ("emitir ND
complementaria por esta diferencia", reusando R5); automatizar el disparo es un refinamiento de T4 o
posterior, con sign-off de Gastón sobre el umbral de materialidad.

**Migración esperada**: `Adr044_M_T3b3_AddBookingCancellationLineTreasuryFxAdjustment` — tabla nueva + 3 FKs
(cargo + los 2 orígenes alternativos + la self-FK `SupersededByAdjustmentId`) + CHECK "exactamente un
origen" + índice único filtrado a `IsSuperseded=false` por `OperatorChargeId` + enum `TreasuryFxAssumedBy`
nuevo. Aditiva, sin migrar datos existentes (no hay liquidaciones previas para retrocalcular: los campos de
TC estimado de Decisión 2 no existían antes de T3b).

### Orden de construcción sugerido dentro de T3b

1. Decisión 2 primero (campos de TC estimado en el cargo + fijación del TC definitivo al emitir, M1) — es
   prerrequisito de Decisión 3 (no hay delta que calcular sin el TC definitivo de la ND) y de la fórmula de
   ND multimoneda que T3b debe automatizar. Incluye el guard B2 de re-validación al emitir.
2. Decisión 1 (resolución de factura destino + invariante M2) en paralelo o inmediatamente después — es
   independiente de Decisión 2, pero ambas son prerrequisito de "emitir la ND real con 2+ facturas activas".
3. Decisión 3 al final — depende de que Decisión 2 ya exista (necesita el TC definitivo de la ND) y de que
   haya al menos una liquidación real (`OperatorRefundReceived` para `Retenida`, o `SupplierPayment` para
   `FacturadaAparte`) para poblarse; es la pieza que menos bloquea el caso simple (una cancelación sin
   liquidación real nunca genera esta fila).
4. Reemplazo del guard ARREGLO 2 para el caso 2+ facturas: solo después de 1-3.
5. Lógica de supersede/recálculo M4 (al voidear/reemplazar la allocation o el `SupplierPayment` de origen).
6. **Wiring del lector (M3)**: línea "Diferencia de cambio" en el extracto del operador + visibilidad en la
   ficha. Sin esto, T3b NO se considera terminada (plata invisible = antipatrón que P2 vino a cerrar).
7. Gate de siempre antes de producción: `arca-tax-expert-argentina`/`travel-agency-accountant-argentina`
   deben revisar la fórmula de Decisión 2, la confirmación M1 y el registro de Decisión 3 antes del primer
   caso real; y el gate humano de firma contable (alícuota IVA pass-through RI, ajeno a T3b) sigue bloqueando
   SOLO ese caso específico, no el resto de T3b.

### Tests obligatorios antes de mergear T3b (mismo estándar que el Addendum T2)

1. **Regresión mono-moneda**: 1 factura activa, cargo en la misma moneda de la factura (sin conversión) — se
   comporta byte-idéntico a T3a; los campos de TC estimado quedan en `null`, la ND sale como hoy.
2. **2 facturas + selección correcta**: reserva con factura ARS + factura USD; el cargo con `TargetInvoiceId`
   explícito emite su renglón en la ND de ESA factura, con su moneda/TC.
3. **2 facturas sin elección**: `TargetInvoiceId` null (histórico o no elegido) -> `RouteDebitNoteToManualReviewAsync`,
   NO emite ND automática.
4. **B2 — factura destino anulada al emitir**: `TargetInvoiceId` apunta a una factura que se anuló entre el
   confirmar y el emitir -> el guard la detecta y rutea a revisión manual (nunca ND contra factura muerta).
5. **M2 — invariante de `TargetInvoiceId` compartido**: rechazar setear en un cargo trasladable
   (`Kind != Withholding`) un `TargetInvoiceId` distinto del que ya tienen los otros cargos trasladables de su
   línea; un `Withholding` con otro (o sin) `TargetInvoiceId` no dispara el rechazo.
6. **Delta FX con signo correcto en ambas direcciones** (`Retenida`): `RateAtSettlement > RateAtNdEmission` ->
   `DeltaAmount` positivo (a favor de la agencia); `RateAtSettlement < RateAtNdEmission` -> negativo; el ajuste
   se liga a la `OperatorRefundAllocation` correcta, `SupplierPaymentId` null.
7. **`FacturadaAparte` cross-currency**: un cargo `FacturadaAparte` en USD sobre línea USD, factura cliente
   ARS -> su ajuste FX se dispara al registrar el `SupplierPayment` del documento del cargo, con
   `SupplierPaymentId` no-null y `OperatorRefundAllocationId` null; el CHECK "exactamente un origen" se cumple.
8. **M4 — supersede/recálculo**: voidear la `OperatorRefundAllocation` (o `SupplierPayment`) de origen marca
   su ajuste `IsSuperseded=true`; la allocation/pago de reemplazo genera una fila nueva enlazada por
   `SupersededByAdjustmentId`; el índice único filtrado sigue permitiendo solo 1 ajuste vigente por cargo.
9. **M3 — lectura**: el ajuste vigente aparece como línea "Diferencia de cambio" en el extracto del operador
   con su signo; las filas superseded NO aparecen.

**Rollback**: las 3 migraciones son aditivas (columnas nullables + tabla nueva), sin tocar filas existentes
— `Down()` estándar. Ninguna decisión de este addendum modifica el cálculo de saldo del cliente
(`ReservaMoneyCalculator`) ni el libro de caja (`CashLedgerEntry`): el rollback de cualquiera de las 3 no
afecta esos números ya en producción.

**Menor — gate humano compartido**: la contabilización formal del ajuste de tesorería (qué cuenta, cuándo se
devenga) entra al MISMO gate humano del contador matriculado de Gastón que ya tiene pendiente la alícuota IVA
de pass-through para RI. (M1 YA quedó CONFIRMADO por Gastón 2026-07-10: lectura (i), TC del día del cargo.)

**Confirmaciones de Gastón (2026-07-10) que cerraron la ronda T4 de T3b**:
- **M1: lectura (i)** — el TC definitivo es el del DÍA DEL CARGO del operador (no el de emisión de la ND). Se
  eliminó el tope de antigüedad de 48h; la banda de sanidad TC ≤ 0 o == 1 se mantiene.
- **Comprobante del pasajero SÍ nombra al mayorista** en el renglón pass-through ("Penalidad de {Operador} por
  cancelación s/Fc ...").
- **Extracto del operador**: la línea "Diferencia de cambio" vive en el bloque de la moneda de la línea (junto
  a la multa que la originó), con el delta convertido a esa moneda — CONFIRMADO como default definitivo.

**Lo que queda deliberadamente sin resolver (anotado, no ignorado)**:
- Contabilización formal del ajuste de diferencia de cambio (qué cuenta contable, cuándo se devenga): firma
  contable pendiente, mismo gap que ya señalaba la spec de T3 (gate humano compartido, arriba).
- Automatizar el disparo de la ND complementaria por diferencia de cambio significativa: requiere criterio
  de materialidad de Gastón, no es arquitectura.
- Alícuota IVA de pass-through para RI: gate humano, firma del contador matriculado, ajeno a esta obra.
- TC real por fecha (BNA/ADR-011): mientras no exista, Decisión 2 opera 100% manual con justificación —
  correcto y suficiente para no bloquear T3b, pero es deuda técnica ya conocida del norte multimoneda.

## Addendum T5 (2026-07-11) — las 3 condiciones de arquitectura que la spec fiscal dejó bloqueadas

Contexto: la especificación fiscal de T5
(`.claude/agent-memory/travel-agency-accountant-argentina/adr044-t5-anulacion-parcial-spec.md`) dejó T5
"apto CON CONDICIONES": 3 bloqueantes de arquitectura verificados en código. Este addendum los cierra.
Código real inspeccionado para decidir: `BookingCancellationService.cs` (`CancelServiceAsync` líneas
380-502, `ResolveServiceCancellationBlockReasonAsync` líneas 756-785, `RecordPartialCancellationLineAsync`
líneas 957-1033, `DraftAsync` líneas 135-354, `TryResolveExistingBcAsync` líneas 1108-1221),
`MutationGuards.cs` (íntegro), `BookingCancellation.cs`, `BookingCancellationLine.cs`,
`BookingCancellationLineScope.cs`, `BookingCancellationCreditNote.cs`, `Voucher.cs`, y los 2 índices únicos
filtrados de `AppDbContext.cs` (líneas 1729 y 1744).

### Hallazgo adicional que cambia la Decisión C (no estaba en los 3 puntos de la spec)

La spec fiscal ya había verificado que `RecordPartialCancellationLineAsync` (el camino de cancelar UN
servicio) se reengancha mal a un BC `Closed` (bug real, punto 3 de la spec). Verificando el camino GEMELO
— `DraftAsync`/`TryResolveExistingBcAsync` (el camino de anular TODA la reserva) — encontré que ese camino
tiene el **bug inverso**: `TryResolveExistingBcAsync` (líneas 1205-1220) mete `Closed` en su "caso (d):
cualquier otro estado → rechazo `INV-081`" junto con estados genuinamente activos
(`AwaitingFiscalConfirmation`, `ManualReviewPending`, etc.). Consecuencia verificada: si una reserva tuvo
**una** cancelación parcial de un solo servicio que ya llegó a `Closed` (reembolso consumido, caso
resuelto), intentar después "Anular" el resto de la reserva **rechaza para siempre** con `INV-081`,
tratando un evento fiscal ya terminado como si todavía estuviera en curso. Sin arreglar esto, T5 destraba
"cancelar 1 servicio" pero **traba permanentemente** cualquier anulación total o parcial posterior de esa
misma reserva — el mismo tipo de agujero que el punto 3 de la spec, en el sentido opuesto. Se corrige junto
con la Decisión C (mismo cambio de regla: `Closed` se trata como `Aborted` a los efectos de "¿puedo abrir
un evento de cancelación NUEVO sobre esta reserva/factura?", nunca reutilizable, nunca bloqueante eterno).

### Decisión A — SEC-B1 se reemplaza por una compuerta que resuelve, no que bloquea

**Diagnóstico verificado del guard actual**: `ResolveServiceCancellationBlockReasonAsync` reusa
`MutationGuards.GetBookingMutationBlockReasonAsync`/`GetServiceMutationBlockReasonAsync` — las MISMAS
funciones que `BookingService.cs` usa para bloquear la EDICIÓN pura de un hotel/vuelo/paquete/traslado ya
facturado o voucherizado (CODE-04/05, origen: auditoría 2026-05-09). Es decir, hoy "cancelar un servicio
con factura viva" y "editar el precio de un hotel ya facturado" comparten el mismo candado. **Esto importa
para el diseño**: no se puede tocar `GetBookingMutationBlockReasonAsync`/`GetServiceMutationBlockReasonAsync`
en sí mismas — seguirán bloqueando la edición pura (ese es un problema de integridad de datos ya
facturados/voucherizados, no relacionado con emitir una NC). Hay que sacar a la cancelación de ese guard
compartido y darle uno propio.

**Por qué existía el guard (verificado, no soy yo interpretando)**: el comentario propio de
`ResolveServiceCancellationBlockReasonAsync` dice literalmente que sin el bloqueo, cancelar bajaría "la
venta confirmada por debajo del `ImporteTotal` de la factura SIN emitir NC → divergencia fiscal
irreversible". El riesgo real que hay que seguir cerrando es exactamente ese: **un servicio que desaparece
del sistema mientras la factura al cliente sigue mostrando el monto viejo, sin ningún NC en camino, ni
siquiera uno pendiente en una cola de revisión.** Hoy el guard cierra ese riesgo con un bloqueo binario;
pero el precio es que **NINGÚN** servicio de una reserva ya facturada puede cancelarse jamás por esta vía
(el caso normal, según la propia spec: "la mayoría de las reservas ya están facturadas"), y el mecanismo
`RecordPartialCancellationLineAsync` que sí corre después es dead-code en ese caso (nunca se alcanza).

**Voucher: SIN CAMBIOS.** Verificado en `Voucher.cs`: el `Scope` es `ReservaCompleta` / `TodosLosPasajeros`
/ `PasajerosSeleccionados` — no existe ninguna asociación voucher↔servicio puntual (solo
voucher↔pasajero/reserva). No hay forma mecánica de acotar el chequeo de voucher a "solo este servicio" sin
inventar un modelo de datos nuevo, que está fuera de alcance explícito de T5 (anulación a nivel pasajero
excluida, y un voucher agrupa por pasajero, no por servicio). **Recomendación: el bloqueo por voucher
`Issued` de la reserva se mantiene EXACTAMENTE como hoy** (reserva-level, vía
`MutationGuards.HasIssuedVoucherForReservaAsync` — hoy privado, se expone como método público nuevo
`GetReservaVoucherOnlyBlockReasonAsync` que reusa el mismo helper, sin duplicar la regla). No reabre el
agujero: un voucher entregado al cliente sigue sin poder mostrarse mentiroso.

**"Servicio ya viajado": NO se agrega un gate nuevo (verificado, no inventado).** Grep sobre
`BookingCancellationService.cs` no encontró ningún chequeo de fecha por-servicio (`EndDate`,
`CheckOutDate`, etc.) para bloquear cancelar un servicio ya consumido. Lo que SÍ existe (verificado,
líneas 393-404) es un gate a nivel RESERVA: `EstadoReserva.IsCollectableStatus` excluye `Traveling` — si
la reserva completa está en viaje, no se cancela NINGÚN servicio por esta vía. El caso residual (una
reserva con rango de fechas amplio, todavía "Confirmada", donde UN servicio puntual ya ocurrió mientras
otros no) no está cubierto hoy y **no lo cubre este addendum** — es una regla de negocio nueva (¿qué campo
por tipo de servicio cuenta como "consumido"?), no una decisión de arquitectura, y no es uno de los 3
bloqueantes que la spec fiscal marcó. Lo dejo anotado como brecha conocida para una tanda posterior, no
lo escondo.

**El reemplazo del candado fiscal (factura viva)**: deja de ser un bloqueo binario y pasa a ser una
**compuerta que SIEMPRE deja un rastro accionable**, nunca un servicio cancelado con la factura intacta y
nada más. Se implementa DENTRO de la misma transacción de `CancelServiceAsync` (no como pre-check
separado), usando el mismo build de línea que hoy existe (`BuildCancellationLinesAsync` +
`RecordPartialCancellationLineAsync`, ver Decisión B para el detalle de cómo se resuelve el monto/factura):

1. **Si no hay factura viva** → sin cambios (comportamiento de hoy).
2. **Si hay factura viva y el monto+factura destino se resuelven sin ambigüedad** (Decisión B: única
   factura activa, o `TargetInvoiceId` ya elegido y dentro del remanente) → la línea queda lista para
   emitir su NC (total-de-esa-factura o parcial, según el monto vs. el remanente) **en la MISMA operación**
   — se reemplaza el "queda como borrador para revisión manual" de hoy (decisión #3 vieja) por la emisión
   real encolada (mismo patrón asincrónico que ya usa el resto del módulo con AFIP).
3. **Si hay factura viva pero es ambigua** (2+ facturas activas sin elección, o factura que no discrimina
   por servicio, Casos c/d de la spec fiscal) → la línea se crea en un BC propio (Decisión C) que pasa a
   `ManualReviewPending` (reusa el estado y el `ApprovalRequest` tipo `PartialCreditNoteApproval` que ya
   existen desde FC1.3/ADR-009, aplicados aquí a un BC de una sola línea en vez del histórico BC total) —
   el servicio SÍ se cancela, pero queda un ítem visible y accionable en la bandeja pasiva (P5), NUNCA
   silencioso.
4. **Bloqueo duro (409) SOLO si ni 2 ni 3 son alcanzables** — hoy el único caso verificado es
   `reserva.PayerId is null` (línea 982, hoy se salteaba en silencio con `return`; pasa a ser un 409
   explícito: sin pagador no hay a quién facturarle la NC, cancelar el servicio dejando ese hueco sin
   rastro es peor que bloquear).

**Por qué esto no reabre el agujero**: el agujero original era "servicio desaparece, cero rastro fiscal".
Con este reemplazo, cancelar SIEMPRE termina en una de tres fotos verificables: NC emitida, NC en revisión
manual con motivo claro, o bloqueo explícito — nunca "se cancela y ya está". **Dependencia dura de orden de
construcción**: la Decisión A (aflojar el guard) y la Decisión B (armado de línea + emisión real, ya no
"borrador muerto") deben desplegarse ATÓMICAMENTE en la misma tanda. Aflojar el guard sin que la emisión
real ya esté cableada reabre exactamente el agujero que SEC-B1 vino a cerrar.

### Decisión B — Campo manual + cap acumulativo, en la LÍNEA (no en un nuevo objeto)

**Campo nuevo, mismo patrón que `TargetInvoiceId` de T3b (T3b lo aplicó al cargo del operador/ND; acá se
aplica al lado NC/crédito de la MISMA línea)**:

```
BookingCancellationLine.TargetInvoiceId          (int?, FK -> Invoice, nullable)
BookingCancellationLine.ConfirmedGrossCreditAmount (decimal(18,2)?, nullable)
BookingCancellationLine.CreditAmountConfirmedByUserId   (string?, MaxLength 450)
BookingCancellationLine.CreditAmountConfirmedByUserName (string?, MaxLength 200)
BookingCancellationLine.CreditAmountConfirmedAt          (DateTime?)
```

**Resolución de `TargetInvoiceId`** (idéntica al patrón ya aprobado en T3b Decisión 1, aplicado al mismo
query `activeInvoices` que `DraftAsync` ya usa):
- **1 sola factura activa** (el caso dominante hoy, y el ÚNICO caso de la "reserva de 1 servicio" que pide
  el enunciado — para esa reserva, cancelar su único servicio sigue yendo por "Anular" total,
  `DraftAsync`/`ConfirmAsync`, sin tocar nada de este addendum): se autocompleta, invisible.
- **2+ facturas activas**: el vendedor elige al confirmar el cargo — mismo momento/UI que T3b, mismo
  fallback a revisión manual si no elige.

**`ConfirmedGrossCreditAmount` — de dónde sale el número (verificado, no soy yo inventando el bruto)**:
`BookingCancellationLine.LineSaleAmount` (campo YA EXISTENTE, línea 68 de `BookingCancellationLine.cs`,
"SalePrice del servicio cancelado, congelado al armar la línea") es el DEFAULT propuesto — no la fuente
de verdad fiscal, porque (hecho verificado por la spec, T3b Decisión 1) no existe ninguna tabla
servicio→renglón de factura: `LineSaleAmount` es lo que el SERVICIO vale en el sistema, no necesariamente
lo que ESA factura (armada 100% manual) le asignó a ese servicio si comparte comprobante con otros. Por
eso el monto se PROPONE con `LineSaleAmount` (cero fricción visual) pero se CONFIRMA por el vendedor —
salvo en el caso trivial de abajo, donde ni se muestra el campo.

**Cómo se deriva el caso (a) "100% de su factura" vs (b) "comparte factura" — SIN pedir un segundo dato**:
no hace falta preguntarle al vendedor "¿esto es el total de la factura o una parte?" (eso sería el
cuestionario que el dueño pidió evitar). Se DERIVA comparando el monto contra el remanente calculado (ver
cap abajo):
- `ConfirmedGrossCreditAmount == remanente_de_TargetInvoiceId` → se trata IGUAL que una NC total de hoy,
  pero **acotada a esa factura** (no a toda la reserva) — cero desarrollo nuevo, reusa T0-T4 tal cual sobre
  1 factura, exactamente como pide el punto 1(a) de la spec fiscal.
- `ConfirmedGrossCreditAmount < remanente_de_TargetInvoiceId` → NC parcial (`CreditNoteKind.PartialOnOriginal`,
  ya existe desde ADR-009/FC1.3), por el monto bruto SIN netear la multa (criterio matriculado, sin
  reabrir).
- `ConfirmedGrossCreditAmount > remanente_de_TargetInvoiceId` → rechazado, no se guarda (ver cap).

**Cap acumulativo (verificado el mecanismo existente que se reusa, punto 5 de la spec)**: el remanente de
una factura se calcula, **NO solo dentro del BC actual** (Decisión C hace que cada evento sea su propio
BC), sino contra **TODAS** las `BookingCancellationCreditNote` de esa factura, sin importar de qué BC
vinieron:

```
remanente(invoiceId) = Invoice.ImporteTotal
                       - SUM(CreditNoteInvoice.ImporteTotal
                             WHERE BookingCancellationCreditNote.OriginatingInvoiceId = invoiceId
                               AND Status IN (Succeeded, Pending))
```

`Pending` cuenta (no solo `Succeeded`): si no se reserva el monto apenas se encola la NC, dos cancelaciones
casi simultáneas sobre la misma factura podrían ver el mismo remanente "libre" y ambas aprobarse — el mismo
riesgo que el propio ADR ya anotó como pendiente ("Seguimientos anotados por los reviewers de T0... T5
multiplica las NCs por reserva → cerrar ANTES de T5"). Este cálculo es la generalización directa del
patrón ya usado en `TryEmitCancellationDebitNoteAsync` (línea 8196: `if (total > resolvedInvoice.ImporteTotal)`
→ `Manual()`), aplicado al lado NC en vez del lado ND, y sumando el histórico en vez de un total fijo.

**Prerrequisito DURO, no opcional**: el candado de concurrencia por factura (`pg_advisory_xact_lock` o
transacción serializable sobre `TargetInvoiceId`, mismo patrón que `Adr042MultiInvoiceConcurrency`) tiene
que envolver el cálculo-y-escritura del remanente ANTES de que T5 emita su primera NC parcial real — sin
esto, dos NCs parciales aprobadas casi a la vez sobre la misma factura pueden sumar más de lo que la
factura vale (exactamente el riesgo que la propia ADR ya había marcado como "cerrar antes de T5").

**Caso trivial, cero fricción, sin campo visible**: reserva con 1 sola factura activa Y el servicio
cancelado es el primero que se toca de esa factura (remanente == `Invoice.ImporteTotal` todavía) → el
sistema autocompleta `TargetInvoiceId` y `ConfirmedGrossCreditAmount = LineSaleAmount` sin mostrar ningún
desplegable ni pedir confirmación extra — coincide con "el servicio es el 100% de su factura" y dispara NC
total de esa factura, cero desarrollo nuevo. El campo manual solo se muestra cuando hay ambigüedad real
(2+ facturas, o remanente ya reducido por una NC previa).

**Migración esperada**: `Adr044_M_T5a_AddTargetInvoiceIdAndConfirmedGrossCreditAmountToLine` — 2 columnas
nullables + 3 de auditoría en `BookingCancellationLine`, FK a `Invoices`, sin backfill (líneas legacy
quedan en null, no participan de emisión automática — caen al mismo fallback de revisión manual).

### Decisión C — cada evento de cancelación (total o parcial) abre SU PROPIO `BookingCancellation`; `Closed` se excluye del único, igual que `Aborted`

**Recomendación única**: excluir también `Status = 4 (Closed)` de los 2 índices únicos filtrados de
`AppDbContext.cs` (líneas 1729 y 1744, hoy `HasFilter("\"Status\" <> 6")` — solo excluye `Aborted`). Nuevo
filtro: `"\"Status\" NOT IN (4, 6)"` en ambos (`ReservaId` y `OriginatingInvoiceId`).

**Por qué esta y no reabrir/reusar el BC `Closed`**: `Closed` (verificado, `BookingCancellation.cs:78`) es
"cierre administrativo... reembolso consumido" — un hecho fiscal TERMINADO (NC ya con CAE, reembolso ya
imputado). Reabrirlo para colgarle una línea nueva reinterpretaría en silencio un evento que ya se cerró y
auditó como completo (mismo argumento que ya usa el propio ADR en T2 Decisión A para no reinterpretar
snapshots históricos). Cada evento de cancelación (una NC, un ciclo de reembolso del operador, un cierre)
es su propia foto — igual que un `BookingCancellation` ya representa hoy "una cancelación", no "todas las
cancelaciones de la reserva".

**Por qué NO rompe lo ya construido (verificado, no supuesto)**: los lectores que muestran "la cancelación
vigente" de una reserva **ya están escritos para elegir la MÁS RECIENTE entre varias filas no-abortadas**
(`OrderByDescending(b => b.Id)` o `.DraftedAt`, verificado en 8+ sitios: líneas 988, 1117, 1271, 3456, 4776,
4805, 4848, 5010 de `BookingCancellationService.cs`). Esto significa que el patrón "puede haber más de una
fila por reserva a lo largo del tiempo, mostrame la última" **ya es el idioma del código**, no algo que hay
que inventar. Con `Closed` excluido del único, una NC parcial nueva simplemente crea la fila siguiente, que
pasa a ser "la más reciente" y la que estos lectores ya eligen — **cero cambios en el código de lectura**.
El extracto del operador, el circuit reader y los read-models no necesitan tocarse: siguen leyendo por
`BookingCancellationId`/línea igual que hoy (`OperatorRefundAllocation.BookingCancellationId`,
`ClientCreditEntry.BookingCancellationId` — verificado, ambos FKs directos al BC, no hay razón para que
una NUEVA fila BC cause fugas entre eventos).

**Los 2 arreglos de código que la migración de índice exige (ambos, no uno solo — es el hallazgo nuevo de
arriba)**:
1. `RecordPartialCancellationLineAsync` (línea 988): el `Where(b => b.ReservaId == reserva.Id && b.Status
   != BookingCancellationStatus.Aborted)` pasa a excluir también `Closed` — deja de reengancharse a un BC
   muerto (bug 3 de la spec fiscal) y abre uno nuevo.
2. `TryResolveExistingBcAsync` (líneas 1205-1220, camino de anulación TOTAL): agregar un caso explícito
   ANTES del `throw` final — `if (existingBc.Status == BookingCancellationStatus.Closed) return null;`
   (mismo tratamiento que el caso (b) `Aborted`: "libre para abrir uno nuevo"). Sin esto, el bug nuevo que
   encontré (arriba) deja cualquier reserva con una cancelación parcial ya cerrada IMPOSIBLE de volver a
   anular, total o parcialmente, para siempre.

**Qué seguirá bloqueando** (sin cambios, correcto): un BC en cualquier estado que representa un evento
fiscal REALMENTE en curso (`AwaitingFiscalConfirmation`, `AwaitingOperatorRefund`, `ClientCreditApplied`,
`ManualReviewPending`, `ManualReviewRejected`, `ArcaRejected` con NC viva) sigue rechazando con `INV-081` un
segundo intento simultáneo sobre la MISMA reserva/factura — la migración de índice solo relaja `Closed`, no
toca el resto de la máquina de estados.

**Caso simple, paridad exacta (verificado que no cambia)**: una reserva de 1 solo servicio sigue yendo
100% por `DraftAsync`/`ConfirmAsync` (Scope=`Full`), sin tocar `CancelServiceAsync` ni ninguna de las 3
decisiones de este addendum — cero cambios de código en ese camino, cero cambios de comportamiento.

**Migración esperada**: `Adr044_M_T5b_NarrowBookingCancellationUniqueIndexesExcludeClosed` — altera 2
índices únicos filtrados existentes (drop + recreate con el filtro ampliado). Cambio de dirección SEGURA:
excluir MÁS estados de un único solo puede RELAJAR la restricción, nunca puede violar filas ya existentes
(ninguna fila `Closed` puede volverse "duplicada" retroactivamente por ampliar la exclusión). Sin backfill,
sin riesgo de dato.

### Migraciones esperadas de T5 (en orden)

1. `Adr044_M_T5a_AddTargetInvoiceIdAndConfirmedGrossCreditAmountToLine`
2. `Adr044_M_T5b_NarrowBookingCancellationUniqueIndexesExcludeClosed`

### Orden de construcción sugerido

1. **Prerrequisito de concurrencia** (ya anotado en el ADR desde T0, ahora se activa): lock por
   `TargetInvoiceId` (`pg_advisory_xact_lock` o serializable, patrón `Adr042MultiInvoiceConcurrency`) para
   el cálculo del remanente. Sin esto, ningún paso siguiente es seguro con 2+ NCs parciales concurrentes.
2. **Decisión C primero** (los 2 fixes de código + la migración de índice): es la base estructural — sin
   ella, Decisión B no tiene dónde colgar una segunda línea/NC sobre la misma factura sin chocar el único.
   Incluye el fix del bug nuevo (`TryResolveExistingBcAsync` caso `Closed`).
3. **Decisión B**: campos nuevos en la línea + cálculo de remanente + derivación NC-total-de-la-factura vs
   NC-parcial. Se prueba en aislado (sin tocar el guard todavía) usando el mismo mecanismo `Confirm` que ya
   existe para el flujo total, acotado a la factura.
4. **Decisión A al final**: reemplazar `ResolveServiceCancellationBlockReasonAsync` para que llame al nuevo
   guard (voucher-only + la compuerta de 3 salidas). Se despliega en la MISMA tanda que el punto 3 (nunca
   suelto — ver la dependencia dura de la Decisión A).
5. Gate de siempre antes de producción: `arca-tax-expert-argentina`/`travel-agency-accountant-argentina`
   revisan la fórmula del remanente y la derivación total-vs-parcial antes del primer caso real;
   `security-data-risk-reviewer` + `data-exposure-reviewer` sobre los nuevos mensajes de bloqueo/revisión
   manual (nunca exponer `BookingCancellationStatus`/IDs internos, mismo estándar que
   `CancellationErrorMessageLeakUnitTests`).

### Tests obligatorios antes de mergear T5

1. **Regresión caso simple**: reserva de 1 servicio → cancelar ese servicio sigue yendo por
   `DraftAsync`/`ConfirmAsync` total, byte-idéntico a hoy; `CancelServiceAsync`/`RecordPartialCancellationLineAsync`
   ni se invocan.
2. **Bug del BC `Closed` (camino parcial, punto 3 original de la spec)**: reserva con un BC previo `Closed`
   → cancelar OTRO servicio abre un BC **nuevo** (no le agrega una línea al `Closed`); el `Closed` viejo
   queda intacto, sin líneas nuevas.
3. **Bug del BC `Closed` (camino total, hallazgo nuevo de este addendum)**: reserva con un BC previo
   `Closed` (de una cancelación parcial de 1 servicio) → `DraftAsync` (anular el resto) NO rechaza con
   `INV-081`; abre un BC nuevo correctamente.
4. **`INV-081` sigue bloqueando lo que debe**: un BC en `AwaitingFiscalConfirmation`/`ManualReviewPending`/etc.
   (no `Closed`, no `Aborted`) sigue rechazando un segundo intento de `DraftAsync` o
   `RecordPartialCancellationLineAsync` sobre la misma reserva.
5. **Cap acumulativo, 2 NC sucesivas (test explícito del enunciado)**: factura con 2 servicios vivos →
   cancelar el primero emite NC parcial por su monto, `Succeeded`; cancelar el segundo (evento nuevo, BC
   nuevo) calcula el remanente correctamente restando la primera NC y emite su propia NC parcial dentro de
   lo que queda; intentar una 3ra NC que exceda el remanente restante → rechazada (revisión manual), nunca
   una NC que deje la suma por encima de `Invoice.ImporteTotal`.
6. **Derivación total-vs-parcial por monto**: `ConfirmedGrossCreditAmount == remanente` → sale como NC
   total de esa factura (reusa T0-T4); `< remanente` → `PartialOnOriginal`; `> remanente` → rechazada, no
   se persiste.
7. **2+ facturas activas sin `TargetInvoiceId` elegido** → revisión manual, nunca NC automática contra la
   factura equivocada.
8. **Voucher `Issued` sigue bloqueando** (regresión): con voucher emitido, cancelar el servicio sigue
   rechazando 409, mensaje idéntico al de hoy — la Decisión A no tocó esta rama.
9. **`PayerId` null** → 409 explícito (antes se salteaba en silencio); ningún servicio queda cancelado sin
   pagador a quien facturarle.
10. **Concurrencia**: 2 cancelaciones parciales casi simultáneas sobre servicios que comparten la misma
    `TargetInvoiceId` → el lock serializa; nunca ambas ven el mismo remanente "libre" y lo exceden juntas
    (test estilo `Adr042MultiInvoiceConcurrency`).

**Rollback**: T5a es aditiva (columnas nullables, sin backfill) — `Down()` estándar. T5b solo reduce el
alcance de un filtro de índice (dirección seguros, ver arriba) — `Down()` restaura el filtro viejo sin
riesgo de dato. Los 2 fixes de código (`RecordPartialCancellationLineAsync`, `TryResolveExistingBcAsync`)
son reversión de línea de código estándar, sin estado persistido que dependa de ellos.

### Qué NO resuelve este addendum (anotado, no ignorado)

- Anulación a nivel PASAJERO: fuera de alcance explícito (no existe granularidad de datos para eso, ni en
  `BookingCancellationLine.ServiceId` ni en `Voucher`).
- "Servicio ya viajado" como gate independiente del estado de la reserva: brecha conocida, no es uno de
  los 3 bloqueantes de arquitectura de la spec fiscal, requiere una regla de negocio nueva (qué campo por
  tipo de servicio cuenta como "consumido").
- Prorrateo de pagos parciales entre servicios que quedan vivos vs. el que se anula: pendiente
  Gastón/contador (punto 3 de la spec fiscal), no es arquitectura.
- Alícuota IVA de pass-through para RI: gate humano ya abierto en T3b, ajeno a T5.

### Revisión 2 (2026-07-11) — 2 agujeros de plata nuevos que abre la Decisión C (reviewer incorporado)

> El `software-architect-reviewer` devolvió "Cambios requeridos": la Decisión C (permitir 2+ BCs
> no-abortados por reserva a lo largo del tiempo) rompe el supuesto "una sola cancelación activa por
> reserva" (INV-081) del que dependían DOS lectores de plata FUERA del perímetro que había inspeccionado
> (`ReservaService.cs` + el camino legacy de `Confirm`/`EditLiquidation`). Ambos VERIFICADOS en código. Sin
> estos fixes, la Decisión C tapa el bug del BC `Closed` pero abre dos fugas nuevas. Se incorporan tal cual,
> como parte OBLIGATORIA del alcance de T5.

#### B1 (crítico) — doble NC por el camino legacy de `Confirm`/`EditLiquidation`

**Verificado en código**: `ConfirmAsync` (`BookingCancellationService.cs:1610-1624`) y `EditLiquidationAsync`
(`:3826-3838`) construyen el `FiscalLiquidationInput` con `CancellationAmount: bc.OriginatingInvoice.ImporteTotal`
HARDCODEADO (el comentario propio de la línea 1610-1613 lo dice: "Fase 1 solo soporta cancelación total...
CancellationAmount = ImporteTotal por defecto"). Es decir, el camino de "Anular toda la reserva" siempre
acredita el TOTAL de la factura, sin mirar si ya salió una NC parcial contra esa misma factura.

**El agujero que abre la Decisión C**: con `Closed` fuera del único (Decisión C), esta secuencia queda
posible y hoy sumaría de más:
1. T5 emite una NC parcial por un servicio (BC1, luego `Closed`); la factura sigue viva por el resto.
2. El usuario dispara "Anular" el resto por el camino viejo (`DraftAsync` → `ConfirmAsync`).
3. `ConfirmAsync` acredita `ImporteTotal` COMPLETO → suma de NCs (parcial previa + total ahora) supera el
   importe de la factura. Exactamente el descuadre que el cap acumulativo de la Decisión B vino a evitar,
   pero por un camino que la Decisión B no tocaba.

**Fix (parte obligatoria de T5, en el MISMO commit que las Decisiones A/B/C)**:
- **(a) El camino legacy capea `CancellationAmount` contra el remanente real**, no contra `ImporteTotal`.
  `ConfirmAsync:1619-1620` y `EditLiquidationAsync:3833-3834` pasan a usar
  `CancellationAmount = remanente(OriginatingInvoiceId)` con la MISMA fórmula de la Decisión B
  (`Invoice.ImporteTotal − SUM(BookingCancellationCreditNote.CreditNoteInvoice.ImporteTotal WHERE
  OriginatingInvoiceId = esa factura AND Status IN (Succeeded, Pending))`). `OriginalInvoiceAmount` queda
  en `ImporteTotal` (es la base fiscal del comprobante original, no cambia); lo que se acota es cuánto se
  acredita. Cuando no hubo ninguna NC parcial previa (el 100% de los casos de hoy), remanente ==
  `ImporteTotal` → comportamiento byte-idéntico al actual, cero regresión.
- **(b) `BuildCancellationLinesAsync` (`:9453-9527`) EXCLUYE los servicios YA CANCELADOS — SOLO en el
  build de alcance TOTAL** [corrección C1 del re-review 2026-07-11]. Verificado: hoy los 6 queries
  (hotels/flights/transfers/packages/assistances/generics, líneas 9492-9527) traen TODOS los servicios
  sin filtrar por estado; tras una cancelación parcial previa, el servicio cancelado re-aportaría línea y
  RefundCap en la anulación total siguiente → doble cómputo. PERO el mismo builder tiene DOS call-sites
  con semántica OPUESTA: `DraftAsync:255` (Scope=Full — acá SÍ se excluye) y
  `RecordPartialCancellationLineAsync:1020-1023` (Scope=Partial, `onlyServiceTable/onlyServiceId` —
  construye la línea del servicio que SE ACABA DE CANCELAR, ya marcado IsCancelled en el paso previo:
  filtrar acá dejaría `builtLines` vacío y `builtLines[0]` tira → rompería la cancelación parcial entera).
  REGLA: el filtro `ServiceResolutionRules.IsCancelled(...)` (mismo helper de
  `MarkTypedServiceCancelledAsync`/`CancelAllReservaServicesAsync`) se aplica ÚNICAMENTE cuando
  `scope == Full` (equivalente: `!wantsOne`); el build de una sola línea NUNCA filtra por cancelado.
  Verificar en implementación que el call-site del guard R1 (`ComputeUnanchoredOperatorRefundCapAsync:933`)
  quede del lado Full del scoping (un servicio ya cancelado no debe re-aportar cap — el fix lo alinea).
- **(c) Test obligatorio (ampliado por C1)**: "cancelar 1 servicio → luego Anular el resto" debe verificar
  TRES cosas: (i) el build de la anulación total EXCLUYE el servicio ya cancelado (no genera su línea/cap),
  (ii) el `CancellationAmount` de la NC total respeta el remanente (suma de las dos NCs ≤ `ImporteTotal`),
  y (iii) el camino PARCIAL sigue armando la línea del servicio recién cancelado (el filtro no aplica
  a Scope=Partial — regresión que C1 detectó en la redacción original).

#### B2 (crítico) — el cartel de multa elige una BC arbitraria

**Verificado en código**: `DeriveCancelledMoneyContextAsync` (`ReservaService.cs:5151-5154`) hace
`FirstOrDefaultAsync` SIN `OrderBy` sobre `LiveDebitNotePredicate`; `FillCancelledMoneyContextForListAsync`
(`:5205-5216`) hace `GroupBy(PublicId).First()` — y su propio comentario (línea 5212) dice explícitamente
"INV-081 garantiza una sola cancelación activa por reserva; si hubiera más de una fila... tomamos la
primera". **Ese es exactamente el supuesto que la Decisión C rompe.** Además, verifiqué que la 2da rama de
`LiveDebitNotePredicate` (`CancellationPenaltyRules.cs:61-64`: `PenaltyStatus == Confirmed &&
PenaltyAmountAtEvent > 0 && DebitNoteStatus IN {NotApplicable, Pending}`) es PERMANENTE — sobrevive a que el
BC quede `Closed`. Con dos BCs con multa viva simultánea (dos anulaciones parciales de dos servicios de
operadores distintos, cada una con su multa), el `FirstOrDefault`/`First` devuelve una fila ARBITRARIA:
monto/moneda del cartel equivocados, o total de multa subestimado (solo el de una BC).

**Fix (parte obligatoria de T5)**: ambos lectores dejan de tomar UNA sola BC y AGREGAN sobre TODAS las BCs
de la reserva con multa viva:
- El cartel de la ficha (`DeriveCancelledMoneyContextAsync`) y el del listado
  (`FillCancelledMoneyContextForListAsync`) suman el `PenaltyAmountAtEvent` de TODAS las BCs que cumplen
  `LiveDebitNotePredicate`, **agrupado por `PenaltyCurrencyAtEvent`** (nunca se suman monedas distintas —
  mismo principio "por moneda" que rige todo el módulo de plata). El resultado que se muestra es lo
  PENDIENTE por moneda (neto de lo ya pagado en esa moneda, reusando `ComputePendingPenaltyForDisplay` como
  hoy), pero sobre la suma, no sobre una fila.
- Si hay más de una moneda de multa viva simultánea, el cartel muestra el desglose (una línea por moneda) en
  vez de un único número — el DTO `CancelledMoneyInfo`/`ReservaListDto` pasa de un par escalar
  (`CancelledPenaltyAmount`/`CancelledPenaltyCurrency`) a una lista por moneda. El caso de 1 sola multa (el
  100% de hoy) rinde exactamente igual que ahora (lista de un elemento → mismo número).
- El comentario engañoso de la línea 5212 ("INV-081 garantiza una sola...") se elimina/corrige: tras la
  Decisión C ya NO es cierto que haya una sola BC no-abortada por reserva.
- **Test obligatorio**: el cartel con DOS BCs con ND/multa viva simultánea (mismo operador o distintos)
  muestra la SUMA por moneda, no el monto de una BC arbitraria; con dos monedas distintas, muestra el
  desglose.

#### Requisitos de diseño explícitos (no analogías) que el reviewer exige escribir

1. **La transición `Pending → Failed` que libera el remanente REUSA literalmente
   `ApplyChildResultAndReevaluateAsync`** (`BookingCancellationService.cs:4132-4181`, ADR-042) — no es "el
   mismo patrón", es el MISMO método. Verificado: cuando ARCA rechaza una NC, ese método pone
   `child.Status = BookingCancellationCreditNoteStatus.Failed` (`:4172`) y persiste (`:4177`). Como la
   fórmula del remanente de la Decisión B suma solo `Status IN (Succeeded, Pending)`, una hija que pasa a
   `Failed` sale automáticamente del cálculo → su monto vuelve a estar disponible para la siguiente NC
   parcial de esa factura, sin código nuevo de "liberación". Requisito de diseño duro: la Decisión B NO
   inventa su propia máquina de estados de la NC; se cuelga de la de ADR-042.
2. **Las Decisiones A y B salen en el MISMO commit/deploy, nunca parcial.** Ya estaba anotado como
   dependencia dura; se eleva a **invariante de la tanda**: aflojar el guard SEC-B1 (Decisión A) sin la
   emisión real capeada (Decisión B) reabre el agujero fiscal original; construir la emisión (Decisión B)
   sin el fix B1(a) del camino legacy deja la puerta de la doble-NC por `ConfirmAsync`. Los tres fixes
   (A, B, B1) son atómicos: o entran todos, o no entra ninguno.
3. **Test faltante (además de los ya listados)**: una NC parcial `Pending` que luego FALLA en ARCA libera
   el remanente — tras el `Failed`, cancelar otro servicio de la misma factura ve el remanente restaurado
   (el monto de la NC fallida vuelve a estar disponible) y su NC parcial se calcula sobre ese remanente
   ampliado.

#### Trade-off del bloqueo secuencial (decisión tomada, se documenta; se informa a Gastón, NO es gate)

Con la Decisión C, **las anulaciones parciales de servicios INDEPENDIENTES no se bloquean entre sí**: cada
BC vive su propio ciclo (puede quedar semanas esperando el reembolso de SU operador) sin frenar que mañana
se anule otro servicio de otro operador de la misma reserva. Esto es la esencia de T5 y es coherente con la
Decisión C (cada evento = su BC). Es un cambio deliberado respecto del mundo INV-081 ("una cancelación
activa por reserva a la vez").

Qué pasa si el MISMO operador tiene dos anulaciones en curso (dos servicios del mismo operador, cancelados
en momentos distintos): quedan DOS BCs, cada uno con su línea de ese operador. El reviewer verificó que el
`SupplierCancellationCircuitReader`/extracto del operador YA suma por línea (no por BC), así que el extracto
del operador refleja correctamente la suma de ambos receivables/multas — no hay doble cómputo ni pérdida. No
requiere trabajo adicional; se anota como **dato a informar a Gastón en el reporte final** (comportamiento
nuevo esperado: puede ver dos anulaciones abiertas del mismo operador sobre la misma reserva conviviendo),
NO como gate ni como bloqueante.

#### Tests obligatorios añadidos en Revisión 2 (se suman a los 10 originales)

11. **B1(a) — remanente en el camino legacy**: cancelar 1 servicio (NC parcial `Succeeded`) → luego Anular
    el resto por `ConfirmAsync`; el `CancellationAmount` de la NC total usa el remanente, no `ImporteTotal`;
    la suma de las dos NCs no supera `Invoice.ImporteTotal`.
12. **B1(b) — exclusión de cancelados en el build**: tras una cancelación parcial, `BuildCancellationLinesAsync`
    de la anulación total siguiente NO genera línea ni RefundCap para el servicio ya cancelado.
13. **B2 — cartel con 2 BCs con multa viva**: dos anulaciones parciales con multa viva simultánea → el
    cartel de la ficha y el del listado muestran la SUMA por moneda (y desglose si hay 2 monedas), no una
    BC arbitraria.
14. **Pending → Failed libera remanente**: una NC parcial `Pending` que falla en ARCA (vía
    `ApplyChildResultAndReevaluateAsync`) restaura el remanente para la siguiente NC parcial de esa factura.

#### Orden de construcción — ajuste de Revisión 2

Los fixes B1(a), B1(b) y B2 entran en el paso 2 (Decisión C) del orden original, PORQUE son consecuencia
directa de excluir `Closed` del único: no tiene sentido mergear la Decisión C sin cerrar simultáneamente las
dos fugas que abre. El invariante de la tanda (A+B+B1 atómicos) gobierna el commit final.

**C2 [re-review 2026-07-11]** — el candado de concurrencia por factura (paso 1) envuelve TAMBIÉN el
cálculo remanente-y-emisión del camino LEGACY (B1(a), dentro de `ConfirmAsync`/`EditLiquidationAsync`),
no solo el de `CancelServiceAsync`: una NC parcial T5 y una anulación total legacy casi simultáneas sobre
la misma factura no pueden leer ambas el mismo remanente. Mismo `pg_advisory_xact_lock`/serializable sobre
`OriginatingInvoiceId` en los tres puntos de escritura de NC.
