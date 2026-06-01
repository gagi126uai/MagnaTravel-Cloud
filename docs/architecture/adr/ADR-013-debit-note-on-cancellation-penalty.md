# ADR-013 — Nota de Debito (ND) por penalidad en el flujo de cancelacion

- **Status**: Propuesto (Draft) — **Round 2 (post Changes Required)**. Incorpora las correcciones del `software-architect-reviewer` (B1 invariante reescrito a nivel evento; B2 + M1 verificados en codigo con archivo:linea; M2/M3/M4 + auditoria clasificador; §1.2 y §3.5 corregidos). **Pendiente de re-review** del reviewer, de confirmacion fiscal del contador matriculado (§11) y de homologacion ARCA para ND C (§3.6). **NO Accepted.** Prender el flag en prod depende de signoff fiscal + CAE de homologacion. **Construir el MVP depende de un fix bloqueante backend (M1, §3.9 — bug de `CbteTipo`).** Ver §14 para el cierre del review.
- **Date**: 2026-06-01 (round 2).
- **Author(s)**: software-architect agent.
- **Related**:
  - Criterio del contador MATRICULADO (2026-06-01): `.claude/agent-memory/travel-agency-accountant-argentina/contador-matriculado-nc-total-mas-nd.md`. Es la **fuente** de las reglas fiscales de este ADR. Donde el matriculado no cerro un caso (RI/IVA, pass-through documentado, IIBB, seguros reales), este ADR lo manda a **revision manual**, no inventa tratamiento.
  - [ADR-009 NC parcial fiscal](ADR-009-partial-credit-note.md) — el enfoque OPUESTO (netear la penalidad DENTRO de la factura, dejando viva una parte). El matriculado lo **descarto**. Su flag `EnablePartialCreditNoteRealEmission` queda **CONGELADO** (no se prende, no se borra). Este ADR **recicla** piezas de FC1.3: el `FiscalLiquidation` VO (sirve de base para el snapshot de la ND), el clasificador de disparadores a manual, los thresholds/approvals 4-eyes, el patron de snapshot fiscal y el patron de flag OFF.
  - [ADR-012 Multimoneda](ADR-012-end-to-end-multicurrency.md) — de aca salen el patron de flag OFF byte-identico, el `ExchangeRateSource`, la herencia de moneda en NC/ND y el hecho de que la NC/ND **total en USD esta hoy bloqueada** (guard de moneda). Este ADR depende de ese trabajo SOLO para el caso multimoneda, que en v1 va a **revision manual**.
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md) — de aca sale `FiscalSnapshot` (TC + condicion fiscal + modo congelado al momento del evento) y el ciclo T0/T2/T3.

---

## 1. Contexto

### 1.1 El ejemplo pelotudo (la multa de la inmobiliaria)

Imaginate que alquilaste un departamento por una inmobiliaria y lo pagaste todo: $100.000.
Despues cancelas. El contrato dice que la inmobiliaria se queda con una multa de $30.000 por
cancelar.

Hay dos formas de hacer los papeles:

- **Forma A (la que hace HOY el sistema):** la inmobiliaria te anula toda la factura de $100.000
  (te hace una **Nota de Credito total**) y la multa de $30.000 queda como un numerito anotado a
  mano "el cliente nos debe $30.000 de multa", **sin papel fiscal propio**.
- **Forma B (la que firmo el contador matriculado):** la inmobiliaria te anula toda la factura
  con la **NC total** Y ademas te emite un **papel nuevo, una Nota de Debito de $30.000**, que dice
  "esto es la multa, es plata MIA". Resultado: te devolvieron $100.000 en los papeles, pero te
  cobraron $30.000 con un comprobante real. Neto facturado: $30.000.

**El detalle clave** que el contador remarco: la Forma B (emitir la ND) **solo corresponde cuando
la multa es plata de la agencia**. Si la multa se la queda el **mayorista/operador** (la agencia
solo es el cartero que pasa la plata), la agencia **NO** emite la ND — porque no es su ingreso.
Si emitiera una ND por plata ajena, estaria declarando como venta propia algo que no le pertenece
(riesgo fiscal). En ese caso la agencia hace **solo la NC total**, igual que hoy.

Este ADR conecta el **motor de ND que ya existe** (en facturacion general) al **flujo de
cancelacion** (donde hoy nunca se emite ND), para el caso "la multa es de la agencia", en
Monotributo (ND clase **C**, sin IVA discriminado).

### 1.2 Lo que YA esta hecho (verificado en el repo — NO hay que construirlo)

Sorpresa importante: **el motor de ND ya existe** en la facturacion general. No partimos de cero.

- **El pipeline sabe emitir una ND.** `CreateInvoiceRequest` tiene `IsDebitNote` y `OriginalInvoiceId`
  (`CreateInvoiceRequest.cs:17,19`). **VERIFICADO.**
- **El mapeo de tipo de comprobante ND con factura origen tiene un BUG para factura C=11
  (VERIFICACION M1 — leida hoy en `AfipService.cs:620-626`).** **CORRECCION FACTUAL respecto de la
  version anterior de este ADR**, que afirmaba "C(11/12)->ND C=12". Eso es **FALSO**. El switch real
  (rama `request.IsDebitNote` **con** `originalInvoice != null`) es:
  ```
  if (t == 3)  cbteTipo = 2;    // NC A   -> ND A
  else if (t == 8)  cbteTipo = 7;    // NC B   -> ND B
  else if (t == 13) cbteTipo = 12;   // NC C   -> ND C
  else if (t == 53) cbteTipo = 52;   // NC M   -> ND M
  ```
  O sea: la rama "ND con factura origen" mapea **desde tipos de Nota de Credito** (3/8/13/53), **NO
  desde tipos de factura**. **NO contempla `t == 11` (factura C).** Si una ND llega con
  `OriginalInvoiceId` apuntando a una **factura C=11** (el caso EXACTO del MVP: ND asociada a la
  factura original), **NINGUNA rama matchea** y `cbteTipo` queda en su valor inicial `baseType`
  (`:609`), que para Monotributo es **11 = factura C**. **Resultado: la ND saldria con
  `CbteTipo=11` (FACTURA C), no `12` (ND C).** Eso es un comprobante equivocado -> rechazo ARCA o,
  peor, emision de una "factura" en vez de una ND. **Este es un bug bloqueante para el backend
  (§3.9).** La rama "sin factura origen" (`:636-641`) SI deriva bien de `baseType` (C=11 -> ND
  C=12), pero el MVP usa factura origen, asi que cae en la rama bugueada.
- **La asociacion al comprobante (`CbtesAsoc`) ya se arma.** Si `invoice.OriginalInvoiceId` tiene
  valor, el envelope SOAP emite el `<CbtesAsoc>` apuntando al `OriginalInvoice` (Tipo/PtoVta/Nro/Cuit)
  (`AfipService.cs:1151-1162`). **VERIFICADO.** O sea: asociar la ND a un comprobante **ya funciona**;
  lo que falta es decidir **a cual** (factura original vs NC) segun la finalidad (§3.5).
- **`Invoice.OriginalInvoiceId` existe** (`Invoice.cs:168-169`, FK a `OriginalInvoice`). **VERIFICADO.**
- **Hay infra de emision async + idempotencia + audit + approvals 4-eyes.** `ProcessInvoiceJob` ->
  CAE, idempotency keys (Fase2_M1b), `MovementsService.DebitNote`, snapshot fiscal multimoneda. Se
  reusa, no se reinventa.
- **El tipo de comprobante (A/B/C) lo deriva la condicion fiscal del emisor** (`AfipService.cs:597-607`):
  RI -> A/B; Mono/Exento -> C. **VERIFICADO.** Mono emite ND C sin tocar esa logica.
- **El operador (`Supplier`) ya es input fiscal de la cancelacion.** `Supplier.InvoicingMode`
  (`Supplier.cs:60`: `TotalToCustomer` / `CommissionOnly`) ya existe y se congela en
  `FiscalSnapshot.InvoicingModeAtEvent`. **VERIFICADO.** Pero ese eje es "reseller vs intermediario",
  **NO** modela "quien se queda la penalidad" (§3.7). Hay que agregar ese eje nuevo.
- **El patron de flag OFF por default ya esta probado.** `OperationalFinanceSettings.EnablePartialCreditNoteRealEmission`
  (default `false`, `OperationalFinanceSettings.cs:276`) y `EnableSoldToSettleStates` (`:390`) son el
  molde. **VERIFICADO.** Este ADR copia ese patron con un flag nuevo.

### 1.3 Lo que NO esta hecho (los agujeros reales que ataca este ADR)

1. **La cancelacion NUNCA emite una ND.** `BookingCancellationService` solo emite NC (total via
   FC1.2, o parcial via el flag congelado). Busque emision de ND en ese servicio -> no existe.
   **VERIFICADO (ausencia).** Este es el agujero central.
2. **La penalidad no es un estado, es una convencion.** Entra como `req.OperatorPenaltyAmountOverride`
   (input manual post-acuerdo "T2", `BookingCancellationService.cs:1151`). No hay
   `Estimated`/`Confirmed` modelado. **VERIFICADO.** El contador exige NO emitir comprobante sobre
   penalidad estimada -> hay que modelar `PenaltyStatus`.
