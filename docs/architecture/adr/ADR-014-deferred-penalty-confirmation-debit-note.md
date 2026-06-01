# ADR-014 — Confirmacion DIFERIDA de la penalidad y emision de la ND en el Dia N

- **Status**: Propuesto (Draft) — **Round 2** (post review). **Extension de
  [ADR-013](ADR-013-debit-note-on-cancellation-penalty.md)**.
  ADR-013 disenio la emision **sincrona** de la ND (en el callback del CAE de la NC, dentro de
  `OnArcaSucceededAsync`). Este ADR cubre el agujero que ADR-013 dejo abierto: emitir la ND **dias o
  semanas DESPUES** de la cancelacion, cuando el operador confirma el monto de la penalidad. **NO Accepted.**
  Round 1 recibio veredicto **Changes Required** (review en
  `.claude/agent-memory/software-architect-reviewer/adr-014-deferred-debit-note-review-round1.md`).
  Esta version (Round 2) resuelve los bloqueantes **B1 (atomicidad / estado zombi)** y **B2 (efecto en
  balance/AR de una ND sobre BC `Closed`)** y cierra los majors M1..M4. Pendiente de **re-review**, de las
  mismas confirmaciones fiscales/homologacion de ADR-013, y de implementacion.
- **Date**: 2026-06-02 (Round 1) / 2026-06-02 (Round 2, post review).
- **Author(s)**: software-architect agent.
- **Related**:
  - [ADR-013](ADR-013-debit-note-on-cancellation-penalty.md) — **base obligatoria**. De ahi salen TODOS
    los enums (`PenaltyStatus`, `CancellationConceptKind`, `DebitNotePurpose`, `PenaltyOwnership`,
    `DebitNoteStatus`), el motor de emision (`TryEmitCancellationDebitNoteAsync`), el gating
    (`EvaluateDebitNoteGating`), el snapshot (`FreezeDebitNoteSnapshot`), la captura
    (`CaptureDebitNoteClassification`), el invariante anti-doble-cobro (INV-ADR013-001), el flag
    `EnableCancellationDebitNote`, el fix de `CbteTipo` (M1) y la bandeja "NC sin su ND". Este ADR
    **reusa** ese motor; **no lo duplica**.
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md) — maquina de estados T0..T3 del BC.
  - Criterio del contador matriculado (2026-06-01):
    `.claude/agent-memory/travel-agency-accountant-argentina/contador-matriculado-nc-total-mas-nd.md`.

---

## 1. Contexto

### 1.1 El problema en criollo

ADR-013 resolvio el caso "la penalidad ya esta confirmada cuando cancelo". Pero ese **NO es el caso
dominante del negocio** (confirmado por el dueno). Lo que pasa de verdad es:

- **Dia 0** — el cliente cancela. La agencia emite la **NC total** (CAE OK). La penalidad propia de la
  agencia se conoce solo como una **estimacion** (`PenaltyStatus = Estimated`): todavia no esta el numero
  definitivo del operador. **No se emite la ND.** La reserva queda "en proceso" (no se espera el CAE en
  vivo; el frontend ya manda la cancelacion a una lista de seguimiento — decision de negocio).
- **Dia N (+10 / +30)** — el operador confirma el **monto definitivo** de la penalidad. **Recien ahi**
  corresponde emitir la ND por ese monto.

ADR-013 dispara la ND **solo** dentro de `OnArcaSucceededAsync` (el callback del CAE de la NC, que ocurre
minutos despues del Dia 0). Para ese momento la penalidad casi siempre esta `Estimated` -> el gating la
rutea a `ManualReview` (correcto: no se emite sobre estimado, R5) **y no hay forma de retomar el caso
cuando el operador confirma**. Ese es el agujero que cierra este ADR.

### 1.2 Lo que YA esta hecho (verificado en codigo — NO se reconstruye)

> Convencion: `archivo:linea` = verificado leyendo el repo en HEAD del 2026-06-02. "Supuesto" = no
> verificado, declarado explicito.

- **El motor de emision de la ND existe y es reusable tal cual.**
  `TryEmitCancellationDebitNoteAsync(bc, ct)` (`BookingCancellationService.cs:1539`) ejecuta, en orden:
  flag OFF -> return (`:1546`); idempotencia por `DebitNoteInvoiceId.HasValue` (`:1550`); fail-safe de
  Tributos contra la BD (`:1579`); gating `EvaluateDebitNoteGating` (`:1594`); disyuncion anti-doble-cobro
  leyendo las `OperatorRefundAllocations` (`:1611`); construye el `CreateInvoiceRequest`
  (`IsDebitNote=true`, `OriginalInvoiceId = factura original`, item con `AlicuotaIvaId=3`) (`:1633`);
  emite via `_invoiceService.CreateAsync` (pipeline async + CAE) (`:1659`); vincula `DebitNoteInvoiceId`
  + `DebitNoteStatus = Pending` + `FreezeDebitNoteSnapshot` (`:1681-1683`); audita; `SaveChangesAsync`
  (`:1702`). **VERIFICADO.** Este metodo es **independiente del momento**: no asume que se lo llame desde
  el callback. Lo unico que hoy lo invoca es `OnArcaSucceededAsync:1507`.
- **El gating es una funcion pura, testeable sin DB** (`EvaluateDebitNoteGating(bc, originatingInvoice)`,
  `:1913`, `internal static`). Exige, entre otras: concepto agency-owned, `PenaltyOwnership != Operator`,
  **`PenaltyStatus == Confirmed`** (`:1926`), `ConceptClassifiedByUserId != null` (`:1937`),
  `PenaltyConfirmedByUserId != null` (`:1939`), `DebitNotePurpose == PenaltyOrCancellationCharge`,
  factura C (11/12), ARS, sin Tributos, `0 < penalty <= total`. **VERIFICADO.** Esto significa que **el
  mismo gating ya cubre el caso diferido**: si en el Dia N seteamos `PenaltyStatus = Confirmed` + el monto
  + la auditoria, el gating deja pasar la emision sin tocar una linea.
- **El snapshot, la idempotencia, la bandeja, la reconciliacion y el invariante anti-doble-cobro ya
  existen** (ver §1.2 de ADR-013 + `GetCancellationsWithMissingDebitNoteAsync:1104`, guarda simetrica en
  `OperatorRefundService.cs:354-365`). **VERIFICADO.**
- **El modelo de datos de la clasificacion ya esta** en `BookingCancellation` (`BookingCancellation.cs:294-407`):
  `PenaltyStatus`, `ConceptKind`, `DebitNotePurpose`, `DebitNoteInvoiceId`, `DebitNoteStatus`,
  `PenaltyAmountAtEvent`, los `*ByUserName`, etc. **VERIFICADO.** El BC tiene `UseXminAsConcurrencyToken`
  (`BookingCancellation.cs:18-20`). **VERIFICADO.**
- **La guarda anti-reclasificacion ya existe** (`EnsureConceptNotLockedByDebitNote:1890`): rechaza CAMBIAR
  el concepto si la ND ya esta en juego (`DebitNoteInvoiceId.HasValue` o `Pending`/`Issued`). **VERIFICADO.**
- **El permiso `cancellations.classify_agency_penalty` existe** (`Permissions.cs:123`) y el patron de
  resolverlo server-side esta en el controller (`CancellationsController.cs:145-147`). **VERIFICADO.**

### 1.3 Lo que NO esta hecho (lo que ataca este ADR)

1. **No hay forma de transicionar `PenaltyStatus: Estimated -> Confirmed` despues del Dia 0.** La unica
   captura de clasificacion ocurre en `ConfirmAsync` (Dia 0) via `CaptureDebitNoteClassification:1754`. Si
   la penalidad quedo `Estimated`, no existe endpoint ni transicion para confirmarla mas tarde.
   **VERIFICADO (ausencia):** los unicos verbos del controller son Draft, Confirm, Abort,
   ForceArcaConfirmation, GetByPublicId, debit-notes/pending, edit-liquidation
   (`CancellationsController.cs`). Ninguno confirma una penalidad diferida.
2. **El unico disparador de la ND es el callback del CAE de la NC.** `TryEmit...` solo se invoca desde
   `OnArcaSucceededAsync:1507`, que corre una sola vez (cuando la NC obtiene CAE). No hay re-disparo
   posterior. **VERIFICADO.**
3. **No se persiste la fecha en que el operador confirmo** (eje fiscal del plazo RG 4540 — §6 fiscal).
   El snapshot tiene `PenaltyConfirmedAt` (`BookingCancellation.cs:389`) que hoy se setea con
   `DateTime.UtcNow` del acto en el sistema, **no** con la fecha real en que el operador confirmo.
   **VERIFICADO.** Para el diferido necesitamos distinguir las dos fechas (§3.3, regla fiscal 1 y 5).
4. **No hay soporte documental del acuerdo del operador** (mail/PDF de confirmacion). Hoy no se persiste.
   **VERIFICADO (ausencia).**

---

## 2. Que entra y que NO entra

### 2.1 Entra (todo detras del flag `EnableCancellationDebitNote`, OFF = byte-identico)

1. **Un endpoint nuevo de confirmacion diferida de la penalidad** (PATCH), que en el Dia N:
   clasifica/confirma el concepto + monto + fecha de confirmacion del operador, y **dispara la ND
   reusando el motor existente** (`TryEmitCancellationDebitNoteAsync`).
2. **Modelo de datos minimo**: `OperatorPenaltyConfirmedDate` (la fecha REAL en que el operador confirmo,
   distinta de la fecha de emision) + soporte documental opcional. Migracion aditiva.
3. **Reuso del gating, el snapshot, la idempotencia, la bandeja y el invariante anti-doble-cobro de
   ADR-013.** Sin duplicar.
4. **Una alerta conservadora de plazo** (15 dias corridos desde la confirmacion del operador), NO
   bloqueante, configurable (§3.5).

### 2.2 NO entra (scope cut explicito)

- **Todo lo que ADR-013 §2.2 ya recorto** (RI/IVA, multimoneda, FCE, seguros, IIBB, pass-through
  automatico, NC parcial) sigue afuera y va a revision manual por el **mismo gating** (no se relaja).
- **NO ND complementaria por diferencia estimado vs real** (R5 de ADR-013): en este flujo la ND sale
  **directo por el monto confirmado** porque **nunca se emitio nada sobre el estimado** (regla fiscal 4).
  No hay comprobante previo que corregir. Si en el futuro se emitiera ND sobre estimado, ahi haria falta
  la complementaria — fuera de alcance.
- **NO se modela la "deuda devengada del Monotributo".** Solo persistimos y exponemos las dos fechas
  (cancelacion y confirmacion del operador) para que cada eje fiscal use la suya (regla fiscal 5); el
  computo del limite del Monotributo es operativo/humano, fuera del alcance tecnico.
- **NO backfill.** Cancelaciones existentes con la penalidad `Estimated` quedan disponibles para
  confirmar via el endpoint nuevo (no necesitan migracion de datos); las historicas sin clasificar quedan
  en sus defaults (pass-through, NO ND).

---

## 3. Decision

### 3.1 El nuevo endpoint / transicion

**Endpoint**: `PATCH /api/cancellations/{publicId:guid}/confirm-penalty`

**Por que PATCH y por que en `CancellationsController`**: es una mutacion parcial del BC existente (no
crea un recurso) que actualiza la clasificacion/confirmacion de la penalidad. Coherente con `confirm`,
`abort`, `force-arca-confirmation` que ya son PATCH/POST sobre el BC. Controller thin, igual que el resto.

**Request DTO** (`ConfirmPenaltyRequest`):

| Campo | Tipo | Obligatorio | Para que |
|---|---|---|---|
| `ConceptKind` | `CancellationConceptKind?` | no (default por operador) | clasificar ingreso propio vs pass-through; el frontend ofrece 2 opciones explicitas: `AgencyManagementFee` (cargo de gestion) vs `AgencyCancellationFee` (cargo de cancelacion) — decision de negocio |
| `ConfirmedPenaltyAmount` | `decimal` (Range 0.01..) | **si** | el monto definitivo del operador |
| `OperatorConfirmationDate` | `DateTime` | **si** | la fecha REAL en que el operador confirmo (eje fiscal del plazo + devengamiento) |
| `DebitNotePurpose` | `DebitNotePurpose?` | no (default `PenaltyOrCancellationCharge`) | finalidad (MVP solo automatiza ese valor) |
| `SupportingDocumentReference` | `string?` (MaxLength 500) | no | referencia/URL del soporte documental del acuerdo (mail/PDF). Opcional, auditoria |
| `OverrideReason` / `ApprovalRequestPublicId` | igual que `Confirm` | condicional | para el patron de aprobacion 4-eyes (§3.6) |

**Precondiciones que valida el service** (en orden, fail-fast):
1. **Flag** `EnableCancellationDebitNote` ON. Si OFF -> 409 `InvalidOperationException` (no existe el
   concepto de ND con el flag apagado; mismo criterio que el resto del flujo nuevo). **Byte-identidad:**
   con el flag OFF el endpoint es inerte (rechaza), no muta nada.
2. **BC existe** (404 si no).
3. **Permiso** `cancellations.classify_agency_penalty` resuelto server-side (Admin o permiso). Sin el ->
   403 (igual que el path sincrono de `Confirm`). Esta es **la** decision fiscalmente sensible: confirmar
   una penalidad propia **dispara un comprobante fiscal real**.
4. **Estado del BC post-NC con CAE**: `Status` ∈ { `AwaitingOperatorRefund`, `ClientCreditApplied`,
   `Closed`, `AbandonedByOperator` } **Y** `CreditNoteInvoiceId != null`. La regla dura es **la NC ya
   tiene CAE** (`CreditNoteInvoiceId` se setea recien en `OnArcaSucceededAsync:1470`). Rechazo (409
   `INV-ADR014-001`) si el BC esta en `Drafted` / `AwaitingFiscalConfirmation` / `ArcaRejected` /
   `Aborted` / estados de manual review NC parcial. **Justificacion**: nunca emitir la ND antes que la NC
   (regla dura heredada de ADR-013 §3.4); si la NC no tiene CAE, este endpoint no procede (para el caso
   sincrono ya esta `Confirm`).
5. **Concepto agency-owned** (despues de aplicar el default por operador): si el caso es pass-through ->
   no hay ND para emitir -> 409 `INV-ADR014-002` ("esta penalidad es del operador, no emite ND"). El
   endpoint es **solo** para penalidades propias confirmadas.