3. **No hay clasificacion de "quien se queda la penalidad".** Como dijimos, `Supplier.InvoicingMode`
   es otro eje. No hay flag pass-through. **VERIFICADO.**
4. **No hay nodo de leyenda/observacion libre que se EMITA al ARCA.** `invoice.Observaciones`
   (`AfipService.cs:1253,1281,1295,1314`) se usa SOLO para guardar el error/observacion que devuelve
   ARCA — **no se manda** un texto libre en el envelope de emision. **VERIFICADO.** Esto es un hallazgo
   importante: el requisito "ND con leyenda mencionando la NC" (RG 4540) **no tiene hoy donde ir** en
   el SOAP. Hay que ver si el WSFEv1 acepta el campo `Opcionales`/observacion y homologarlo, o si la
   leyenda vive solo en el PDF/representacion impresa (a confirmar contador + homologacion, §3.5).
5. **La penalidad puede netear el refund al cliente HOY — y ESE es el doble-cobro real a prevenir
   (VERIFICACION B2, ver abajo).** El review anterior de este ADR apuntaba al objeto equivocado: decia
   que el riesgo era poner la penalidad dentro del VO `FiscalLiquidation` y que el CHECK de suma la
   neteara. **Eso no protege nada en el flujo del MVP**, porque (VERIFICADO leyendo el codigo hoy) el
   VO `FiscalLiquidation` **SOLO se escribe dentro de `if (settings.EnablePartialCreditNotes)`**
   (`BookingCancellationService.cs:396` abre el if; escritura del VO en `:512-515`). En el **path
   FC1.2 puro** (NC parcial OFF, el que usa el MVP) **el VO queda NULL** -> el CHECK de suma tiene
   clausula `IS NULL OR...` (`FiscalLiquidation.cs`) y **ni siquiera aplica**. Una guarda sobre
   "`OperatorPenaltyAmount == 0` en el VO" protegeria un objeto que en este flujo **no se materializa**.

   **El doble-cobro REAL es otro (verificado en §1.5 / VERIFICACION B2):** la penalidad ya tiene un
   camino por donde **reduce lo que se le devuelve al cliente**. Si encima de eso emitimos una ND por
   la misma penalidad, la cobramos DOS veces (una bajando el refund, otra con el comprobante). El
   invariante correcto se reformula a **nivel evento de cancelacion** (§3.3), no a nivel VO null.

### 1.4 Normativa relevante (del archivo del matriculado — verificada por el contador-integrado con web)

- **NC total + ND por penalidad** (round 1). Ejemplo $100k / $30k: NC $100.000 + ND $30.000.
- **R1 — pass-through del operador = SOLO NC total, NO ND propia.** La ND propia SOLO cuando la
  penalidad es ingreso de la agencia.
- **R2 — ND propia: Mono = C sin IVA; RI = A/B con 21% (cargo de gestion = servicio gravado).**
  El RI/IVA es **FUTURO**, fuera del MVP.
- **R3 — asociacion segun finalidad (`DebitNotePurpose`):** `PenaltyOrCancellationCharge` -> asociar
  a **factura original** + leyenda mencionando la NC. `CorrectCreditNote` -> asociar a la NC +
  revision contable. FCE MiPyME -> NUNCA automatico.
- **R4 — clasificar conceptos (`CancellationConceptKind`):** `OperatorPenaltyPassThrough` (NO ND) /
  `AgencyManagementFee` / `AgencyCancellationFee` (ND gravada) / `RealInsurancePremium` (revision) /
  `AgencyCancellationCoverage` / `AgencyInsuranceCommission`. PROHIBIDO string libre.
- **R5 — `PenaltyStatus` Estimated/Confirmed: NO emitir comprobante sobre estimado.** Real>estimado ->
  ND complementaria por diferencia; real<estimado -> NC complementaria. Plazo **15 dias CORRIDOS**
  desde que el OPERADOR confirma (RG 4540: mismo receptor + comprobante asociado + 15 corridos).
- **R6 — IIBB NO se discrimina en el comprobante.** Solo aparece como percepcion si la agencia es
  agente de percepcion -> **FUERA del MVP, revision manual.**

> **Aviso profesional**: este ADR analiza y disena. La validez vigente de RG 4540, el tratamiento del
> IVA de la ND en RI, el valor del campo de leyenda y el plazo de 15 dias deben ser confirmados por el
> contador matriculado antes de produccion. NO es autoridad fiscal final.

### 1.5 VERIFICACION B2 — ¿la penalidad netea el refund al cliente HOY? (precondicion del invariante)

**Pregunta del reviewer:** trazar que hace hoy la penalidad sobre los campos economicos del
`BookingCancellation` y las allocations del operador. ¿La penalidad YA reduce el refund al cliente? Si
SI -> emitir ND encima es doble cobro. Esta es la pieza mas importante del ADR.

**Respuesta verificada (leido hoy):** **HAY DOS conceptos de "penalidad" en dos flujos distintos. Uno
SI netea el refund.**