6. **Pre-check de idempotencia (B1, §3.4 pieza 1).** Rechaza con 409 idempotente (`INV-ADR014-003`, "la
   penalidad ya fue confirmada / la ND ya esta en juego") si se cumple **cualquiera** de:
   `PenaltyStatus == Confirmed` **O** `DebitNoteInvoiceId.HasValue` **O** `DebitNoteStatus ∈ { Pending,
   Issued }`. Para **proceder**, exige: `PenaltyStatus == Estimated` **Y** `DebitNoteInvoiceId == null`
   **Y** `DebitNoteStatus ∈ { NotApplicable, ManualReview, Failed }`. La condicion sobre `PenaltyStatus`
   es la que cierra la ventana de doble emision tras un crash entre crear-la-ND y vincularla (§3.4 pieza 2:
   la marca `Confirmed` se persiste ANTES de crear la ND). Reusa ademas el espiritu de
   `EnsureConceptNotLockedByDebitNote:1890`.
   - **Excepcion controlada `Failed` / `ManualReview` con `DebitNoteInvoiceId == null`:** si una corrida
     previa dejo `PenaltyStatus == Confirmed` pero la ND rebanoto a `ManualReview` (gating) o fallo el CAE
     (`Failed`) **sin** vincular ND, el re-disparo **NO** es por este endpoint (que rebota por
     `Confirmed`), sino por el re-trigger de la bandeja (§3.8). Esto evita que el operador genere una
     segunda ND reintentando el endpoint.
7. **`OperatorConfirmationDate`**: no futura; no anterior a la fecha de la cancelacion
   (`ConfirmedWithClientAt`). 400 si viola.

**Transicion de estado del BC**: **NINGUNA del enum `BookingCancellationStatus`.** El BC permanece en su
estado operativo (`AwaitingOperatorRefund` / etc.) porque la confirmacion de la penalidad **no cambia el
ciclo T0..T3 del refund**: es ortogonal. Lo que transiciona es el **`DebitNoteStatus`**
(`NotApplicable`/`ManualReview` -> `Pending` -> `Issued`/`Failed`) y el **`PenaltyStatus`**
(`Estimated` -> `Confirmed`). Esto es deliberado y a desafiar (§10.1): meter un estado nuevo en la
maquina T0..T3 acoplaria dos ejes que el negocio mantiene separados (el refund del operador vs el cobro
de la penalidad propia).

### 3.2 Como dispara la emision (reuso del motor, sin duplicar)

El service, tras pasar las precondiciones, hace **exactamente** lo que ya hace `ConfirmAsync` para la
clasificacion, pero en el Dia N y sobre un BC post-NC:

1. **Aplica la clasificacion** llamando a `CaptureDebitNoteClassification(bc, request, userId, userName,
   userCanClassifyAgencyPenalty, debitNoteFeatureEnabled: true)` — el **mismo metodo** que usa el path
   sincrono (`:1754`). Setea `ConceptKind`, `PenaltyStatus = Confirmed`, `DebitNotePurpose`,
   `PenaltyAmountAtEvent`, y la auditoria del clasificador/confirmador. Ya enforza el permiso elevado y la
   anti-reclasificacion. **NOTA de implementacion**: hoy `CaptureDebitNoteClassification` toma los campos
   desde `ConfirmCancellationRequest`. Hay que adaptar la **forma de entrada** (M1, §10 punto 3): NO cambiar la
   firma a polimorfica, sino **extraer un `record` de clasificacion comun** (`PenaltyClassificationInput`)
   que ambos requests construyan; `CaptureDebitNoteClassification` pasa a consumir ese record. Refactor de
   shape, no de logica — con tests de no-regresion de los 4 caminos de permiso del sincrono (§10 punto 3).
2. **Setea la fecha real de confirmacion del operador**: `bc.OperatorPenaltyConfirmedDate =
   request.OperatorConfirmationDate` y `bc.SupportingDocumentReference = request.SupportingDocumentReference`.
3. **Persiste la clasificacion en su propio `SaveChanges` (commit (c) de §3.4, pieza 2).** Esta es la
   **marca de no-retorno** de exactly-once: deja `PenaltyStatus = Confirmed` durable ANTES de crear la ND.
   Si este commit choca por `xmin`, **no se creo ninguna ND todavia** y el 409 es seguro de reintentar
   (§3.4). Esto difiere de Round 1, donde la clasificacion y la emision compartian un solo `SaveChanges` y
   un crash dejaba la ND huerfana sin marca.
4. **Invoca el motor existente**: `await TryEmitCancellationDebitNoteAsync(bc, ct)` (`:1539`). Ese metodo
   ya hace el gating, la idempotencia (`DebitNoteInvoiceId.HasValue` `:1550`), la disyuncion
   anti-doble-cobro **re-chequeada en runtime con query fresca** (`:1611`, NO snapshot viejo —
   **VERIFICADO**, ver §3.7 y major anti-doble-cobro diferido), la emision async, el snapshot y su propio
   `SaveChanges` (`:1702`, que vincula `DebitNoteInvoiceId`). **No se duplica nada.**
5. **Resultado**: `TryEmit...` deja `DebitNoteStatus = Pending` (encolada) o `ManualReview` (gating la
   rebota) — exactamente igual que en el path sincrono. El CAE real lo procesa el pipeline async
   (`ProcessInvoiceJob`); la reconciliacion **normal** `Pending -> Issued/Failed` la hace
   `GetCancellationsWithMissingDebitNoteAsync:1104` (la bandeja) sin cambios en ese flujo. La ND fallida
   cae en la misma bandeja "NC sin su ND". (La **unica** extension de la bandeja es para el caso de ND
   huerfana por crash entre T1 y T2 — §3.8 pieza 3; no cambia la reconciliacion `Pending->Issued`.)

**Punto critico fiscal verificado (regla fiscal 3 — CbtesAsoc + fecha):**
- El `<CbtesAsoc>` apunta a la **factura original** (no a la NC) porque `TryEmit...` construye el request
  con `OriginalInvoiceId = originatingInvoice.PublicId` (`:1637`) y el envelope SOAP arma el CbtesAsoc
  desde ahi (`AfipService.cs:1151-1162`, verificado en ADR-013). **Esto aplica IGUAL en el camino
  diferido** porque el motor es el mismo y la factura original no cambia con el tiempo. **VERIFICADO** por
  reuso del mismo codigo.
- La **fecha de la ND = fecha real de emision (Dia N)** (regla fiscal 1): el pipeline de emision usa la
  fecha del momento de emision para el comprobante (no la del evento de cancelacion). Como la ND se crea
  en el Dia N via `CreateAsync`, su fecha de comprobante es la del Dia N. **Supuesto a confirmar en
  homologacion** (no traze que campo de fecha usa exactamente el envelope; ADR-013 §11.4 ya marca que
  `Concepto`/fechas estan hardcoded en `AfipService.cs:1196-1213` — esa deuda se hereda y debe
  confirmarse que la ND diferida fecha correctamente al Dia N, no al Dia 0).

### 3.3 Modelo de datos (aditivo, nullable)

Dos columnas nuevas en `BookingCancellation` (migracion 100% aditiva, defaults nullable, Postgres):

| Campo | Tipo | Para que |
|---|---|---|
| `OperatorPenaltyConfirmedDate` | `DateTime?` | la fecha REAL en que el operador confirmo (eje fiscal del plazo RG 4540 + devengamiento). **Distinta** de `PenaltyConfirmedAt` (que es el timestamp del acto en el sistema). Regla fiscal 1 + 5: cada eje fiscal usa la suya |
| `SupportingDocumentReference` | `string?` (MaxLength 500) | referencia/URL del soporte documental del acuerdo (mail/PDF del operador). Opcional, auditoria |

**Por que dos fechas (no reusar `PenaltyConfirmedAt`):** `PenaltyConfirmedAt` (`:389`) es "cuando un
usuario apreto Confirmar en el sistema" (auditoria de quien/cuando opero). `OperatorPenaltyConfirmedDate`
es "cuando el operador comunico el monto" (puede ser un dia anterior, lo informa el usuario). El plazo de
15 dias corridos (RG 4540) corre desde **esta ultima** (regla fiscal 2). Mezclarlas perderia el eje
fiscal correcto.

**No se agrega estado nuevo al enum** (§3.1). `DebitNoteStatus` y `PenaltyStatus` ya cubren la
observabilidad del diferido.

**Nada toca el VO `FiscalLiquidation`** — confirmado abajo (§3.7).

### 3.4 Idempotencia, atomicidad y exactly-once — RESOLUCION DE B1 (Round 2)

> **El problema real (B1, verificado en codigo):** el motor `TryEmitCancellationDebitNoteAsync` invoca
> `_invoiceService.CreateAsync` (`:1659`), que internamente:
> 1. **commitea** la ND PENDING en su propio `SaveChanges` (`AfipService.CreatePendingInvoice`,
>    invocada en `InvoiceService.cs:396`), y
> 2. **encola el job Hangfire** `ProcessInvoiceJob` (`InvoiceService.cs:413`),
>
> **ANTES** de retornar al motor. Recien DESPUES de ese return, el motor vincula
> `bc.DebitNoteInvoiceId` y hace su propio `SaveChanges` (`BookingCancellationService.cs:1702`).
> **VERIFICADO.** Hay por lo tanto **dos transacciones separadas**: T1 = crear la ND PENDING (commit
> dentro de `CreateAsync`); T2 = vincular el BC (commit en `:1702`). Entre T1 y T2 existe una **ventana de
> estado zombi**: la ND ya existe (y el job ya esta encolado), pero el BC todavia tiene
> `DebitNoteInvoiceId == null`. En el path **sincrono** de ADR-013 esto vive dentro de un callback
> automatico (sin humano que reintente). En el path **diferido** lo dispara una **persona** via HTTP: si
> T2 choca por `xmin` (otro proceso toco el BC entre el read y el write) y el controller responde 409, el
> usuario reintenta, el pre-check vuelve a ver `DebitNoteInvoiceId == null`, y se **emite una SEGUNDA ND**.
> Esa es la falla de exactly-once que B1 exige cerrar.

**Red de seguridad existente (verificada, NO suficiente por si sola):** `CreateAsync` tiene el guard
`UX_Invoices_OnePendingPerReserva` — un **unique index parcial** sobre la reserva con filtro
`Resultado='PENDING' AND AnnulmentStatus != Succeeded` (`InvoiceService.cs:387-405`, comentario B1.15).
Esto bloquea una **segunda** invoice PENDING para la misma reserva mientras la primera siga en vuelo.
Cubre la ventana en que la primera ND **todavia no obtuvo CAE**. **Pero NO cubre** el caso peligroso del
diferido: si la primera ND **ya obtuvo CAE** (`Resultado='A'`, ya no es PENDING) pero el link al BC no se
persistio (crash en T2, o T2 perdio el `xmin`), el reintento **pasa** el unique index y emite una segunda
ND con CAE real. Por eso el unique index es defensa en profundidad, no la solucion.

**Decision (B1): endpoint idempotente con clave de confirmacion + pre-check transaccional + manejo
explicito del 409, sin reordenar el motor.** Tres piezas, en orden de prioridad:

1. **Pre-check de idempotencia ANTES de tocar el motor, dentro de la misma carga del BC (precondicion 6,
   §3.1).** El endpoint carga el BC y rechaza con **409 `INV-ADR014-003`** si
   `DebitNoteInvoiceId.HasValue` **O** `DebitNoteStatus ∈ { Pending, Issued }` **O** `PenaltyStatus ==
   Confirmed`. Las tres condiciones cubren: ND ya vinculada (T2 ya corrio), ND en vuelo o emitida, o
   penalidad ya confirmada por una corrida anterior. Esto convierte un reintento del **happy path** (la
   primera corrida funciono) en un 409 idempotente claro, no en una segunda ND.

2. **Clave de idempotencia explicita de la CONFIRMACION (no de la ND).** El problema del pre-check solo es
   la ventana T1->T2: si T1 commiteo la ND pero T2 fallo, el pre-check del reintento ve
   `DebitNoteInvoiceId == null` y deja pasar. Para cerrar ESA ventana sin depender de reordenar
   `CreateAsync`, el endpoint usa **el propio `PenaltyStatus` como marca persistida ANTES de llamar al
   motor**: el flujo es
   **(a)** validar precondiciones; **(b)** aplicar `CaptureDebitNoteClassification` que setea
   `PenaltyStatus = Confirmed` + auditoria; **(c)** `SaveChanges` (commit de la clasificacion, con `xmin`);
   **(d)** recien entonces llamar a `TryEmitCancellationDebitNoteAsync`. Asi, si el motor (T1) crea la ND
   pero el link (T2) falla, **`PenaltyStatus` ya quedo `Confirmed` en (c)**, y un reintento choca con la
   condicion `PenaltyStatus == Confirmed` del pre-check (pieza 1) -> 409, **no** segunda ND.
   - **Trade-off honesto:** esto introduce un commit adicional (c) y un orden estricto. Es deliberado:
     mover el punto de no-retorno (la marca `Confirmed`) **antes** de la creacion de la ND, de modo que la
     marca exista aunque la ND quede huerfana. El precio es que, si el motor rebota a `ManualReview`
     (gating) DESPUES de (c), el `PenaltyStatus` queda `Confirmed` con `DebitNoteStatus = ManualReview`
     — lo cual es **correcto** (la penalidad esta confirmada, la ND requiere intervencion). Para
     re-disparar tras corregir el motivo del manual, se usa la bandeja / un re-trigger explicito, **no** un
     segundo `confirm-penalty` (que ya rebota por `PenaltyStatus == Confirmed`).

3. **ND huerfana (T1 commiteo, T2 nunca corrio por crash): reconciliacion, no reintento ciego.** Si el
   proceso muere entre T1 y T2, queda una ND PENDING/Issued sin `DebitNoteInvoiceId` en el BC. Como
   `PenaltyStatus` ya es `Confirmed` (pieza 2.c), el operador **no puede** re-emitir. La ND huerfana se
   detecta y reconcilia por **`ReservaId` + es-ND + asociada a la factura original del BC** (la bandeja de
   §3.8 se extiende para detectar "BC con `PenaltyStatus=Confirmed` y `DebitNoteInvoiceId=null` pero con
   una ND existente para esa factura original" -> re-vincular, no re-emitir). Esto es **trabajo nuevo del
   ADR** (la bandeja actual `GetCancellationsWithMissingDebitNoteAsync:1104` filtra
   `CreditNoteInvoiceId != null && DebitNoteStatus in (Pending,Failed)` y **no** ve un BC con
   `DebitNoteInvoiceId == null`; **VERIFICADO**). Ver §3.8.

**Manejo explicito del 409 `xmin` (cerrando lo que B1 marca como gatillo del doble):** el controller
devuelve **409 `CONCURRENT_EDIT`** ante `DbUpdateConcurrencyException` (mismo contrato que
`EditLiquidation`, `CancellationsController.cs:379-390`, **VERIFICADO**). La diferencia con Round 1 es que
**ahora el 409 es seguro de reintentar**: gracias a la pieza 2 (la marca `Confirmed` se persiste en su
propio commit (c) ANTES de crear la ND), un reintento tras un 409 `xmin` cae siempre en una de dos
situaciones deterministas:
- el commit (c) **sucedio** -> el reintento ve `PenaltyStatus == Confirmed` -> 409 idempotente
  (`INV-ADR014-003`), no segunda ND; o
- el commit (c) **no sucedio** (el `xmin` choco en (c) mismo, antes de llamar al motor) -> **no se creo
  ninguna ND** -> el reintento procede limpio.

El caso imposible-de-distinguir de Round 1 (T1 commiteo la ND, T2 perdio el `xmin`) **deja de existir**
porque la marca de no-retorno se movio antes de T1.

**Sobre idempotency key a nivel ARCA (CORRECCION Round 2 — NO existe para este path):** el review de
Round 2 verifico en codigo que el path de la ND (`TryEmit -> CreateAsync -> ProcessInvoiceJob`) **NO
escribe ninguna idempotency key a nivel CAE**: `grep ArcaIdempotencyKey` en `AfipService.cs` da 0
matches, y la tabla `ArcaIdempotencyKeys` (Fase2_M1b) la usa **solo** `ProcessPartialCreditNoteJob` (el
path de NC parcial), no el de ND. `ProcessInvoiceJob` solo tiene el short-circuit `Resultado=="A"`
(`AfipService.cs:1053`), que protege un re-run del **mismo** `invoiceId` por Hangfire, no dos invoices
distintas. Por lo tanto, **la garantia exactly-once de este flujo NO se apoya en ninguna red a nivel
ARCA**, sino enteramente en las tres piezas verificadas: (1) marca `PenaltyStatus=Confirmed` persistida
en commit propio ANTES de crear la ND, (2) pre-check que rebota el reintento, (3) unique index parcial
`UX_Invoices_OnePendingPerReserva` (`InvoiceService.cs:340-405`, traduce el 23505 a 409). Esas tres
alcanzan, porque el escenario peligroso son dos `CreateAsync` distintos y esa key — aun si existiera —
nunca lo cubriria. **Decision MVP:** no se agrega idempotency key ARCA para la ND (tocaria el pipeline
de facturacion compartido que este ADR decidio NO tocar). Si en el futuro se quisiera defensa en
profundidad a nivel CAE, seria **trabajo nuevo** (escribir una `ArcaIdempotencyKey` en `ProcessInvoiceJob`
antes del POST, para el caso ND), no una red existente.

**Por que NO reordenar `CreateAsync` para vincular antes:** se evaluo mover el seteo de
`bc.DebitNoteInvoiceId` **dentro** de la transaccion que crea la ND PENDING (un solo `SaveChanges`). Se
descarta para el MVP: `CreateAsync` es el pipeline de facturacion compartido por TODAS las facturas/NC/ND
del sistema; meterle conocimiento del BC acoplaria el aggregate de facturacion al de cancelacion y
arriesgaria regresiones en todos los caminos de emision. La solucion elegida (marca persistida antes de
crear la ND) logra exactly-once **sin** tocar `CreateAsync`. Si en una fase futura se quiere atomicidad
estricta T1+T2 en una sola transaccion, seria un refactor del pipeline de facturacion, fuera de alcance
aqui.

### 3.4-bis Efecto en el balance / cuenta corriente del cliente — RESOLUCION DE B2 (Round 2)

> **Pregunta de B2:** la precondicion 4 (§3.1) admite emitir la ND diferida sobre un BC cuya reserva
> puede estar `Closed` (T3). La ND es un cargo NUEVO a favor de la agencia (plata que el cliente debe).
> ¿Genera saldo a cobrar / reabre la reserva / es solo comprobante? **Gaston delego la decision al
> architect** ("que lo proponga el architect"). Hay que elegir la opcion mas coherente con el modelo de
> balance/AR REAL del sistema.

**Hecho verificado en codigo (decisivo):** el balance de la reserva NO deriva de los comprobantes
fiscales. `Reserva.Balance` es un campo persistido que se recalcula en `ReservaService.UpdateBalanceAsync`
(`ReservaService.cs:2158-2199`) y en `PaymentService` (`:834`) como:

```
Balance = TotalSale - TotalPaid
```

donde `TotalSale` = suma del `SalePrice` de las **5 colecciones de servicios** (FlightSegments,
HotelBookings, TransferBookings, PackageBookings, AssistanceBookings) + Servicios genericos, filtradas por
`WorkflowStatusHelper.CountsForReservaBalance(...)`; y `TotalPaid` = suma de los Payments no anulados
(`ReservaService.cs:2175-2196`, **VERIFICADO**). **Las facturas, NC y ND NO participan de `Balance`**: no
hay ningun termino de invoice en la formula. Una penalidad de cancelacion **no es un servicio** de ninguna
de esas colecciones. Por lo tanto, **emitir una ND hoy tiene efecto CERO sobre `Reserva.Balance`** por
construccion del modelo actual. Lo mismo aplica a la NC total que ya se emite en el Dia 0: tampoco toca el
balance.

**Opciones evaluadas:**
- **(a) Generar saldo a cobrar / cuenta corriente del cliente.** Requiere **inventar un nuevo termino** en
  la formula del balance (o un sub-ledger de cuenta corriente del cliente que hoy **no existe** como tal:
  el "saldo" del cliente ES `TotalSale - TotalPaid` de la reserva, no un AR contable separado). Es un
  cambio de modelo de datos grande, transversal a cobranzas/tesoreria/alertas, y **acoplaria el eje fiscal
  (ND) con el eje de saldo comercial (servicios vs pagos)** que el sistema mantiene separados. Riesgo alto,
  fuera del MVP.
- **(b) Reabrir la reserva (Closed -> estado previo).** Descartada: la NC del Dia 0 ya cerro el ciclo;
  reabrir la maquina de estados de la reserva por un cargo fiscal posterior reintroduce los gates de
  cobranza/voucher/facturacion sobre una reserva que el negocio considera cerrada, y colisiona con el
  rediseno de estados (`EnableSoldToSettleStates`, ver mas abajo). Efecto colateral amplio.
- **(c) Solo comprobante fiscal; NO toca `Reserva.Balance` ni reabre la reserva.** **ELEGIDA.** Es la
  unica opcion **coherente con el modelo actual**: el balance ya ignora todos los comprobantes (factura,
  NC, ND); la ND diferida se comporta igual que la NC total del Dia 0 a efectos de balance (neutra). La ND
  es un **comprobante fiscal** (cargo declarado ante ARCA), no un movimiento del saldo comercial de la
  reserva. El cobro real de la penalidad al cliente (si lo hubiera) es un acto de cobranza separado, que
  hoy se registra como Payment/movimiento de tesoreria, **independiente** de la emision del comprobante.

**Decision (c): la ND diferida es solo comprobante fiscal. NO modifica `Reserva.Balance`, NO reabre la
reserva, NO cambia el estado del BC ni de la reserva.** El BC puede estar `Closed` y la ND se emite igual;
la reserva permanece `Closed`. Esto es consistente con que el motor `TryEmit...` ya **no toca** el balance
hoy (no hay ninguna llamada a `UpdateBalanceAsync` en `TryEmitCancellationDebitNoteAsync`, **VERIFICADO**).

> **⚠️ REQUIERE VALIDACION DE GASTON (negocio).** La opcion (c) asume que **el cobro de la penalidad al
> cliente se gestiona por fuera del balance de la reserva** (como un Payment / movimiento de tesoreria
> manual, no como un saldo automatico). Esto es lo coherente con el modelo de datos actual, pero es una
> **decision de negocio**: si Gaston espera que, al emitir la ND, la reserva "vuelva a deber" ese monto
> automaticamente (cuenta corriente), entonces hace falta la opcion (a) — que es una pieza de modelo
> aparte, fuera de este ADR, y deberia diseñarse junto con el rediseno de estados de reserva. **Pregunta
> concreta a Gaston:** "Cuando emitis la ND por la penalidad, ¿queres que el sistema te muestre ese monto
> como saldo a cobrar de la reserva (que la reserva vuelva a tener deuda), o alcanza con el comprobante
> fiscal y vos cobras/registras el pago aparte?" Si la respuesta es "que vuelva a deber", este ADR se
> amplia (opcion a) en una fase posterior; si es "alcanza el comprobante", (c) queda firme.

**Conexion con el rediseno de estados de reserva (`EnableSoldToSettleStates`, en curso).** El rediseno
introduce los estados `Sold`/`ToSettle`/`Closed` (ver memoria del proyecto). La opcion (c) es **neutral** a
ese rediseno: como la ND no toca balance ni estado, no interfiere con la nueva maquina. Pero hay un punto a
vigilar: el guard fiscal de `InvoiceService.cs:318` bloquea **facturar** una reserva `Sold` cuando el flag
nuevo esta ON. Ese guard mira `IsCreditNote == false && IsDebitNote == false` (`InvoiceService.cs:352`,
**VERIFICADO**): la rama del guard de deuda NO aplica a NC ni ND. **La ND diferida NO queda bloqueada por
ese guard.** Aun asi, la precondicion 4 (§3.1) exige que la NC del Dia 0 **ya tenga CAE** (el BC esta
post-NC), lo cual implica que la reserva ya transito por la cancelacion — un estado `Sold` "puro" (vendida,
nunca confirmada) no llega a tener una NC con CAE asociada. **Si el rediseno de estados cambiara esa
relacion, re-evaluar.** Marcado como supuesto a revisar cuando se prenda `EnableSoldToSettleStates`.

### 3.5 Plazo (alerta conservadora, NO bloqueante)

- Al confirmar, si `(DateTime.UtcNow.Date - OperatorConfirmationDate.Date).TotalDays > N` (default
  **15 dias corridos**, configurable en `OperationalFinanceSettings` — ej.
  `CancellationDebitNoteGraceDays`), se **emite igual** pero se loguea un warning + counter
  `metric:cancellation_debit_note_late` para que el back-office/contador lo vea. **NO se bloquea**: la
  decision de si un plazo vencido invalida fiscalmente la ND es del contador (ADR-013 §11.2, abierto). El
  MVP es conservador en el sentido de **avisar**, no de decidir el tratamiento fiscal por su cuenta.
- **Decision a desafiar (§10.4):** alerta vs bloqueo. Propongo alerta porque bloquear sin dictamen del
  contador podria impedir una emision que es valida; un vencimiento de plazo es un tema fiscal, no un
  invariante de software.
- **Plazo MUY vencido (+60/+90 dias) — RESOLUCION DE M4 (Round 2).** El review pidio documentar que pasa
  cuando el operador confirma mucho despues de los 15 dias. **Decision en capas, sin bloqueo de software:**
  1. **El sistema NO bloquea ni rutea a manual por plazo** (coherente con §3.8): emite igual + warning +
     counter. La validez fiscal de una ND tardia es decision del contador, no del software.
  2. **La realidad la dicta ARCA, no nosotros.** Una ND con fecha muy posterior puede ser **rechazada por
     ARCA** (CAE rechazado). Si eso ocurre, el flujo ya lo maneja: la ND queda `DebitNoteStatus = Failed`
     con el mensaje de ARCA y **cae en la bandeja** "NC sin su ND" (§3.8, §3.9) para tratamiento manual.
     **No hay camino oculto:** un rechazo por plazo se ve como cualquier otro rechazo de ARCA.
  3. **Escalonamiento del warning (configurable):** ademas del warning a >15 dias, un segundo umbral
     (`CancellationDebitNoteHardWarnDays`, default p.ej. 60) eleva el log a nivel `Warning` con un
     `metric:cancellation_debit_note_very_late` distinto, para que el back-office/contador lo priorice. No
     cambia el comportamiento, solo la visibilidad.
  - **Por que no bloquear a +60:** elegir un numero de corte en software seria inventar una regla fiscal
    sin dictamen. ARCA es la autoridad sobre si acepta el comprobante; nuestro trabajo es **emitir,
    observar y exponer** el resultado, no pre-decidir el rechazo.

### 3.6 Permisos y aprobacion (4-eyes)

- **Permiso base**: `cancellations.classify_agency_penalty` (Admin + Colaborador), resuelto server-side
  igual que en `Confirm` (`CancellationsController.cs:145-147`). Sin el -> 403.
- **4-eyes (RESOLUCION DE M2, Round 2).** El review de Round 1 (M2) objeto que "4-eyes por umbral de
  monto es insuficiente para una emision fiscal pura" y propuso atarlo a "hay `SupportingDocumentReference`
  o siempre". **Decision Round 2:** reusar el patron de aprobacion ya presente en `Confirm`
  (`ApprovalRequiredException` -> 409 `requiresApproval`, `CancellationsController.cs:160-172`, espejando
  los thresholds de FC1.3) **y endurecerlo asi**, en orden:
  1. **Siempre auditado** (clasificador + confirmador non-null es invariante del gating; sin eso ->
     ManualReview).
  2. **4-eyes OBLIGATORIO si NO hay `SupportingDocumentReference`** (no se adjunto soporte del acuerdo del
     operador). Confirmar una penalidad propia **sin** respaldo documental es el caso de mayor riesgo de
     declarar un ingreso ficticio: ahi exigimos doble firma siempre, sin importar el monto. Esto recoge
     literalmente la sugerencia de M2.
  3. **4-eyes OBLIGATORIO si `ConfirmedPenaltyAmount` supera el threshold de approval del modulo** (el
     mismo que gobierna las cancelaciones con pagos), aunque haya soporte documental.
  4. En el resto (monto bajo el umbral **Y** con soporte documental), alcanza el permiso
     `classify_agency_penalty`.
  - **Por que no siempre 4-eyes:** forzar doble firma para toda penalidad propia friccionaria el caso
    dominante (penalidades chicas, agencia chica, con su mail de confirmacion adjunto). La regla 2 cubre el
    riesgo fiscal real (emision sin respaldo) sin paralizar el flujo legitimo. El umbral y el "exigir
    soporte" son **configurables** en `OperationalFinanceSettings` para que cada cliente endurezca segun su
    perfil. El reviewer puede aun desafiar si conviene 4-eyes **siempre** para toda ND; lo dejamos en la
    regla 2+3 por costo/beneficio, documentado.
- **Anti-reclasificacion**: heredada de `EnsureConceptNotLockedByDebitNote:1890` (via
  `CaptureDebitNoteClassification`): si la ND ya esta en juego, no se puede cambiar el concepto.

### 3.7 Relacion con el CHECK del VO `FiscalLiquidation` (punto que marco el contador)

**Verificado:** este camino diferido **NO toca el VO `FiscalLiquidation`**, igual que el sincrono. El VO
solo se escribe dentro de `if (settings.EnablePartialCreditNotes)` (`BookingCancellationService.cs:396`,
escritura `:512-515`); en el path FC1.2 (el del MVP de ADR-013 y de este ADR) queda **NULL** y su CHECK de
suma tiene clausula `IS NULL OR...` -> **no aplica**. El motor `TryEmit...` construye la ND con su propio
cuadre ARCA (`ImpTotal == ImpNeto`, `ImpIVA=0` para C) y **no participa de ninguna suma que involucre el
refund** (el monto de la ND vive en `PenaltyAmountAtEvent`, fuera de la liquidacion de la NC).

**Conclusion (respondiendo al punto del contador):** la preocupacion de que "la ND incrementa, no reduce,
y rompe el CHECK de suma" **no se materializa** porque el CHECK opera sobre un VO que en este flujo no
existe. El anti-doble-cobro se garantiza a nivel **evento** (INV-ADR013-001): si el concepto es ND propia,
el backend **rechaza** cargar la misma penalidad como `DeductionKind.CancellationPenalty` (guarda
simetrica verificada en `OperatorRefundService.cs:354-365` + en el motor `:1611`). **El camino diferido
hereda esa proteccion sin cambios** porque usa el mismo motor y la misma clasificacion.

### 3.8 Gating y casos a ManualReview (heredados, no relajados)

El camino diferido usa **el mismo `EvaluateDebitNoteGating`** (`:1913`). Por lo tanto, en el Dia N caen a
`ManualReview` exactamente los mismos casos que en el sincrono: pass-through, factura no-C (A/B/M),
no-ARS, seguros, `CorrectCreditNote`/`FceMiPyme`, factura con Tributos (IIBB), `penalty > total`, o
auditoria incompleta (`ConceptClassifiedByUserId`/`PenaltyConfirmedByUserId` null). **No se agrega ni se
relaja ningun caso.** La unica diferencia es **cuando** se evalua (Dia N en vez de Dia 0) y que para ese
momento `PenaltyStatus` ya es `Confirmed` (la condicion que en el sincrono casi siempre fallaba).

**Caso nuevo a rutear a manual (propuesto, §3.5):** plazo vencido -> el ADR propone **alerta, no manual**
(§3.5). Si el contador dicta que el plazo vencido invalida la ND, se agrega una rama al gating
(`OperatorConfirmationDate` + N dias < hoy -> ManualReview). Queda pendiente del contador.

**Anti-doble-cobro RE-CHEQUEADO en el Dia N (no snapshot del Dia 0) — explicito por exigencia del review.**
Entre el Dia 0 y el Dia N pudo cargarse una deduccion de penalidad
(`DeductionKind.CancellationPenalty`) en el refund del operador (`OperatorRefundService`). El gating
diferido **NO** debe confiar en ningun snapshot tomado en el Dia 0: la disyuncion INV-ADR013-001 se
**re-evalua en el Dia N con una query fresca**. **VERIFICADO en codigo:** el motor `TryEmit...` consulta
`_db.OperatorRefundAllocations ... AnyAsync(d => d.Kind == CancellationPenalty)` en runtime
(`BookingCancellationService.cs:1611`), dentro de la misma corrida del Dia N — **no** lee un campo
congelado. Si en el Dia N existe una deduction de penalidad cargada, la ND se rutea a ManualReview (no se
emite). Como el camino diferido usa **el mismo motor**, hereda esta re-evaluacion sin cambios. **El ADR lo
exige explicitamente:** la implementacion NO debe introducir un atajo que cachee el resultado del
anti-doble-cobro del Dia 0; debe pasar siempre por `TryEmit...` (que re-consulta). Un test de integracion
del Dia N debe cubrir: deduction cargada DESPUES del Dia 0 -> confirm-penalty rutea a ManualReview.

**Re-vinculacion de ND huerfana (B1 pieza 3, §3.4).** La bandeja `GetCancellationsWithMissingDebitNoteAsync`
se **extiende** para cubrir el caso de crash entre crear-la-ND (T1) y vincularla (T2): un BC con
`PenaltyStatus == Confirmed` **y** `DebitNoteInvoiceId == null` para el cual exista una Invoice ND
(`IsDebitNote`) asociada a la **factura original** del BC. **VERIFICADO** que la bandeja actual NO lo cubre:
filtra `CreditNoteInvoiceId != null && DebitNoteStatus in (Pending,Failed)` y proyecta sobre BCs que ya
tienen `DebitNoteInvoice` cargada (`:1111-1116`) — un BC con `DebitNoteInvoiceId == null` queda invisible.
La extension: detectar esos BCs, **re-vincular** la ND existente (no emitir otra), reconciliar su estado.
Si NO existe ND asociada (T1 nunca commiteo), el BC con `PenaltyStatus == Confirmed` y sin ND tambien
aparece en la bandeja, donde un operador con permiso puede **re-disparar** la emision explicitamente (este
es el unico camino de re-emision tras un `Confirmed`; el endpoint `confirm-penalty` ya rebota por
`PenaltyStatus == Confirmed`, §3.1 precondicion 6).

### 3.9 Compatibilidad flag OFF y byte-identidad

- Con `EnableCancellationDebitNote` OFF: el endpoint `confirm-penalty` **rechaza con 409** (precondicion
  1) — no muta nada. No hay path nuevo que altere el comportamiento del Dia 0 (la captura sincrona ya
  short-circuita con el flag OFF, `CaptureDebitNoteClassification:1770`). **Byte-identico** verificado por
  reuso del mismo guard de flag.
- Migracion aditiva (2 columnas nullable). Sin backfill. Drop limpio en rollback. Las NDs ya emitidas con
  CAE son inmutables (no se borran por rollback de schema) — igual que ADR-013 §5.2.

---

## 4. Fases

Todo detras de `EnableCancellationDebitNote` (OFF = comportamiento actual).

### Fase 1 (MVP diferido) — el caso dominante

Backend:
- Migracion aditiva: `OperatorPenaltyConfirmedDate` (`DateTime?`) + `SupportingDocumentReference`
  (`string?` 500) en `BookingCancellation`. Setting opcional `CancellationDebitNoteGraceDays` (default 15).
- DTO `ConfirmPenaltyRequest` (§3.1).
- Endpoint `PATCH /api/cancellations/{publicId}/confirm-penalty` (controller thin, resuelve permiso
  server-side, mapea excepciones: 404 / 403 / 409 `requiresApproval` / 409 `CONCURRENT_EDIT` / 409
  invariantes / 400 / 503 — mismo shape que `Confirm`/`EditLiquidation`).
- Metodo de service `ConfirmPenaltyAsync` (§3.2): valida precondiciones, aplica
  `CaptureDebitNoteClassification` (firma adaptada), setea las fechas, llama a
  `TryEmitCancellationDebitNoteAsync`, audita.
- Refactor de shape de `CaptureDebitNoteClassification` para aceptar el nuevo request sin cambiar la
  logica del path sincrono (con tests de no-regresion del sincrono).
- Alerta de plazo (§3.5): warning + counter, NO bloqueante.
- Interfaz `IBookingCancellationService`: agregar `ConfirmPenaltyAsync`.

Frontend (si aplica UI):
- En la lista "en proceso" (cancelaciones con penalidad estimada): accion "Confirmar penalidad del
  operador" -> form con concepto (2 opciones: cargo de gestion / cargo de cancelacion), monto confirmado,
  fecha de confirmacion del operador, soporte documental opcional. Estados loading/empty/error/permiso.
  Mostrar que la ND saldra (o que va a revision manual). El permiso elevado gatea la accion en la UI **y**
  server-side (no confiar en el front).

Homologacion ARCA:
- La ND C diferida usa el mismo motor que la sincrona -> el CAE de homologacion de ADR-013 §3.6 la cubre,
  **salvo** que haya que confirmar que la fecha del comprobante es la del Dia N (§3.2, supuesto). Probar
  en homologacion una ND emitida con fecha posterior a la NC asociada.

Tests:
- Unit del gating diferido (reusa `EvaluateDebitNoteGating`, ya cubierto; agregar el caso
  `Estimated -> Confirmed` en el Dia N).
- Unit de precondiciones del endpoint (estado post-NC, flag, permiso, idempotencia, fecha).
- **Unit B1 — pre-check idempotente:** confirm-penalty sobre un BC con `PenaltyStatus==Confirmed` ->
  409 `INV-ADR014-003`, ninguna segunda ND; con `DebitNoteStatus∈{Pending,Issued}` -> 409.
- **Unit B1 — orden de commit:** verificar que la clasificacion se persiste (commit c) ANTES de invocar
  `TryEmit` (la marca `Confirmed` existe aunque `TryEmit` falle/rebote a ManualReview).
- **Unit B2 — balance neutro:** confirm-penalty sobre un BC cuya reserva esta `Closed` -> `Reserva.Balance`
  y `Reserva.Status` sin cambios; `UpdateBalanceAsync` NO se invoca.
- **Unit M1 — no-regresion del sincrono:** los 4 caminos de permiso de `CaptureDebitNoteClassification`
  (`:1791,:1803,:1825`) se comportan igual tras extraer el record comun.
- Equivalencia flag OFF (endpoint rechaza, nada muta).
- Integration (VPS): cancelacion -> NC con CAE -> dias despues confirm-penalty -> ND emitida + CbtesAsoc a
  la factura original + `DebitNoteStatus=Issued`; concurrencia (dos confirm-penalty -> uno gana, otro 409
  por `Confirmed`/`xmin`); ND fallida -> bandeja.
- **Integration B1 — ND huerfana:** simular crash entre crear-la-ND (T1) y vincular (T2) -> la bandeja
  detecta el BC con `PenaltyStatus==Confirmed` + `DebitNoteInvoiceId==null` + ND existente -> re-vincula,
  NO emite otra.
- **Integration anti-doble-cobro diferido (R13):** cargar una deduction `CancellationPenalty` en el refund
  DESPUES del Dia 0 -> confirm-penalty en el Dia N rutea a ManualReview (re-chequeo runtime, no snapshot).

### Fase posterior

- Plazo vencido como ManualReview (si el contador lo dicta, §3.8).
- ND complementaria estimado vs real (si algun dia se emite sobre estimado — hoy NO).
- RI/IVA, multimoneda, seguros, etc. — igual que ADR-013, fuera de alcance.

---

## 5. Consecuencias, compatibilidad y rollback

- **Compatibilidad**: migracion 100% aditiva, defaults nullable. Flag OFF = byte-identico. El VO
  `FiscalLiquidation` y su CHECK **no se tocan** (§3.7). **`Reserva.Balance` y la maquina de estados de la
  reserva NO se tocan** (§3.4-bis, B2): la ND es solo comprobante. La extension de la bandeja (§3.8) es
  aditiva (no cambia el contrato existente, solo amplia el conjunto de candidatos).
- **Exactly-once (B1)**: garantizado por el orden de commit (marca `Confirmed` antes de crear la ND) +
  pre-check + 409 idempotente, sin reordenar el pipeline de facturacion. Ver §3.4.
- **Rollback**: apagar el flag (el endpoint rechaza). Drop de las 2 columnas nullable si hiciera falta.
  NDs emitidas con CAE son inmutables (no se borran por rollback de schema; se anulan via NC sobre la ND,
  flujo fiscal normal).
- **Riesgo de activacion**: no prender el flag en prod hasta el signoff del contador (ADR-013 §11 +
  fecha-del-comprobante-Dia-N) + CAE de homologacion (ADR-013 §3.6 + caso ND diferida).

---

## 6. Estrategia de testing

> Entorno (regla del proyecto): Postgres en VPS remoto. Unit local (InMemory + Moq); integration con
> TestContainers los corre el reviewer en el VPS.

- **Unit (local)**:
  - Endpoint precondiciones: BC en `Drafted`/`AwaitingFiscalConfirmation` -> rechazo `INV-ADR014-001`;
    `CreditNoteInvoiceId == null` -> rechazo; pass-through -> `INV-ADR014-002`; ND ya en juego ->
    `INV-ADR014-003` (idempotente); `OperatorConfirmationDate` futura o anterior a la cancelacion -> 400.
  - Permiso: sin `classify_agency_penalty` y no-Admin -> 403; el service exige el flag de permiso.
  - Flag OFF: endpoint rechaza, BC sin mutar (byte-identidad).
  - Captura diferida: `Estimated -> Confirmed` + monto + fecha + auditoria del clasificador/confirmador;
    `CaptureDebitNoteClassification` adaptado **no cambia** el path sincrono (no-regresion).
  - Plazo: confirmacion vencida (> 15 dias) -> emite igual + warning/counter (no bloquea).
  - Anti-doble-cobro: si hay una deduction `CancellationPenalty` cargada -> `TryEmit` rutea a manual
    (heredado, reconfirmar en el camino diferido).
- **Integration (VPS)**:
  - Flag OFF: confirm-penalty -> 409, ninguna ND, BC intacto.
  - Flag ON: NC con CAE (Dia 0) -> dias despues confirm-penalty (Dia N) -> ND C (CbteTipo=12) emitida +
    CbtesAsoc a la **factura original** + `DebitNoteInvoiceId` seteado + `DebitNoteStatus=Issued`.
  - Concurrencia: dos confirm-penalty simultaneos -> uno emite, el otro 409 (`xmin` / idempotencia).
  - Recovery: ND falla CAE -> NC intacta, `DebitNoteStatus=Failed`, aparece en bandeja "NC sin su ND".
  - Fecha del comprobante de la ND = Dia N (no Dia 0) — verificar contra el Invoice emitido.
  - **B1 ND huerfana**: crash entre T1 (crear ND) y T2 (vincular) -> bandeja re-vincula, no re-emite.
  - **B1 reintento tras 409**: confirm-penalty -> 409 `xmin` simulado -> reintento -> 409 idempotente por
    `PenaltyStatus==Confirmed`, una sola ND en total.
  - **B2 balance**: ND sobre BC `Closed` -> `Reserva.Balance`/`Status` intactos.
  - **R13 anti-doble-cobro diferido**: deduction cargada despues del Dia 0 -> ManualReview en el Dia N.
- **M3 (determinismo de fecha, bloquea PRENDER flag si el contador lo exige):** `CbteFch` se computa con
  `DateTime.Now` del job async (no determinista, §10 punto 6); el test debe asertar
  `IssuedAt >= OperatorConfirmationDate` y dentro de la ventana RG 4540.
- **Homologacion ARCA (manual)**: ND C diferida (fecha posterior a la NC) asociada a factura C -> CAE
  aprobado. Bloquea prod.

---

## 7. Riesgos

| # | Riesgo | Sev | Mitigacion |
|---|---|---|---|
| R1 | Emitir la ND con fecha del Dia 0 en vez del Dia N (regla fiscal 1) | **Alto** | §3.2: la ND se crea en el Dia N via `CreateAsync`; fecha del comprobante = momento de emision. **Supuesto a confirmar en homologacion** (deuda de fechas hardcoded heredada de ADR-013 §11.4). Test de integracion verifica la fecha |
| R2 | Doble emision de la ND (dos confirm-penalty, o confirm-penalty + reintento tras 409) | **Alto** | §3.4 (B1 resuelto): marca `PenaltyStatus=Confirmed` persistida en commit propio ANTES de crear la ND (pieza 2) + pre-check que rebota por `Confirmed`/ND-en-juego (pieza 1) + unique index parcial `UX_Invoices_OnePendingPerReserva` (la garantia NO usa idem key ARCA — no existe para el path de ND, ver §3.4). El 409 `xmin` es seguro de reintentar |
| R3 | Estado zombi: ND creada (T1) pero `DebitNoteInvoiceId` no vinculado (T2 fallo/crash) -> reintento emite segunda ND | **Alto** | §3.4 (B1 resuelto): la marca `Confirmed` (commit c) vive ANTES de crear la ND, asi un reintento rebota; la ND huerfana se **re-vincula** desde la bandeja (§3.8 pieza 3), no se re-emite. **NO** se reordena `CreateAsync` (acoplaria facturacion con cancelacion) |
| R4 | Confirmar penalidad propia sin permiso (vendedor dispara ND fiscal) | **Alto** | §3.6: permiso `classify_agency_penalty` server-side; sin el -> 403. Heredado del path sincrono |
| R5 | Confirmar penalidad cuando es pass-through (declarar ingreso ajeno) | **Alto** | §3.1 precondicion 5: pass-through -> 409 `INV-ADR014-002`. Gating heredado tambien lo rebota |
| R6 | ND diferida sobre un BC cuya NC nunca obtuvo CAE | **Alto** | §3.1 precondicion 4: exige `CreditNoteInvoiceId != null` + estado post-NC |
| R7 | Plazo RG 4540 vencido al confirmar tarde | Medio | §3.5: alerta + counter (no bloqueante); el contador decide si invalida (ADR-013 §11.2 abierto) |
| R8 | Anti-doble-cobro: la penalidad ya bajo el refund via deduction | **Alto** | §3.7: INV-ADR013-001 heredado (guarda simetrica `OperatorRefundService.cs:354-365` + motor `:1611`) |
| R9 | ND diferida fallida queda invisible | Medio | Reusa bandeja "NC sin su ND" + reconciliacion `GetCancellationsWithMissingDebitNoteAsync:1104` sin cambios |
| R10 | Refactor de `CaptureDebitNoteClassification` rompe el path sincrono | Medio | §10.3/M1: NO cambiar firma a polimorfica; extraer `record PenaltyClassificationInput` comun; tests de no-regresion de los 4 caminos de permiso del sincrono (`:1791,:1803,:1825`) |
| R11 | Dos fechas (sistema vs operador) confundidas -> plazo/devengamiento mal | Medio | §3.3: `OperatorPenaltyConfirmedDate` separada de `PenaltyConfirmedAt`; cada eje fiscal usa la suya |
| R12 | ND diferida modifica el balance/reabre la reserva por error | Medio | §3.4-bis (B2 resuelto): opcion (c), la ND es SOLO comprobante; `Balance=TotalSale-TotalPaid` ignora comprobantes (VERIFICADO `:2196`); `TryEmit` no llama `UpdateBalanceAsync`. **Requiere validacion de Gaston** (cuenta corriente vs comprobante) |
| R13 | Anti-doble-cobro evaluado sobre snapshot del Dia 0 (deduction cargada despues queda invisible) | **Alto** | §3.8: INV-ADR013-001 re-evaluado en runtime con query fresca en el Dia N (`:1611`, VERIFICADO); el ADR prohibe cachear el resultado del Dia 0; test de integracion del Dia N |

---

## 8. Precondiciones backend (lista para el que implemente)

1. **Migracion aditiva**: `OperatorPenaltyConfirmedDate` (`DateTime?`) + `SupportingDocumentReference`
   (`string?` MaxLength 500) en `BookingCancellation`; opcional `CancellationDebitNoteGraceDays` en
   `OperationalFinanceSettings` (default 15). Postgres, comillas dobles. Sin backfill.
2. **DTO** `ConfirmPenaltyRequest` (§3.1) con DataAnnotations (`ConfirmedPenaltyAmount` Range 0.01+,
   `OperatorConfirmationDate` Required, `SupportingDocumentReference` MaxLength 500).
3. **Endpoint** `PATCH /api/cancellations/{publicId}/confirm-penalty`: controller thin, resuelve
   `userCanClassifyAgencyPenalty` server-side (igual que `Confirm:145`), mapea excepciones al mismo shape.
4. **`ConfirmPenaltyAsync`** en el service: valida las 7 precondiciones (§3.1) **fail-fast en ese orden**;
   carga el BC con los Includes que necesita el gating (`OriginatingInvoice.ThenInclude(Tributes)` +
   `Supplier`, igual que `OnArcaSucceededAsync:1444-1455`). **Orden de operaciones de B1 (§3.4 pieza 2),
   OBLIGATORIO:** (a) `CaptureDebitNoteClassification` (setea `PenaltyStatus=Confirmed` + auditoria); (b)
   setea `OperatorPenaltyConfirmedDate` + `SupportingDocumentReference`; (c) **`SaveChanges` (commit de la
   marca de no-retorno) ANTES de crear la ND**; (d) `TryEmitCancellationDebitNoteAsync`; (e) audita el
   evento de confirmacion diferida. NO fusionar (c) con el `SaveChanges` interno de `TryEmit`.
5. **Refactor de `CaptureDebitNoteClassification` (M1):** NO cambiar la firma a polimorfica. Extraer un
   `record PenaltyClassificationInput` (concepto/finalidad/monto/flags de permiso) que construyan TANTO
   `ConfirmCancellationRequest` (sincrono) COMO `ConfirmPenaltyRequest` (diferido); el metodo consume ese
   record. **No cambiar** la logica del path sincrono — **tests de no-regresion de los 4 caminos de
   permiso** (`:1791,:1803,:1825`).
6. **Exactly-once (B1):** implementar el pre-check de la precondicion 6 (§3.1) que rebota por
   `PenaltyStatus==Confirmed` / `DebitNoteInvoiceId.HasValue` / `DebitNoteStatus∈{Pending,Issued}`; mapear
   `DbUpdateConcurrencyException` a 409 `CONCURRENT_EDIT` (controller); confiar en que la marca `Confirmed`
   (paso 4c) hace el 409 seguro de reintentar. **NO reordenar `CreateAsync`** (§3.4). NOTA (Round 2,
   verificado): el path de ND **no tiene** idempotency key a nivel ARCA (no existe en codigo; solo la usa
   la NC parcial). La garantia exactly-once recae enteramente en las piezas 1-3 + el unique index parcial
   `UX_Invoices_OnePendingPerReserva`. No agregar idem key ARCA en el MVP (§3.4).
7. **B2 (efecto en balance):** `ConfirmPenaltyAsync` / `TryEmit...` **NO** deben llamar a
   `UpdateBalanceAsync` ni mutar `Reserva.Balance` ni `Reserva.Status`. La ND es solo comprobante (§3.4-bis,
   opcion c). Test: emitir la ND sobre un BC `Closed` -> `Reserva.Balance` y `Reserva.Status` sin cambios.
8. **Extension de la bandeja** (§3.8 pieza 3, M-R2-1): `GetCancellationsWithMissingDebitNoteAsync` debe
   detectar BCs con `PenaltyStatus==Confirmed` y `DebitNoteInvoiceId==null` (ND huerfana o nunca creada)
   para re-vincular o permitir re-disparo manual. Hoy NO los ve (proyecta sobre `DebitNoteInvoice` ya
   cargada y filtra `CreditNoteInvoiceId!=null && DebitNoteStatus∈{Pending,Failed}` — VERIFICADO), por lo
   que requiere **un segundo query/rama** (el actual nunca devuelve `DebitNoteInvoiceId==null`).
   **Matching de la ND huerfana (precision Round 2):** la ND candidata es una `Invoice` con
   `IsDebitNote==true`, `OriginalInvoiceId == bc.OriginatingInvoice` y la misma `ReservaId`. La
   re-vinculacion **debe validar que la `OriginalInvoice` de la ND candidata coincide con la del BC** antes
   de asociar, para no re-vincular una ND de otro evento. La marca `PenaltyStatus==Confirmed` (pieza 2.c)
   garantiza que re-vincular NUNCA re-emite. Los identificadores existen (verificado por el reviewer); el
   SQL exacto del segundo query queda como detalle de implementacion.
9. **Alerta de plazo** (§3.5): warning + counter a >15 dias; segundo umbral
   `CancellationDebitNoteHardWarnDays` (default 60) -> warning elevado + counter distinto (M4). No
   bloqueante.
10. **4-eyes (M2, §3.6):** 4-eyes obligatorio si NO hay `SupportingDocumentReference` O si el monto supera
    el threshold del modulo; ambos configurables en `OperationalFinanceSettings`.
11. **Interfaz** `IBookingCancellationService`: agregar `ConfirmPenaltyAsync` con el mismo patron de
    parametros que `ConfirmAsync` (incluyendo `userCanClassifyAgencyPenalty`).

### Fuera de alcance (fase futura, explicito)
- Plazo vencido como ManualReview (vs alerta) — depende del contador.
- ND complementaria estimado vs real (hoy la ND sale directo por el confirmado).
- RI/IVA, multimoneda, seguros, FCE, IIBB, NC parcial — igual que ADR-013.
- Computo del limite del Monotributo (solo se exponen las fechas).
- Almacenamiento real del soporte documental (hoy solo una referencia/URL string; subir el archivo a
  MinIO seria una pieza aparte).

---

## 9. Lo que debe validar el contador matriculado (bloquea PRENDER el flag, no construir)

1. **Fecha del comprobante de la ND diferida = Dia N** (regla fiscal 1): confirmar que ARCA acepta una ND
   con fecha posterior a la NC asociada y que el plazo RG 4540 no la rechaza (homologacion).
2. **Plazo 15 dias corridos desde la confirmacion del operador** (regla fiscal 2): confirmar el hecho
   exacto desde el que corre y que pasa si se emite vencido (define si §3.8 va a alerta o a ManualReview).
3. **Devengamiento Monotributo** (regla fiscal 5): confirmar que exponer las dos fechas (cancelacion +
   confirmacion) alcanza para que el contador/dueno impute el ingreso al periodo correcto.
4. **Las mismas de ADR-013 §11** (leyenda RG 4540 en SOAP vs PDF, Concepto/fechas del envelope,
   transicion Mono->RI con snapshot congelado).
5. **Homologacion ARCA**: CAE aprobado para ND C diferida asociada a factura C. **Bloquea prod.**

---

## 10. Puntos de diseno — estado tras Round 2

> Round 1 dejo estos 6 puntos abiertos. Round 2 los resuelve abajo; los que siguen abiertos para
> re-review estan marcados.

1. **§3.1 — ninguna transicion nueva en `BookingCancellationStatus`. [RESUELTO + B2].** Se mantiene: no se
   toca la maquina T0..T3 (la confirmacion de la penalidad es ortogonal al refund). La observabilidad
   "penalidad pendiente de confirmar" se cubre con `PenaltyStatus=Estimated` + la lista "en proceso", y
   "ND huerfana / pendiente" con la bandeja (§3.8). **B2 (BC `Closed`):** se emite la ND igual, **sin**
   reabrir la reserva ni tocar el balance (§3.4-bis, opcion c) — coherente con que `Balance=TotalSale-
   TotalPaid` ignora comprobantes (VERIFICADO). **Pendiente para re-review:** confirmar con Gaston si la
   penalidad debe volverse "saldo a cobrar" (opcion a, fase futura) o alcanza el comprobante (opcion c).
2. **§3.6 — 4-eyes. [RESUELTO, M2].** Ya no es solo por umbral: 4-eyes obligatorio si **no hay soporte
   documental** O si el monto supera el threshold (ambos configurables). Ver §3.6.
3. **§3.2 — refactor de `CaptureDebitNoteClassification`. [RESUELTO, M1].** Se extrae un `record`
   de clasificacion comun (NO firma polimorfica); tests de no-regresion de los 4 caminos de permiso del
   sincrono. Ver §3.2 paso 1 y precondicion 5.
4. **§3.5 — plazo: alerta vs bloqueo. [RESUELTO, M4].** Alerta, no bloqueo; +60 dias = warning elevado;
   un rechazo real de ARCA cae en la bandeja como `Failed`. Ver §3.5 y §3.8.
5. **§3.4 — atomicidad / exactly-once (B1). [RESUELTO].** Se confirmo en codigo que `CreateAsync` commitea
   la ND y encola el job ANTES de vincular el BC (dos transacciones). La solucion **no** reordena
   `CreateAsync`: persiste la marca `PenaltyStatus=Confirmed` en un commit propio ANTES de crear la ND, de
   modo que un reintento tras 409 rebota; la ND huerfana se re-vincula desde la bandeja. Ver §3.4.
6. **§3.2 — fecha del comprobante. [RESUELTO a favor por el reviewer].** El review verifico
   `AfipService.cs:1181` (`DateTime.Now` dentro de `SendToAfip`, que corre en el job async del Dia N) ->
   la ND diferida fecha al Dia N por construccion; `CbteFch` jamas deriva de `CreatedAt/IssuedAt`. NO es
   bloqueante de construir. **Queda como tema de homologacion** (que ARCA acepte la fecha Dia N dentro del
   plazo RG 4540) y como **M3 para PRENDER el flag**: `CbteFch` = fecha del job (no determinista) puede
   divergir de `OperatorConfirmationDate`; el test debe asertar `IssuedAt >= OperatorConfirmationDate` y
   dentro de la ventana RG 4540 (§6, §9).

---

## 11. Alternativas consideradas

1. **Extender `ConfirmAsync` para que acepte la confirmacion diferida.** Descartada: `ConfirmAsync` solo
   opera desde `Drafted` (`:286`) y dispara la NC; el diferido opera post-NC. Mezclarlos sobrecargaria un
   metodo ya complejo y arriesgaria el path sincrono.
2. **Un job nocturno que detecte penalidades confirmadas y emita la ND.** Descartada: la confirmacion del
   operador es un acto humano (el usuario informa el monto y la fecha); un job no tiene de donde sacar el
   monto confirmado. El endpoint explicito es la fuente del dato + la auditoria de quien confirmo.
3. **Disparar la ND desde `EditLiquidation`.** Descartada: `EditLiquidation` solo opera desde
   `ManualReviewPending` (`:1201`) y es del flujo NC parcial (congelado). No es el lugar.
4. **Reabrir el BC a un estado previo para re-correr `OnArcaSucceededAsync`.** Descartada: la NC ya tiene
   CAE; reabrir el estado fiscal seria peligroso y `OnArcaSucceededAsync` ademas re-transicionaria la
   reserva. El endpoint dedicado es mas limpio y no toca la maquina T0..T3.
5. **Modelar la penalidad confirmada como entidad/VO nuevo.** Descartada para el MVP (mismo criterio que
   ADR-013 §8.3): es un atributo del evento de cancelacion; columnas + el snapshot existente alcanzan.

---

## 12. Migracion / rollback

Ver §5. Aditiva (2 columnas nullable + 1 setting opcional), sin backfill, reversible por flag + drop.
NDs emitidas con CAE son inmutables.

---

## 12-bis. Registro de review

### Round 1 (2026-06-02) — veredicto Changes Required
Review en `.claude/agent-memory/software-architect-reviewer/adr-014-deferred-debit-note-review-round1.md`.
Resuelto a favor del ADR: **R1** (la ND fecha al Dia N por construccion, `AfipService.cs:1181` verificado).
Bloqueantes: **B1** (estado zombi / atomicidad: `CreateAsync` commitea la ND y encola el job antes de
vincular `bc.DebitNoteInvoiceId`; un 409 `xmin` en un request humano podia disparar una segunda ND).
**B2** (efecto en balance/AR de una ND sobre BC `Closed`, delegado a decision del architect). Majors:
**M1** (refactor de `CaptureDebitNoteClassification`), **M2** (4-eyes por umbral insuficiente para emision
fiscal pura), **M3** (determinismo de la fecha del comprobante para PRENDER el flag), **M4** (plazo +60/90).

### Round 2 (2026-06-02) — resoluciones
- **B1 (§3.4):** verificado en codigo que `CreateAsync` usa dos transacciones (T1 crear ND PENDING +
  encolar job `InvoiceService.cs:396,413`; T2 vincular BC `BookingCancellationService.cs:1702`). Solucion:
  persistir la marca `PenaltyStatus=Confirmed` en un commit propio ANTES de crear la ND (mueve el punto de
  no-retorno antes de T1) + pre-check que rebota por `Confirmed`/ND-en-juego + manejo del 409 `xmin` que
  ahora es seguro de reintentar + re-vinculacion de ND huerfana via bandeja. **NO** se reordena
  `CreateAsync` (acoplaria facturacion con cancelacion). Defensa en profundidad: unique index parcial
  `UX_Invoices_OnePendingPerReserva` (verificado `:387-405`). CORRECCION Round 2: NO hay idempotency key
  ARCA para el path de ND (verificado: no existe en codigo, solo la usa la NC parcial); el exactly-once
  recae enteramente en las 3 piezas + el unique index parcial. Ver §3.4.
- **B2 (§3.4-bis):** verificado que `Reserva.Balance = TotalSale - TotalPaid` (`ReservaService.cs:2196`)
  ignora todos los comprobantes (factura/NC/ND). Decision: **opcion (c)** — la ND es solo comprobante
  fiscal, NO toca balance ni reabre la reserva. **Marcado como requiere validacion de Gaston** (cuenta
  corriente automatica vs comprobante + cobranza aparte). Neutral al rediseno `EnableSoldToSettleStates`
  (el guard fiscal `:318` no aplica a ND, verificado `:352`).
- **M1 (§3.2, §3.6):** extraer `record PenaltyClassificationInput` comun, no firma polimorfica; tests de
  no-regresion de los 4 caminos de permiso.
- **M2 (§3.6):** 4-eyes obligatorio si NO hay soporte documental O si supera el threshold; configurable.
- **M3 (§6, §9, §10.6):** queda como bloqueante de PRENDER el flag (no de construir); test asienta
  `IssuedAt >= OperatorConfirmationDate` y ventana RG 4540.
- **M4 (§3.5, §3.8):** plazo vencido = alerta + segundo umbral de warning; rechazo real de ARCA -> bandeja.
- **R13 (§3.8):** explicitado que el anti-doble-cobro se re-evalua en runtime en el Dia N (query fresca
  `:1611`, verificado), prohibido cachear el resultado del Dia 0.

**Pendiente para re-review (Round 2):** validar la coherencia de la solucion de B1 (especialmente que
mover la marca `Confirmed` antes de la creacion de la ND no rompa el path sincrono ni el gating) y que la
extension de la bandeja para ND huerfana sea implementable como se describe.

## 13. Fuentes

- ADR-013 (base) + sus fuentes (criterio del contador matriculado, RG 4540, art. 61 DR IVA + DAT 44/01).
- Codigo verificado: `BookingCancellationService.cs` (`OnArcaSucceededAsync:1434`,
  `TryEmitCancellationDebitNoteAsync:1539`, anti-doble-cobro runtime `:1611`, link+SaveChanges `:1681-1702`,
  `EvaluateDebitNoteGating:1913`, `CaptureDebitNoteClassification:1754`,
  `EnsureConceptNotLockedByDebitNote:1890`, `GetCancellationsWithMissingDebitNoteAsync:1104-1116`,
  `ConfirmAsync:244`), `OperatorRefundService.cs:354-365`, `BookingCancellation.cs:18-407`,
  `CancellationsController.cs` (409 `CONCURRENT_EDIT` `:379-390`), `Permissions.cs:123`,
  `CancellationDtos.cs:136-180`.
- **Round 2 (verificacion de B1/B2):** `InvoiceService.cs` (`CreateAsync` commitea ND + encola job
  `:303,396,413`; guard fiscal Sold no aplica a ND `:318,352`; unique index parcial
  `UX_Invoices_OnePendingPerReserva` `:387-405`), `ReservaService.cs` (`UpdateBalanceAsync:2158-2199`,
  `Balance = TotalSale - TotalPaid` `:2196`), `PaymentService.cs:834`,
  `ReservationEconomicPolicy.cs` (`Balance` puro).

> **Aviso profesional**: este ADR analiza y disena. La validez de la fecha del comprobante, el plazo RG
> 4540, el devengamiento Monotributo y el resto de los puntos fiscales deben ser confirmados por el
> contador matriculado antes de produccion. NO es autoridad fiscal final.