**(A) Penalidad que RETIENE EL OPERADOR — `DeductionKind.CancellationPenalty=3`. SI netea el refund.**
- Existe el enum `DeductionKind.CancellationPenalty = 3` (`DeductionKind.cs:33-34`: "Penalidad
  contractual por cancelacion").
- En `OperatorRefundService.AllocateAsync`: `netAmount = GrossAmount - totalDeductions`
  (`OperatorRefundService.cs:338-339`). El `GrossAmount` es lo que el operador devolvio; las
  deducciones (entre ellas `CancellationPenalty`) se **restan**.
- Ese `netAmount` es **exactamente** lo que el cliente recibe como saldo a favor (`ClientCreditEntry`,
  `OperatorRefundService.cs:413-414`) y lo que incrementa `bc.ReceivedRefundAmount`
  (`OperatorRefundService.cs:405`).
- **Conclusion:** cuando la penalidad la retiene el operador y se carga como deduction, el cliente
  recibe MENOS. **Esto es el caso PASS-THROUGH** (plata del operador). El MVP **NO** emite ND aca
  (gating §3.4.1: pass-through -> solo NC total). Correcto que netee: la plata se la quedo el operador.

**(B) Penalidad propia de la agencia — `OperatorPenaltyAmountOverride`. Netea SOLO en el path NC
parcial (congelado); en el path FC1.2 puro (el del MVP) NO existe / NO netea.**
- El override entra en `EditLiquidation` (`BookingCancellationService.cs:1151`) y alimenta el
  calculator (`:1159`).
- En el `FiscalLiquidationCalculator` (path NC parcial): `fiscalAmountToCredit = OriginalInvoiceAmount
  - nonRefundableTotal - penalty` (`FiscalLiquidationCalculator.cs:255`) y `amountToRefundCustomer =
  fiscalAmountToCredit` (`:281`). O sea: **ahi la penalidad SI resta** (baja lo que se acredita Y lo
  que se devuelve al cliente). **PERO ese path esta CONGELADO** (flag `EnablePartialCreditNotes` OFF)
  y es justo el criterio que el contador descarto.
- En el **path FC1.2 puro** (el del MVP, NC parcial OFF): el calculator se invoca con
  `OperatorPenaltyAmount: 0m` **hardcodeado** (`BookingCancellationService.cs:444-453`). Resultado:
  `AmountToRefundCustomer = OriginalInvoiceAmount` completo, la NC anula TODO, y **la penalidad propia
  de la agencia HOY no tiene donde cargarse en este flujo** (no existe el concepto).

**Conclusion para el invariante (precondicion verificada):**
- En el caso **pass-through** (operador retiene): la penalidad **ya netea** via `DeductionKind` ->
  por eso el MVP **NO** emite ND ahi (seria doble cobro). El gating §3.4.1 lo cubre.
- En el caso **ingreso propio de la agencia** (el que SI emite ND): hoy en el path FC1.2 esa penalidad
  **no reduce el refund** (la NC devuelve el total). Asi que la ND es **plata nueva** y NO hay doble
  cobro... **siempre que la implementacion del MVP NO la cargue ademas como `DeductionKind`
  reduciendo el refund, ni la netee contra la liquidacion del operador.** Ese es el invariante B1
  (§3.3): la penalidad propia se materializa **EXACTAMENTE UNA VEZ**, como ND, sin tambien bajar el
  refund. Si el backend permitiera cargar la penalidad propia como deduction Y ademas emitir la ND,
  ahi nace el doble cobro.

**Pieza que NO pude cerrar leyendo solo esto (declarado explicito):** no traze el flujo COMPLETO de
quien decide el `GrossAmount` ni todos los callers de allocations cruzados con el clasificador de
concepto. La afirmacion "en FC1.2 la penalidad propia no reduce el refund" se apoya en que el path
FC1.2 manda `penalty=0` al calculator (`:453`) y la NC acredita el total — verificado. Lo que el
**backend debe garantizar** (no esta garantizado hoy por construccion, porque el concepto "penalidad
propia" todavia no existe en FC1.2) es que cuando se introduzca, NO se enchufe a la vez al neteo del
refund (`DeductionKind.CancellationPenalty`) Y a la ND. **Eso es una precondicion verificada que el
backend tiene que respetar, no algo que el codigo ya impide.**

---

## 2. Que entra y que NO entra

### 2.1 Entra (a lo largo de las fases, todo detras del flag `EnableCancellationDebitNote`)

1. **MVP — emitir ND C por penalidad** cuando: concepto = ingreso propio de la agencia
   (`AgencyManagementFee` / `AgencyCancellationFee`), emisor Monotributo, `PenaltyStatus = Confirmed`,
   moneda ARS, `DebitNotePurpose = PenaltyOrCancellationCharge`, asociada a la factura original. La ND
   se emite **DESPUES** de la NC total, en el flujo de cancelacion, reusando el pipeline ND existente.
2. **Modelo de datos nuevo:** `PenaltyStatus`, `DebitNotePurpose`, `CancellationConceptKind`, el eje
   pass-through en `Supplier`, y el snapshot de la ND (§3.1).
3. **Invariante nuevo del `FiscalLiquidation`** que modele la ND que incrementa, sin romper el CHECK
   actual (§3.3).
4. **Gating conservador:** todo lo que no sea el caso feliz va a **revision manual** (§3.4).
5. **Snapshot fiscal de la ND** congelado al momento del evento (§3.8).
6. El **flag `EnableCancellationDebitNote`** (OFF por default). Con OFF, comportamiento **byte-identico**
   al actual: NC total, sin ND.

### 2.2 NO entra (scope cut explicito)

- **NO RI / ND con IVA discriminado (A/B).** El matriculado lo definio (R2) pero es FUTURO. En v1, si
  el emisor es RI -> **revision manual** (no emitir ND automatica).
- **NO pass-through automatico.** Si la penalidad es del operador -> SOLO NC total (comportamiento de
  hoy). La agencia **no** emite ND. Eso ya es lo que pasa hoy; el MVP simplemente **no dispara** la ND.
- **NO multimoneda en la ND.** Factura USD -> ND USD con TC heredado es FUTURO (depende de ADR-012 NC/ND
  total, que hoy esta bloqueada). En v1, factura no-ARS -> **revision manual**.
- **NO FCE MiPyME.** Nunca automatico (R3).
- **NO seguros reales** (`RealInsurancePremium`) ni `CorrectCreditNote` -> revision manual.
- **NO IIBB / percepciones** discriminadas (R6) -> revision manual si la agencia es agente de percepcion.
- **NO toca el flag `EnablePartialCreditNoteRealEmission`** (NC parcial) — sigue congelado.
- **NO diferencia de cambio contable ni asientos.** No existe modulo contable (ADR-012 §2.2). Fuera de scope.
- **NO backfill.** Cancelaciones viejas quedan como estan (NC total sin ND). Migracion 100% aditiva.

---

## 3. Decision

### 3.1 Modelo de datos — los tres enums, el eje del operador y el snapshot de la ND

**Principio**: el MVP modela explicitamente lo que hoy es convencion (la penalidad como override
manual) y agrega solo lo necesario. Todo aditivo, nullable, flag OFF = nadie lo setea.

**Enums nuevos (en `Domain`)**:

| Enum | Valores | Para que |
|---|---|---|
| `PenaltyStatus` | `Estimated`, `Confirmed` | NO emitir comprobante sobre estimado (R5). Default `Estimated`. |
| `DebitNotePurpose` | `PenaltyOrCancellationCharge`, `CorrectCreditNote`, `FceMiPyme` | finalidad -> a que se asocia la ND (R3). MVP solo automatiza `PenaltyOrCancellationCharge`. |
| `CancellationConceptKind` | `OperatorPenaltyPassThrough`, `AgencyManagementFee`, `AgencyCancellationFee`, `RealInsurancePremium`, `AgencyCancellationCoverage`, `AgencyInsuranceCommission` | clasifica la naturaleza fiscal (R4). PROHIBIDO string libre. MVP automatiza solo `AgencyManagementFee` / `AgencyCancellationFee`. |

**Donde viven estos campos** (decision de diseno, a desafiar — §10):

- `PenaltyStatus`, `CancellationConceptKind` y `DebitNotePurpose` se agregan como **columnas en
  `BookingCancellation`** (la entidad que ya orquesta la cancelacion y ya tiene el penalty override).
  Son atributos del **evento de cancelacion**, no de un comprobante independiente. Default conservador:
  `PenaltyStatus = Estimated`, `CancellationConceptKind = OperatorPenaltyPassThrough` (= NO ND, igual a
  hoy), `DebitNotePurpose = null` (solo se setea cuando hay ND).
- El **eje pass-through del operador** vive en `Supplier` (§3.7).
- El **vinculo a la ND emitida** vive en `BookingCancellation` como FK nullable `DebitNoteInvoiceId`
  (apunta a la `Invoice` que es la ND), espejando como ya se vincula la NC. Esto da idempotencia
  (§3.4) y trazabilidad.

**El snapshot de la ND** (§3.8): se congela `PenaltyAmountAtEvent`, `CurrencyAtEvent`,
`DebitNoteCbteTipoAtEvent` (ej. 12=C), `EmitterTaxConditionAtEvent`, `PenaltyConfirmedByUserId`,
`PenaltyConfirmedAt`. **Decision (a desafiar §10):** reusar / extender el `FiscalSnapshot` existente
del modulo de cancelacion (ya congela TC + condicion fiscal + modo) en vez de crear un VO nuevo. El
`FiscalSnapshot` ya esta acoplado al ciclo de cancelacion T0/T2/T3 — extenderlo es coherente (a
diferencia de ADR-012, que lo descarto para la factura directa por NO ser cancelacion).

**Migraciones**: 100% aditivas (3 columnas enum + 1 FK nullable en `BookingCancellation` + 1 columna
enum en `Supplier` + campos de snapshot + setting nuevo). Postgres (comillas dobles, `xmin` concurrency
ya existente). Sin backfill (las cancelaciones viejas quedan con defaults conservadores = NO ND).

### 3.2 Como se relaciona con `OperatorPenaltyAmountOverride`

Hoy la penalidad entra como `req.OperatorPenaltyAmountOverride` (`:1151`), un numero suelto. El MVP
lo **enriquece**, no lo reemplaza: ese numero sigue siendo el **monto** de la penalidad. Lo nuevo es
que ese monto ahora viene acompañado de:
- **un estado** (`PenaltyStatus`): si es `Estimated`, NO se emite ND (se espera la confirmacion del
  operador);
- **una clasificacion** (`CancellationConceptKind`): si es `OperatorPenaltyPassThrough`, NO se emite ND
  (es del operador);
- **una finalidad** (`DebitNotePurpose`).

O sea: el numero es el mismo; lo que cambia es que ahora el sistema sabe **si ese numero genera un
comprobante propio o no**, en vez de dejarlo siempre como override silencioso.

### 3.3 El invariante anti-doble-cobro (REESCRITO tras review — nivel evento de cancelacion)

**Que estaba mal en la version anterior:** el invariante apuntaba al VO `FiscalLiquidation` ("poner
`OperatorPenaltyAmount=0` en el VO para que el CHECK de suma se cumpla"). El reviewer verifico (y se
reconfirmo en §1.3 punto 5 + §1.5) que **ese VO NO se materializa en el flujo del MVP**: solo se
escribe dentro de `if (settings.EnablePartialCreditNotes)` (`BookingCancellationService.cs:396`,
escritura `:512-515`); en el path FC1.2 puro (NC parcial OFF) queda NULL y el CHECK no aplica
(`IS NULL OR...`). Una guarda sobre ese VO **no protege nada en este flujo**.

**El doble-cobro REAL (verificado en §1.5):** la penalidad tiene un camino donde **reduce el refund al
cliente** (`DeductionKind.CancellationPenalty=3` -> `netAmount = GrossAmount - deductions` ->
`ClientCreditEntry`). Si una penalidad de **ingreso propio de la agencia** se carga ahi (bajando el
refund) **Y ademas** se emite una ND por ese mismo monto, **se cobra dos veces**.

**Invariante nuevo (INV-ADR013-001) — a nivel EVENTO de cancelacion, no a nivel VO:**
> La penalidad confirmada de **ingreso propio de la agencia** se materializa **EXACTAMENTE UNA VEZ**,
> como **Nota de Debito**. Cuando el evento de cancelacion emite una ND por la penalidad propia, esa
> misma penalidad **NO debe**: (a) reducir el monto a reembolsar al cliente (no cargarse como
> `DeductionKind.CancellationPenalty` ni como ningun otro neteo del refund), **ni** (b) netearse contra
> la liquidacion del operador. La penalidad propia es **plata nueva** (la ND la cobra); el cliente
> recibe el refund **completo** que corresponda por la NC total. Una penalidad **pass-through**
> (retenida por el operador) hace el camino OPUESTO: netea el refund (via `DeductionKind`) y **NO**
> genera ND. **Las dos vias son mutuamente excluyentes para un mismo monto de penalidad.**

**Como se hace cumplir (las guardas que el backend debe implementar):**
1. **Disyuncion dura por concepto.** Si `CancellationConceptKind` clasifica a ND propia
   (`AgencyManagementFee` / `AgencyCancellationFee`), el backend **rechaza** cargar ese mismo monto
   como `DeductionKind.CancellationPenalty` en una allocation del operador para ese BC (y viceversa).
   Un test dedicado (§6) cubre el rechazo.
2. **El path FC1.2 sigue mandando `penalty=0` al calculator** (`:453`): la NC acredita el TOTAL y
   `AmountToRefundCustomer` queda completo. La ND no toca el calculator ni el VO. La penalidad propia
   vive **solo** en el snapshot de la ND (§3.8), fuera de la liquidacion de la NC.
3. **El monto de la ND es independiente del refund.** `DebitNoteAmount == PenaltyAmountAtEvent`, con su
   propio cuadre ARCA (`ImpTotal == ImpNeto + ImpIVA + ImpTrib`; para C, `ImpIVA=0`). No participa de
   ninguna suma que involucre el refund al cliente.

**Sobre el CHECK de suma del VO `FiscalLiquidation`:** **NO se toca.** En el modo NC-total+ND el VO
queda NULL (path FC1.2) y el CHECK no aplica; en el modo NC-parcial congelado, el CHECK sigue
protegiendo ese flujo como hoy. El invariante B1 **no depende** de ese CHECK — opera a nivel del evento
de cancelacion (concepto + refund + ND), no a nivel de la fila del VO.

**Alternativa descartada:** modelar el anti-doble-cobro como una guarda sobre el VO
(`OperatorPenaltyAmount=0`). **Descartada** porque el VO no existe en el flujo del MVP (verificado);
la guarda seria un no-op que da falsa sensacion de seguridad. El eje correcto es concepto + refund.

### 3.4 Flujo de emision (donde y cuando se dispara la ND)

**Punto de disparo:** en `BookingCancellationService.OnApprovedAsync`, en el path FC1.2 (NC total real),
**despues** de que la NC total fue encolada/emitida con exito. NO en el path de NC parcial (congelado).

**Orden NO NEGOCIABLE (NC primero, ND despues):**
1. Emitir la **NC total** (path FC1.2 existente, byte-identico).
2. **Solo si** `EnableCancellationDebitNote` ON **y** el caso clasifica a ND propia automatica
   (ver gating §3.4.1), encolar la **ND** con `IsDebitNote=true` + `OriginalInvoiceId` = factura
   original (segun `DebitNotePurpose`, §3.5).
3. La ND se emite por el **pipeline async existente** (`ProcessInvoiceJob` -> CAE), con idempotency key.

**Idempotencia (no emitir la ND dos veces):**
- Guard duro: si `BookingCancellation.DebitNoteInvoiceId` ya tiene valor (la ND ya se creo), **no se
  crea otra**. Es la misma idea que el vinculo de la NC.
- Idempotency key a nivel ARCA (reusar `InvoiceIdempotencyKey` de Fase2_M1b) derivada de
  `(BookingCancellationId, "debit-note")`, para que un reintento del job no genere dos CAE.

**Recovery ante fallo de CAE de la ND:**
- La NC total ya tiene CAE (se emitio primero). Si la ND **falla** en ARCA, NO se revierte la NC (el
  ajuste de la factura es correcto). El BC queda en un estado que refleja "NC emitida, ND pendiente/fallida":
  se reusa el patron de `AnnulmentStatus.Failed` -> log warning + notificacion + la ND queda
  reintenable (el job idempotente reintenta; si persiste, va a la bandeja de revision manual). Una ND
  fallida **NO** bloquea la cancelacion (la cancelacion ya quedo correcta con la NC).
- **Regla dura:** nunca emitir la ND **antes** que la NC. Si por algun orden raro la NC fallara, la ND
  no se dispara (el disparo esta condicionado al exito de la NC).

#### 3.4.1 Gating y ruteo a revision manual (conservador: ante la duda, NO emitir)

Va **automatico** (emite ND) SOLO si TODO esto se cumple:
- `EnableCancellationDebitNote` ON;
- `CancellationConceptKind` ∈ { `AgencyManagementFee`, `AgencyCancellationFee` } (ingreso propio gravado);
- el operador **NO** es pass-through (§3.7);
- `PenaltyStatus == Confirmed` (operador confirmo);
- **la factura original tiene `TipoComprobante` ∈ { 11, 12 } (= C)** — ver M3 abajo;
- moneda **ARS**;
- `DebitNotePurpose == PenaltyOrCancellationCharge`;
- `0 < PenaltyAmountAtEvent <= OriginalInvoiceAmount` — ver M2 abajo;
- la penalidad **NO** esta cargada ademas como `DeductionKind.CancellationPenalty` en una allocation del
  operador para este BC (INV-ADR013-001, §3.3).

Va a **revision manual** (NO emite automatico, encola approval / bandeja) en CUALQUIERA de:
- pass-through / `OperatorPenaltyPassThrough` -> de hecho **NO ND**, solo NC total (es lo de hoy);
- factura original con `TipoComprobante` **NO** ∈ { 11, 12 } (A=1/2, B=6/7, M=51/52) -> FUTURO/revision (M3);
- moneda **no-ARS** -> FUTURO (depende de ADR-012 NC/ND total);
- `RealInsurancePremium`, `AgencyCancellationCoverage`, `AgencyInsuranceCommission` (seguros) -> revision;
- `DebitNotePurpose ∈ { CorrectCreditNote, FceMiPyme }` -> revision / nunca automatico;
- `PenaltyStatus == Estimated` -> esperar confirmacion del operador, NO emitir;
- `PenaltyAmountAtEvent > OriginalInvoiceAmount` -> revision manual (M2);
- la factura tiene IIBB/percepcion discriminada -> revision.

#### 3.4.1.a (M2) — tope: penalidad no puede superar la factura original
El gating automatico **rechaza a revision manual** si `PenaltyAmountAtEvent > OriginalInvoiceAmount`.
Una penalidad mayor a lo facturado es casi seguro un error de carga (un cero de mas, moneda mezclada) o
un caso atipico que merece ojo humano. Es una guarda **barata y conservadora**: una linea en el gating,
sin nuevo schema. El tope por default es el total de la factura; si en el futuro el negocio define un
tope distinto (ej. % de la factura), se parametriza en `OperationalFinanceSettings`. **Nota:** la
penalidad propia y el total de la factura deben estar en la **misma moneda** para que el tope tenga
sentido; en el MVP ambas son ARS (moneda no-ARS ya va a manual), asi que el tope compara montos
comparables.

#### 3.4.1.b (M3) — decidir por `TipoComprobante`, NO por la condicion fiscal del emisor (defensa en profundidad)
El gating automatico decide si el caso es "C automatizable" mirando el **`TipoComprobante` de la factura
original** (`Invoice.TipoComprobante`): solo **11 / 12 (= C)** automatiza el MVP. A=1/2, B=6/7, M=51/52
-> revision manual. **NO** se infiere de la condicion fiscal del emisor (Mono/RI).

**Por que:** la version anterior dejaba que el tipo de comprobante de la ND lo derivara la condicion
fiscal del emisor (`AfipService.cs:597-607`: Mono->C). Pero la condicion fiscal **al momento de emitir
la ND** y el tipo de la **factura original** pueden quedar **desincronizados** (ej. la factura se emitio
en Mono=C y el emisor ya paso a RI: la condicion diria "A" pero la factura asociada es C). La ND debe
ser **del mismo tipo que el comprobante al que se asocia** (RG 4540: mismo receptor + comprobante
asociado). Decidir por el `TipoComprobante` real de la factura original es la fuente de verdad correcta
y evita emitir una ND A asociada a una factura C (o viceversa). El emisor RI con factura C historica
sigue siendo automatizable como ND C (lo correcto); el emisor que paso a RI y emite facturas A nuevas
cae a revision (FUTURO). Esto **reemplaza** el criterio "emisor RI -> manual" por el criterio mas
preciso "factura original no-C -> manual".

**Reusar el clasificador existente:** FC1.3 ya tiene el motor de `ReviewRequiredReason` +
approvals 4-eyes + thresholds. Se extiende con las razones nuevas (RI, no-ARS, seguro, pass-through),
NO se construye uno nuevo.

### 3.5 Asociacion (`CbtesAsoc`) + leyenda segun `DebitNotePurpose`

**Asociacion:** ya funciona via `Invoice.OriginalInvoiceId` -> `<CbtesAsoc>` (`AfipService.cs:1151-1162`,
verificado). Lo que el ADR define es **a que** se asocia segun finalidad:
- `PenaltyOrCancellationCharge` (MVP) -> `OriginalInvoiceId` = **factura original** + leyenda mencionando
  la NC.
- `CorrectCreditNote` (futuro) -> `OriginalInvoiceId` = la **NC** + revision contable.
- `FceMiPyme` -> nunca automatico.

**La leyenda NO es un agujero del MVP — es una decision fiscal pendiente (severidad bajada tras review).**
El dato fiscal **duro** de la RG 4540 (mismo receptor + **comprobante asociado**) **SI se emite hoy**:
el `<CbtesAsoc>` apuntando a la factura original se arma en `AfipService.cs:1152-1162` (verificado). O
sea: la asociacion ND<->factura, que es lo que ARCA cruza, **ya viaja en el envelope**. Lo que NO viaja
es el **texto** de leyenda ("esta ND corresponde a la cancelacion de la factura X / NC Y"): no hay hoy
un nodo de observacion libre que se mande al ARCA (`invoice.Observaciones` solo guarda la respuesta de
ARCA, no se envia). Eso es un detalle de **representacion**, no el dato fiscal duro.

**Decision para el MVP:** la leyenda se **persiste** en la ND y se **muestra en el PDF** (representacion
impresa). La inclusion del **texto** en el envelope SOAP queda como **consulta al contador +
homologacion** (§11.1), no como bloqueante tecnico del MVP: el `CbtesAsoc` ya cubre el requisito duro de
asociacion. Si el contador dictamina que el texto debe ir en el dato fiscal, se evalua el campo
`Opcionales` del WSFEv1 (a homologar) — NO se inventa un nodo sin homologar (rebotaria el comprobante).

**Persistencia del vinculo:** la ND apunta a la factura original via `Invoice.OriginalInvoiceId`; el BC
apunta a la ND via `DebitNoteInvoiceId` (§3.1). El vinculo NC<->ND<->factura queda trazable por las dos
FKs + el snapshot.

### 3.6 Homologacion ARCA

- Emitir ND C **asociada a factura** en el flujo de cancelacion requiere probar en el **ambiente de
  homologacion** de ARCA y obtener un CAE aprobado ANTES de prod. `AfipSettings.IsProduction` ya separa
  homologacion/prod con certificados distintos (verificado en ADR-012).
- Caso a homologar especificamente: ND C=12 con `CbtesAsoc` apuntando a la factura C original, con (si
  aplica §3.5) la leyenda. Confirmar que ARCA acepta el `CbteTipo`/`CbtesAsoc` y que el plazo RG 4540
  (15 dias corridos) no genera rechazo.
- **Regla operativa:** no prender el flag en prod hasta tener un CAE de homologacion aprobado para ND C
  asociada a factura.

### 3.7 Configuracion por operador — donde vive el flag pass-through

**Decision:** agregar a `Supplier` un campo nuevo para "quien se queda la penalidad", **separado** de
`InvoicingMode` (que es reseller-vs-intermediario, otro eje). Propuesta de modelo:
- enum `PenaltyOwnership` { `Agency`, `Operator` } (o un bool `OperatorRetainsPenalty`), default
  **`Operator`** (conservador = NO ND, comportamiento de hoy). El operador define, por proveedor, si la
  penalidad es ingreso de la agencia o pass-through.
- Al momento del evento, ese valor se **congela** en el snapshot de la cancelacion (como ya se congela
  `InvoicingMode` en `FiscalSnapshot.InvoicingModeAtEvent`), para que una cancelacion futura use el
  acuerdo vigente AL MOMENTO, no el actual.

**Relacion con "facturacion configurable por operador" (decision previa 2026-05-21):** este eje es
**ortogonal** a `InvoicingMode`. Un operador puede ser reseller (`TotalToCustomer`) y aun asi quedarse
o no con la penalidad. Por eso son dos campos, no uno. El override manual al confirmar la cancelacion
sigue disponible (el vendedor puede corregir el clasificador en un caso puntual, igual que el penalty
override de hoy), pero el default sale del operador.

### 3.8 Snapshot fiscal de la ND (que se congela al momento del evento)

Al confirmar la penalidad y disparar la ND, se congela (en el `FiscalSnapshot` extendido / snapshot de
la ND):
- `PenaltyAmountAtEvent` (monto de la penalidad confirmada);
- `CurrencyAtEvent` (ARS en el MVP);
- `DebitNoteCbteTipoAtEvent` (ej. 12 = ND C — derivado del **`TipoComprobante` de la factura original**
  AL MOMENTO, NO de la condicion fiscal del emisor; ver M3 §3.4.1.b);
- `OriginalInvoiceCbteTipoAtEvent` (el tipo de la factura asociada, 11/12 = C en el MVP — fuente de
  verdad para derivar el tipo de la ND, M3);
- `EmitterTaxConditionAtEvent` (Mono en el MVP — informativo/auditoria, NO se usa para derivar el tipo);
- `PenaltyOwnershipAtEvent` (Agency/Operator congelado del operador);
- `CancellationConceptKindAtEvent`;
- `ConceptClassifiedByUserId` + `ConceptClassifiedAt` (quien clasifico el concepto — ver §3.11);
- `PenaltyConfirmedByUserId` + `PenaltyConfirmedAt` (quien y cuando confirmo — exigido por R5 + audit).

**Por que congelar:** si manana el operador cambia de pass-through a ingreso propio, o Gaston pasa de
Mono a RI, las cancelaciones ya emitidas no deben recalcularse. El comprobante con CAE es inmutable;
el snapshot prueba con que reglas se emitio.

### 3.9 (M1) — BUG verificado en el mapeo de `CbteTipo` para factura C=11 (precondicion bloqueante backend)

**Verificacion M1 (leida hoy en `AfipService.cs:620-642`):** el switch de `CbteTipo` para ND **con
factura origen** (rama `request.IsDebitNote && originalInvoice != null`) mapea **desde tipos de Nota de
Credito** (`t==3->2`, `t==8->7`, `t==13->12`, `t==53->52`), y **NO contempla `t==11` (factura C)**. El
MVP asocia la ND a la **factura original C=11** (`OriginalInvoiceId` -> factura, `TipoComprobante=11`).
Con esa entrada, **ninguna rama matchea** y `cbteTipo` queda en `baseType` (`:609`), que para
Monotributo es **11 = factura C**.

**Consecuencia:** sin fix, la ND del MVP saldria con **`CbteTipo=11` (FACTURA C)** en vez de **`12`
(ND C)**. Eso es un comprobante del tipo equivocado: o ARCA lo rechaza, o (peor) se emite una factura
nueva en lugar de una nota de debito. **Es un bug real, bloqueante para el backend.**

**Fix conceptual propuesto (backend):** extender la rama "ND con factura origen" para cubrir los tipos
de **factura** ademas de los de NC. Como minimo, el caso del MVP:
```
else if (t == 11 || t == 12) cbteTipo = 12;   // factura/NC C -> ND C
```
y, por simetria y robustez, los tipos A (`t==1||t==2 -> 2`), B (`t==6||t==7 -> 7`) y M
(`t==51||t==52 -> 52`) — aunque esos caen a revision manual en el MVP (M3), conviene que el mapeo sea
correcto para cuando se habiliten. **Este cambio toca `AfipService`, que es compartido con facturacion
general** -> el backend debe cubrirlo con tests que prueben que las ramas existentes (ND desde NC
3/8/13/53) **no cambian** y que se agrega el caso factura->ND. No es solo del flujo de cancelacion.

**Por que NO es suficiente confiar en `baseType`:** `baseType` se deriva de la condicion fiscal del
emisor AL MOMENTO (`:597-607`), que puede estar desincronizada del tipo de la factura asociada (mismo
problema que M3). La ND debe derivar su tipo del **comprobante asociado**, no de la condicion fiscal.
Por eso el fix mira `t` (el `TipoComprobante` de la factura original), no `baseType`.

### 3.10 (M4) — estado observable de "ND fallida" + bandeja operativa

**El problema:** si la NC total ya salio con CAE y la **ND falla** su CAE, la cancelacion queda
fiscalmente **incompleta** (penalidad sin comprobante). Con el diseño anterior eso solo quedaba en un
log de Hangfire -> nadie se entera hasta que el contador audita.

**Decision:** agregar un estado/flag explicito y observable, **reusando patrones existentes**:
- **Estado:** `DebitNoteStatus` (enum nullable en `BookingCancellation`):
  `NotApplicable` (default, pass-through/sin ND) / `Pending` (encolada) / `Issued` (CAE OK) /
  `Failed` (CAE fallo tras reintentos) / `ManualReview` (gating la mando a revision). Espeja el patron
  de `AnnulmentStatus` que ya existe para la NC. Migracion aditiva nullable.
- **Bandeja operativa:** "**cancelaciones con NC emitida pero sin su ND**" (`DebitNoteStatus ∈
  {Pending, Failed}` con la NC ya en `Issued`). Se reusa el patron de las bandejas de reconciliacion
  de FC1.3 (la bandeja de NC parciales con recibos vivos ya existe como molde: clon de
  `ApprovalsInboxPage`, filtro mensual, permiso de review). NO se construye una bandeja desde cero.
- **Alerta:** un counter `metric:cancellation_debit_note_failed` (mismo formato que los counters del
  modulo) para que si sube en prod, alguien lo vea sin esperar la auditoria.

**Regla:** una ND `Failed` **NO** bloquea ni revierte la cancelacion (la NC ya quedo correcta); solo
la marca como "fiscalmente pendiente de completar" y la pone en la bandeja para reintento/manual. Esto
hace **observable** lo que antes era invisible.

### 3.11 (menor) — auditar QUIEN clasifico el concepto (no solo quien confirmo la penalidad)

El cambio mas sensible del flujo **NO** es confirmar el monto de la penalidad: es **clasificar el
`CancellationConceptKind`** (decidir si la penalidad es pass-through del operador o ingreso propio de la
agencia). Esa clasificacion es la que decide **si se emite ND o no**, y por lo tanto si la agencia
declara o no un ingreso. Un error ahi tiene consecuencia fiscal directa (declarar ingreso ajeno como
propio, o no declarar uno propio).

**Decision:** ademas de `PenaltyConfirmedByUserId/At`, el snapshot (§3.8) congela
`ConceptClassifiedByUserId` + `ConceptClassifiedAt`, y el cambio de `CancellationConceptKind`
(especialmente pass-through <-> ingreso propio) se registra en el **audit log** como evento propio
(quien, cuando, valor anterior -> nuevo). Reusa `_auditService.LogBusinessEventAsync` (ya usado en el
modulo). Asi el contador puede rastrear quien tomo la decision fiscalmente sensible.

---

## 4. Fases (MVP-first, conservador)

Todo detras de `EnableCancellationDebitNote` (OFF por default = comportamiento actual byte-identico:
NC total, sin ND).

### Fase MVP — ND C por penalidad propia, Mono, ARS, confirmada

Backend:
- Enums `PenaltyStatus`, `DebitNotePurpose`, `CancellationConceptKind`, `DebitNoteStatus` (M4) +
  columnas en `BookingCancellation` + FK `DebitNoteInvoiceId` (§3.1). Migracion aditiva nullable +
  defaults conservadores.
- Eje `PenaltyOwnership` en `Supplier` (default `Operator`) + congelado en snapshot (§3.7, §3.8).
- **FIX BLOQUEANTE M1 (§3.9):** cubrir `t==11||t==12 -> 12` (y A/B/M por simetria) en el switch de
  `CbteTipo` de ND con factura origen (`AfipService.cs:620-626`). Con tests de no-regresion de las
  ramas existentes (ND desde NC 3/8/13/53). **Sin esto, la ND C del MVP sale con el tipo equivocado.**
- Invariante INV-ADR013-001 (§3.3) a nivel **evento**: la penalidad propia se materializa SOLO como ND;
  el backend **rechaza** cargarla ademas como `DeductionKind.CancellationPenalty` (neteo del refund)
  para el mismo BC. NO depende del VO `FiscalLiquidation` (que en este flujo queda NULL).
- Gating §3.4.1 con M2 (tope `penalty <= OriginalInvoiceAmount`) y M3 (decidir por
  `Invoice.TipoComprobante` ∈ {11,12}, NO por condicion fiscal del emisor).
- Disparo de la ND en `OnApprovedAsync`/path FC1.2 despues de la NC total, con idempotencia
  (`DebitNoteInvoiceId` + idempotency key) y recovery (§3.4).
- Estado observable `DebitNoteStatus` (M4, §3.10) + counter + bandeja "NC sin su ND".
- Reusar pipeline ND existente (`IsDebitNote` + `OriginalInvoiceId` + `ProcessInvoiceJob`).
- Extender el clasificador `ReviewRequiredReason` con las razones nuevas (factura no-C / no-ARS /
  seguro / pass-through / estimado / penalidad > factura).
- Auditar `ConceptClassifiedByUserId/At` + log del cambio de `CancellationConceptKind` (§3.11).

Frontend (si aplica UI):
- En la pantalla de confirmacion de cancelacion: clasificar el concepto (`CancellationConceptKind`),
  marcar `PenaltyStatus` (estimada/confirmada), mostrar que la ND saldra (o que va a revision manual).
  Estados loading/empty/error/permiso.

Homologacion ARCA:
- ND C asociada a factura C (+ leyenda si §3.5 lo resuelve) -> CAE aprobado en homologacion. **Bloquea
  prender el flag en prod.**

Tests de equivalencia flag OFF (byte-identico: NC total, sin ND).

### Fase posterior — RI / IVA, multimoneda, pass-through documentado, complementarias

- **ND en RI (A/B con IVA 21%)** (R2): emisor RI -> ND gravada. Depende de homologar A/B + confirmacion
  IVA del contador.
- **ND multimoneda** (factura USD -> ND USD con TC heredado): depende de ADR-012 NC/ND total (hoy
  bloqueada por guard de moneda).
- **Penalidad estimada -> confirmada:** ND complementaria por diferencia (real>estimado) o NC
  complementaria (real<estimado) (R5).
- **Pass-through documentado:** registrar contablemente la penalidad que retiene el operador sin emitir
  ND propia (hoy va a revision/anotacion manual).
- **IIBB / percepciones, seguros reales, `CorrectCreditNote`, FCE:** cada uno su propio mini-diseño;
  hoy todos a revision manual.

---

## 5. Consecuencias, compatibilidad y rollback

### 5.1 Compatibilidad
- **Migracion 100% aditiva**: enums + columnas nullable en `BookingCancellation` + `Supplier` + snapshot
  + setting nuevo. Defaults conservadores (`Operator`, `Estimated`, `OperatorPenaltyPassThrough`) = NO ND.
  Nada se borra. Sin backfill (cancelaciones viejas = NC total sin ND, que es correcto).
- **Flag OFF = comportamiento actual**: el path FC1.2 (NC total) queda byte-identico; el disparo de la ND
  esta detras del flag. Cero cambio observable con OFF.
- **El CHECK de suma del VO `FiscalLiquidation` NO se toca** (§3.3): en el flujo del MVP ese VO queda
  NULL (path FC1.2) y el CHECK no aplica; el modo NC-parcial (congelado) queda protegido igual que hoy.
  El invariante anti-doble-cobro (INV-ADR013-001) opera a nivel **evento** (concepto + refund + ND), NO
  sobre ese CHECK.

### 5.2 Rollback
- Apagar el flag revierte el comportamiento sin migracion inversa (vuelve a NC total sin ND).
- Rollback de esquema (si hiciera falta): drop de las columnas nullable. Limpio porque son aditivas y con
  flag OFF nadie las setea. Las NDs ya emitidas con CAE existen como `Invoice` normales (no dependen de
  estas columnas para existir).
- **Atencion:** una ND ya emitida con CAE **NO se puede borrar** (es un comprobante fiscal real). El
  rollback apaga la *emision futura*, no deshace NDs emitidas. Si hubiera que anular una ND emitida, es
  via NC sobre esa ND (flujo fiscal normal), no via rollback de schema.

### 5.3 Riesgo de activacion
- **No prender el flag en prod hasta**: (a) signoff del contador matriculado sobre los puntos §11, (b)
  CAE de homologacion aprobado para ND C asociada a factura (§3.6). Mismo patron y disciplina que
  `EnablePartialCreditNoteRealEmission`.

---

## 6. Estrategia de testing

> **Entorno** (regla del proyecto): la DB Postgres vive en el **VPS remoto, no local**. Unit corren
> local (InMemory + Moq); integration con TestContainers los corre el reviewer **en el VPS**.

- **Unit (local) — MVP**:
  - Gating: concepto propio + factura C(11/12) + ARS + Confirmed + PenaltyOrCancellationCharge +
    `0 < penalty <= total` -> emite ND C.
  - Gating negativo: pass-through -> NO ND (solo NC total); Estimated -> NO ND; factura A/B/M -> revision
    (M3); no-ARS -> revision; seguro/CorrectCreditNote/FCE -> revision; `penalty > total` -> revision (M2).
  - **Anti-doble-cobro (INV-ADR013-001, §3.3) — a nivel EVENTO:** si el concepto clasifica a ND propia,
    intentar cargar la MISMA penalidad como `DeductionKind.CancellationPenalty` en una allocation del
    operador del mismo BC -> **rechazado**. Y al reves: si ya hay una deduction `CancellationPenalty`
    por ese monto, no se emite ND por el mismo concepto. La penalidad se materializa **una sola vez**.
  - **Equivalencia FC1.2:** con la ND emitida, el path FC1.2 sigue mandando `penalty=0` al calculator
    (`:453`) -> `AmountToRefundCustomer == OriginalInvoiceAmount` (la NC devuelve el total; la ND no lo
    toca). El refund al cliente NO se ve reducido por la penalidad propia.
  - **Mapeo ND C (FIX M1, §3.9):** factura origen `TipoComprobante=11` (factura C) -> ND
    `CbteTipo=12`. **Este test FALLA hoy** (sin el fix devuelve 11) — es el que prueba que el bug se
    arreglo. Mas tests de no-regresion: ND desde NC (`t=13 -> 12`, `t=3 -> 2`, etc.) sin cambios.
  - Idempotencia: con `DebitNoteInvoiceId` ya seteado, no se crea otra ND.
  - Snapshot: se congela monto/moneda/`OriginalInvoiceCbteTipoAtEvent`/concepto/quien-clasifico/quien-confirmo.
  - Estado M4: ND `Pending` -> `Issued` en CAE OK; -> `Failed` tras reintentos; `DebitNoteStatus`
    refleja cada transicion.
- **Integration (VPS)**:
  - **Flag OFF: cancelacion con penalidad -> NC total, NINGUNA ND emitida** (no-regresion byte-identica).
  - Flag ON (MVP): cancelacion con penalidad propia confirmada sobre factura C -> NC total emitida + ND
    **C (CbteTipo=12)** emitida + `CbtesAsoc` a la factura original + BC.DebitNoteInvoiceId seteado +
    `DebitNoteStatus=Issued` + audit del clasificador.
  - Recovery: ND falla CAE -> NC queda emitida, `DebitNoteStatus=Failed`, aparece en la bandeja
    "NC sin su ND", cancelacion NO se revierte.
  - Orden: nunca ND antes que NC.
- **Homologacion ARCA (manual, fuera de CI)**: ND C asociada a factura C -> CAE aprobado en homologacion
  ANTES de prod.

---

## 7. Riesgos

| # | Riesgo | Sev | Mitigacion |
|---|---|---|---|
| R1 | **Doble cobro de la penalidad propia**: la penalidad reduce el refund al cliente (via `DeductionKind.CancellationPenalty`, verificado `OperatorRefundService.cs:339,405,413`) Y ademas la ND la cobra | **Alto** | INV-ADR013-001 reescrito a nivel **evento** (§3.3): si el concepto clasifica a ND propia, el backend RECHAZA cargar la misma penalidad como deduction del refund. NO depende del VO (que en este flujo queda NULL). Test dedicado (§6) |
| R1b | **Bug CbteTipo: ND C del MVP sale como `CbteTipo=11` (factura) en vez de `12` (ND)** porque el switch no cubre `t==11` (verificado `AfipService.cs:620-626`) | **Alto** | FIX M1 §3.9: cubrir `t==11||t==12 -> 12`. Precondicion bloqueante backend. Test que hoy falla |
| R2 | Emitir ND sobre penalidad de pass-through (plata del operador) -> declarar ingreso ajeno como propio | **Alto** | Gating §3.4.1: pass-through -> NO ND. Default `PenaltyOwnership=Operator`. Eje congelado en snapshot |
| R3 | Emitir ND sobre penalidad ESTIMADA -> comprobante sobre monto no confirmado | Alto | `PenaltyStatus`: solo `Confirmed` dispara ND (R5). `Estimated` -> espera/manual |
| R4 | Emitir ND antes que la NC, o ND queda emitida con NC fallida | Medio | Orden no negociable §3.4: ND condicionada al exito de la NC; NC primero |
| R5 | ND emitida dos veces (reintento del job) | Medio | `DebitNoteInvoiceId` guard + idempotency key (Fase2_M1b) |
| R6 | ND fallida (CAE) tras NC emitida queda invisible (solo log Hangfire) -> penalidad sin comprobante sin que nadie se entere | Medio | M4 §3.10: `DebitNoteStatus=Failed` + bandeja "NC sin su ND" + counter de alerta |
| R7 | Tipo de comprobante de la ND derivado de la condicion fiscal del emisor (desincronizable con la factura asociada) | Medio | M3 §3.4.1.b + §3.9: derivar de `Invoice.TipoComprobante` de la factura original, no de la condicion fiscal |
| R8 | Penalidad cargada con un cero de mas / moneda mezclada -> ND desproporcionada | Medio | M2 §3.4.1.a: `penalty > OriginalInvoiceAmount` -> revision manual |
| R9 | Asumir que `Supplier.InvoicingMode` ya modela pass-through (no lo hace) | Medio | §3.7: eje `PenaltyOwnership` NUEVO y ortogonal. Verificado que `InvoicingMode` es otro eje |
| R10 | Plazo RG 4540 (15 dias corridos) vencido al emitir la ND | Medio | Snapshot + alerta operativa; si vencio -> revision manual (no emitir automatico tarde) |
| R11 | RI / multimoneda / FCE / seguros emitidos automaticos por error | Alto | Gating conservador §3.4.1: todos a revision manual en v1 |
| R12 | Leyenda RG 4540 (texto) no viaja en SOAP | Bajo | §3.5: el dato fiscal duro (`CbtesAsoc`) SI viaja (`AfipService.cs:1152-1162`). El texto va en PDF; su inclusion SOAP es consulta al contador, no bloqueante |
| R13 | Clasificacion de concepto pass-through<->propio errada (decision fiscalmente sensible) sin traza de quien la tomo | Medio | §3.11: auditar `ConceptClassifiedByUserId/At` + log del cambio |

---

## 8. Alternativas consideradas

1. **Netear la penalidad dentro de la factura (NC parcial, ADR-009).** Descartada por el **matriculado**
   (es el criterio opuesto). El flag `EnablePartialCreditNoteRealEmission` queda congelado.
2. **Anclar el invariante anti-doble-cobro al VO `FiscalLiquidation`** (`OperatorPenaltyAmount=0` en la
   NC, version anterior). **Descartada tras review**: ese VO NO se materializa en el flujo del MVP (solo
   se escribe dentro de `if (settings.EnablePartialCreditNotes)`, `BookingCancellationService.cs:396`;
   en el path FC1.2 puro queda NULL y el CHECK no aplica). Una guarda ahi seria un no-op. El invariante
   correcto opera a nivel **evento** (concepto + refund + ND), no sobre el VO (§3.3).
3. **Modelar la penalidad confirmada como un nuevo VO/entidad independiente del BC.** Descartada para el
   MVP: la penalidad es un atributo del evento de cancelacion (ya orquestado por `BookingCancellation`);
   columnas + snapshot extendido son mas simples (la agencia es chica). Si el negocio crece (varias NDs
   por cancelacion), se promueve a entidad despues.
4. **Inventar un nodo SOAP de leyenda sin homologar.** Descartada: haria rebotar el comprobante. La
   leyenda va por PDF + (si se confirma) `Opcionales`, post-homologacion (§3.5).
5. **Emitir la ND en el mismo job que la NC (un solo comprobante combinado).** Descartada: NC y ND son
   dos comprobantes fiscales distintos con CAE propio; el orden (NC primero) y el recovery independiente
   exigen dos emisiones separadas (§3.4).

---

## 9. Migracion / rollback

Ver §5. Resumen: aditiva, sin backfill, defaults conservadores (NO ND), reversible por flag + drop de
columnas nullable. NDs ya emitidas con CAE son inmutables (no se borran por rollback de schema).

---

## 10. Puntos que el `software-architect-reviewer` DEBERIA desafiar

1. **§3.1 — ¿columnas en `BookingCancellation` o entidad/owned type nuevo?** Propuse columnas + snapshot
   extendido por simplicidad. El reviewer deberia desafiar si el modelo aguanta el futuro (penalidad
   estimada->confirmada con ND complementaria, varias NDs por cancelacion). Si el futuro pide varias NDs,
   columnas planas se quedan cortas.
2. **§3.3 — el invariante anti-doble-cobro (REESCRITO).** Es el corazon del ADR. Ahora opera a nivel
   evento: "la penalidad propia se materializa SOLO como ND, NO reduce el refund al cliente". Se hace
   cumplir por **codigo** (el backend rechaza cargar la penalidad como `DeductionKind.CancellationPenalty`
   si el concepto es ND propia). Desafiar: ¿alcanza con la guarda en codigo o conviene un CHECK SQL
   cross-aggregate (BC + allocations)? El precedente del modulo (`INV-126`,
   `OperatorRefundService.cs:320`) es que las validaciones cross-aggregate viven en runtime, NO en CHECK
   (un CHECK acoplaria dos tablas) — este ADR sigue ese precedente, pero el reviewer deberia confirmarlo.
3. **§3.5 — la leyenda RG 4540.** ¿Es aceptable que en el MVP la leyenda viva solo en el PDF y no en el
   envelope SOAP? ¿El `CbtesAsoc` solo alcanza para la RG 4540 o el contador exige la leyenda en el dato
   fiscal? Esto puede bloquear el MVP si el contador dice que la leyenda es obligatoria en el web service.
4. **§3.4 — recovery ND fallida.** ¿Dejar la NC emitida con la ND pendiente/fallida es aceptable
   operativamente, o deberia bloquear el cierre de la cancelacion hasta que la ND tenga CAE? Trade-off:
   no bloquear (cancelacion correcta con NC) vs consistencia (penalidad sin comprobante temporalmente).
5. **§3.7 — eje pass-through en `Supplier` vs en el acuerdo por reserva.** ¿La propiedad "quien se queda
   la penalidad" es estable por operador, o varia por reserva/tarifa? Si varia por reserva, ponerla en
   `Supplier` es insuficiente (habria que poder override por cancelacion — ya previsto, pero el default
   podria estar en el nivel equivocado).
6. **§3.3 / §3.8 — extender `FiscalSnapshot` vs VO nuevo para la ND.** ADR-012 descarto reusar
   `FiscalSnapshot` para la factura directa; aca propongo extenderlo porque SI es cancelacion. Desafiar
   si extenderlo lo sobrecarga (ya carga T0/T2/T3 + multimoneda).

---

## 11. Pendiente de definicion del contador (NO bloquea construir el MVP; SI bloquea activar el flag en prod)

(Derivar al `travel-agency-accountant-argentina`.)

1. **Leyenda RG 4540:** ¿la leyenda mencionando la NC es obligatoria **en el dato fiscal** (web service)
   o alcanza con el `CbtesAsoc` + la leyenda en el PDF? (Define si §3.5 bloquea el MVP.)
2. **Plazo 15 dias corridos:** ¿desde que hecho exacto corre el plazo (confirmacion del operador)? ¿Que
   pasa fiscalmente si se emite la ND vencido el plazo? (Define R8 / gating temporal.)
3. **Asociacion para `PenaltyOrCancellationCharge`:** confirmar que la ND se asocia a la **factura
   original** (no a la NC) — el criterio dice eso, confirmar para el MVP.
4. **Concepto/fechas del envelope:** hoy `Concepto`/fechas estan hardcoded (`AfipService.cs:1196-1213`,
   deuda B2). ¿La ND por penalidad usa `Concepto=2` (servicios) y que fechas de servicio? (Hereda la
   deuda existente; confirmar que no rompe la ND.)
5. **Monotributo y tope:** ¿la ND por penalidad computa para el tope de facturacion del Monotributo de
   Gaston? (Impacto operativo, no de emision, pero relevante para el dueño.)
6. **Transicion Mono->RI:** ¿como se trata una cancelacion cuyo origen fue facturado en Mono (C) pero
   la ND se emitiria cuando ya es RI? (El snapshot congela la condicion al evento — confirmar que es lo
   correcto.)

---

## 12. Preguntas abiertas para Gaston (en criollo)

**Q1 — ¿el operador define "quien se queda la multa" siempre igual, o cambia segun la reserva?**
Ejemplo pelotudo: el operador "Despegar" ¿SIEMPRE se queda la multa, o a veces te deja una parte a vos
segun el paquete? Si es siempre igual por operador, lo configuramos una vez en la ficha del operador. Si
cambia caso por caso, lo tenes que poder elegir al cancelar cada reserva. ¿Cual de las dos?

**Q2 — ¿hay penalidades que cobres vos (la agencia) hoy, o por ahora siempre se la queda el operador?**
Esto define si el MVP (emitir la ND propia) te sirve ya o es para mas adelante. Si hoy SIEMPRE se la
queda el operador, el sistema sigue haciendo solo la NC total (lo de hoy) y la ND queda lista para
cuando empieces a cobrar multas propias.

**Q3 — la "leyenda" de la nota de debito.** El contador quiere que la ND diga algo como "esta multa
corresponde a la cancelacion de la factura X / nota de credito Y". Te aviso que hoy el sistema NO manda
ese texto al ARCA en la nota de debito (no hay donde ponerlo en el mensaje que se le manda). Opciones:
lo ponemos solo en el PDF que imprimis, o lo agregamos al mensaje de ARCA (eso hay que probarlo en el
ambiente de pruebas de ARCA primero). ¿Te alcanza con que salga en el PDF por ahora?

**Q4 — ¿ya estas como Responsable Inscripto o seguis en Monotributo?** El MVP hace la nota de debito
**C** (sin IVA discriminado), que es lo de Monotributo. Si manana pasas a RI, la nota de debito lleva
21% de IVA (es otra fase). Confirmame que hoy seguis en Monotributo asi arrancamos por ahi.

---

## 13. Fuentes

- Criterio del contador matriculado (verificado por el contador-integrado con web):
  `.claude/agent-memory/travel-agency-accountant-argentina/contador-matriculado-nc-total-mas-nd.md`
- RG 4540/2019 (ARCA/AFIP) — asociacion de NC/ND, mismo receptor, comprobante asociado, 15 dias
  corridos. **Vigencia y plazo exacto a confirmar por el contador matriculado** (el matriculado corrigio
  "15 corridos, NO habiles" en round 2).
- art. 61 Decreto Reglamentario IVA + Dictamen DAT 44/01 (tratamiento IVA del cargo de gestion en RI —
  relevante para la fase RI, NO el MVP).

> **Aviso profesional**: la validez vigente de RG 4540, el tratamiento del IVA de la ND en RI, el campo
> de leyenda en el web service y el plazo deben ser confirmados por el contador matriculado antes de
> produccion. Este ADR analiza y disena; NO es autoridad fiscal final.

---

## 14. Cierre del review — precondiciones verificadas vs pendientes

### 14.1 Verificaciones de codigo realizadas (con archivo:linea)

| # | Pregunta del review | Resultado verificado |
|---|---|---|
| **B1** | ¿El invariante apunta al objeto correcto? | **NO (corregido).** El VO `FiscalLiquidation` solo se escribe dentro de `if (settings.EnablePartialCreditNotes)` (`BookingCancellationService.cs:396`, escritura `:512-515`); en el path FC1.2 del MVP queda NULL. Invariante reescrito a nivel evento (§3.3). |
| **B2** | ¿La penalidad netea el refund al cliente HOY? | **DEPENDE del concepto.** (a) Pass-through (operador retiene): SI netea, via `DeductionKind.CancellationPenalty=3` -> `netAmount = GrossAmount - deductions` (`OperatorRefundService.cs:339`) -> `ClientCreditEntry` (`:413`) / `ReceivedRefundAmount` (`:405`). Por eso el MVP NO emite ND ahi. (b) Ingreso propio en el path FC1.2: la penalidad va al calculator como `0m` hardcodeado (`BookingCancellationService.cs:453`), la NC acredita el total -> NO reduce el refund hoy. **La ND seria plata nueva, OK, SIEMPRE que el backend NO la cargue ademas como deduction.** |
| **M1** | ¿El switch cubre factura C=11 para la ND? | **NO — es un bug.** `AfipService.cs:620-626` (ND con factura origen) mapea desde tipos de NC (3/8/13/53), no de factura. `t==11` no matchea -> `cbteTipo` queda en `baseType=11` (factura C) en vez de `12` (ND C). **Fix bloqueante backend (§3.9).** |

### 14.2 Precondiciones VERIFICADAS que el backend DEBE respetar

1. **(M1, §3.9) Arreglar el switch de `CbteTipo`** para `t==11||t==12 -> 12` (factura/NC C -> ND C),
   con tests de no-regresion de las ramas ND-desde-NC existentes. **Sin esto la ND C sale con el tipo
   equivocado.** Toca `AfipService` (compartido con facturacion general).
2. **(B1, §3.3) Disyuncion dura penalidad propia <-> deduction.** Si `CancellationConceptKind` es ND
   propia, el backend RECHAZA cargar esa misma penalidad como `DeductionKind.CancellationPenalty` en
   una allocation del operador del mismo BC (y viceversa). El invariante NO se apoya en el VO
   `FiscalLiquidation` (NULL en este flujo).
3. **(B2) El path FC1.2 sigue mandando `penalty=0` al calculator** (`:453`): la NC acredita el total y
   `AmountToRefundCustomer == OriginalInvoiceAmount`. La ND no toca el calculator ni el refund.
4. **(M3, §3.4.1.b / §3.9) Derivar el tipo de la ND del `Invoice.TipoComprobante` de la factura
   original** (11/12 = C automatiza; A/B/M -> manual), NO de la condicion fiscal del emisor.
5. **(M2, §3.4.1.a) Tope:** `penalty > OriginalInvoiceAmount` -> revision manual.
6. **(M4, §3.10) Estado observable** `DebitNoteStatus` + bandeja "NC sin su ND" + counter.
7. **(§3.11) Auditar quien clasifico el concepto** (`ConceptClassifiedByUserId/At` + log del cambio).

### 14.3 Pendiente de Gaston (negocio)

- **Q1/Q2 (§12):** ¿el "quien se queda la multa" es estable por operador o varia por reserva? ¿hoy
  cobra penalidades propias o siempre las retiene el operador? Define el default de `PenaltyOwnership`
  y si el MVP le sirve ya o queda listo para despues.
- **Q3 (§12):** ¿le alcanza con la leyenda en el PDF por ahora? (severidad baja, §3.5).
- **Q4 (§12):** confirmar Monotributo hoy.

### 14.4 Pendiente del contador matriculado (bloquea PRENDER el flag, NO construir)

- §11.1 leyenda RG 4540 en dato fiscal vs PDF; §11.2 plazo 15 dias corridos; §11.3 asociacion a factura
  original; §11.4 Concepto/fechas del envelope; §11.5 tope Monotributo; §11.6 transicion Mono->RI.
- **Homologacion ARCA:** CAE aprobado para ND C asociada a factura C, en ambiente de homologacion
  (§3.6). **Bloquea prod.**

> **Estado tras este round:** las 2 verificaciones (B2, M1) estan hechas y documentadas con archivo:
> linea; B1 reescrito a nivel evento; M2/M3/M4 + auditoria del clasificador incorporados; §1.2 y §3.5
> corregidos. **M1 es un bug real -> precondicion bloqueante backend.** El doble-cobro real (B2) NO
> esta impedido por el codigo actual (el concepto "penalidad propia" no existe en FC1.2); el backend
> debe construir la disyuncion. Queda **pendiente de re-review** del `software-architect-reviewer` y de
> respuestas de Gaston/contador.
