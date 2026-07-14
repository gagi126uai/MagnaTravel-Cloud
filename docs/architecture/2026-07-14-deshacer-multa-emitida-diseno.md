# Diseño — "Deshacer una multa ya emitida" (ADR-044, tanda previa a la de emisión T5)

Fecha: 2026-07-14. Autor: software-architect. Estado: **propuesto, pendiente de
`software-architect-reviewer` + gate UX (Gastón) + gate fiscal (contador)**. NO implementar hasta
review verde.

Fuente de verdad fiscal (leída entera):
`.claude/agent-memory/travel-agency-accountant-argentina/adr044-deshacer-multa-emitida-spec.md`
(15 reglas duras). Este documento traduce esas 15 reglas a arquitectura concreta contra el código
REAL inspeccionado (no asumido).

---

## 1. Qué se pide (flujo de producto)

La Nota de Débito (ND) de la multa ya salió con CAE y estaba mal. En el panel de la ficha, la familia
"confirmada / resuelta" (`Done`) ofrece un botón nuevo **"Deshacer esta multa"** → modal con **motivo
obligatorio** → se emite una **NC asociada A LA ND** que la anula fiscalmente → la plata del cliente se
revierte → el paso de la multa vuelve a **abierto** (`ConfirmedNoDebitNote`: "confirmada pero todavía no
se le cobró", con *Cobrarle la multa ahora / Corregir monto y moneda / El operador no cobró esta multa*).
El ciclo emitir→deshacer→re-emitir puede repetirse.

---

## 2. Hechos verificados en el código (base fáctica, no supuestos)

| # | Hecho | Evidencia |
|---|-------|-----------|
| V1 | El motor de tipo ARCA deriva SOLO la letra de la NC a partir del `TipoComprobante` del comprobante asociado: ND C 12→NC C 13, ND A 2→NC A 3, ND B 7→NC B 8. | `AfipService.cs:629-679` (spec fiscal) + `InvoiceComprobanteHelpers`. Una NC con `OriginalInvoiceId = ND` sale con la letra correcta sola. |
| V2 | La ND de multa se emite por `TryEmitCancellationDebitNoteAsync` → `_invoiceService.CreateAsync` con `IsDebitNote=true`, `OriginalInvoiceId = factura`. El CAE llega **async** por `ProcessInvoiceJob`. | `BookingCancellationService.cs:8322-8596`. |
| V3 | `CancellationDebitNoteReconciliation` transiciona `BookingCancellation.DebitNoteStatus` de `Pending`→`Issued`/`Failed` cuando la ND consigue/rechaza CAE (llamado desde `ProcessInvoiceJob`). | `CancellationDebitNoteReconciliation.cs` + `AfipService.cs:1573,2655`. |
| V4 | **`EnqueueAnnulmentAsync` RECHAZA ND/NC/M** (`IsSupportedForAnnulment` sólo acepta facturas 1/6/11). El "deshacer" NO puede pasar por ese endpoint. | `InvoiceService.cs:1110-1116`. |
| V5 | **BLOQUEANTE #1 (verificado):** cuando CUALQUIER NC consigue CAE, `ProcessInvoiceJob` corre `ApplyCreditNoteEconomicReversalAsync(invoice.Id)` gateado sólo por `IsCreditNote(TipoComprobante)`. Nuestra NC-anula-ND ES `IsCreditNote` (13/3/8). Adentro busca BC por `CreditNoteInvoiceId==invoice.Id` (null en nuestro caso) → cae al fallback histórico → como `ImporteTotal == OriginalInvoice(la ND).ImporteTotal`, lo trata como **NC total** → `ApplyTotalCreditNoteReversalAsync` → busca un `Payment` que matchee el importe, crea un `-reversalAmount` y **anula el recibo** (`Receipt Voided`). Eso es lógica de anular-FACTURA (reversar cobros), **exactamente lo contrario** de lo que necesitamos. | `AfipService.cs:1566-1568, 1663-1720, 1902-1948, 2647-2649`. |
| V6 | El **extracto fiscal** (`ReservaAccountStatementBuilder` / `AddInvoiceLines`) cuenta TODO comprobante con CAE: ND = cargo, NC = abono, factura anulada se conserva con su NC. Nuestra NC-anula-ND, al conseguir CAE, aparece **automáticamente** como abono que compensa el cargo de la ND. El extracto se auto-sana; **no hay que tocar ese builder**. | `ReservaService.cs:199-266`. |
| V7 | El **cartel "multa por cobrar"** de la ficha usa `LiveDebitNotePredicate`: rama 1 = `DebitNoteStatus==Issued && DebitNoteInvoiceId!=null && DebitNoteInvoice.AnnulmentStatus!=Succeeded`; rama 2 = `PenaltyStatus==Confirmed && PenaltyAmountAtEvent>0 && DebitNoteStatus IN {NotApplicable, Pending}`. El monto mostrado se netea contra el saldo por moneda (`ComputePendingPenaltyForDisplay` → `Math.Min(gross, balance)`, clamp a 0). | `CancellationPenaltyRules.cs:55-64`, `ReservaService.cs:5127-5139`. |
| V8 | El paso del cartel lo deriva `OperatorPenaltySituationRules.Derive`: `Confirmed + DebitNoteStatus.Issued` → `Done`; `Confirmed + NotApplicable` → `ConfirmedNoDebitNote`. Es una regla PURA sobre primitivos. | `OperatorPenaltySituationRules.cs:44-84`. |
| V9 | `EnsureConceptNotLockedByDebitNote` bloquea reclasificar el concepto si `DebitNoteInvoiceId!=null \|\| DebitNoteStatus IN {Pending, Issued}` (INV-ADR013-002, "anulá la ND antes de reclasificar"). Al deshacer y **desvincular** la ND, ese candado se libera solo → coherente con "paso vuelve a abierto". | `BookingCancellationService.cs:9502-9520`. |
| V10 | Ya existe el patrón `RunUnderParentLockAsync(bc.Id)` = `SELECT … FOR UPDATE` sobre el BC padre + `lock_timeout` acotado, usado por confirm/correct/retry. `CorrectPenaltyAsync` es el molde exacto de una acción atómica que re-lee bajo lock, re-valida guards, muta y re-encola comprobante. | `BookingCancellationService.cs:4585-4614, 6458-6717`. |
| V11 | La ND congela un snapshot en el BC (`PenaltyAmountAtEvent`, `PenaltyCurrencyAtEvent`, `DebitNoteCbteTipoAtEvent`, `OriginalInvoiceCbteTipoAtEvent`). `CreatePendingInvoice` ya **espeja `CanMisMonExt`** del comprobante asociado para las ND (por tener `OriginalInvoiceId`). | `BookingCancellation.cs:342-374`, `BookingCancellationService.cs:8516-8518`. |

**Consecuencia de V6+V7 (clave de plata):** la reversión económica NO necesita crear ningún `Payment`.
El lado fiscal se sana con la NC (extracto). El lado operativo se sana desvinculando la ND (el cartel
vuelve a `ConfirmedNoDebitNote`). El **saldo a favor del caso pagado emerge solo**: el pago de la multa
queda vivo, la obligación desaparece, y `ComputePendingPenaltyForDisplay` ya netea (pendiente = 0 cuando
hay saldo). **Correr la reversión de cobros (V5) haría exactamente el daño opuesto**: anularía el recibo
y crearía un `-Payment`, borrando el saldo a favor / duplicando la devolución. Por eso el corazón del
diseño es **interceptar y NO correr** esa reversión para las NC que anulan una ND (regla dura #5).

---

## 3. Opciones consideradas para modelar el "deshacer"

**Opción A — reusar `DebitNoteStatus` con un valor nuevo (`Annulling`).** Descartada: `DebitNoteStatus`
describe el CAE de la ND propia; meterle "la ND se está anulando" ensucia su semántica y obliga a tocar
`LiveDebitNotePredicate`, el reconciliador y los readers que hacen `switch` sobre él. Riesgo de regresión
en un área de plata por ahorrar una tabla.

**Opción B — entidad hija `BookingCancellationDebitNoteAnnulment` (1 fila por evento de deshacer),
con su propio estado.** **Recomendada.** Es el mismo idiom que ya usa el repo para eventos N por
cancelación (`BookingCancellationCreditNote` de ADR-042, líneas de T5): la ND sigue siendo `Issued`
hasta que la anulación tiene CAE; el panel deriva un estado nuevo mirando "¿hay una anulación viva/fallida
para la ND de este BC?"; y el ciclo emitir→deshacer→re-emitir queda **historizado** (una fila por vuelta),
no pisado por escalares.

**Decisión: Opción B.**

---

## 4. Modelo de datos (migración ADITIVA, sin backfill)

### 4.1 Entidad nueva `BookingCancellationDebitNoteAnnulment`

```
BookingCancellationDebitNoteAnnulment
  Id (int, PK)
  PublicId (Guid, unique)
  BookingCancellationId (int, FK -> BookingCancellations, cascade delete)
  AnnulledDebitNoteInvoiceId (int, FK -> Invoices)   -- la ND que se está anulando (foto de cuál ND)
  AnnulmentCreditNoteInvoiceId (int?, FK -> Invoices) -- la NC-anula-ND; null hasta crearla
  Status (DebitNoteAnnulmentStatus: Pending=0 | Succeeded=1 | Failed=2)  -- sigue el CAE de la NC
  Reason (string, MaxLength 1000, NOT NULL)          -- motivo obligatorio (regla #14)
  -- snapshot espejo de la ND (regla #3/#4), foto al momento de deshacer:
  Amount (decimal(18,2))                             -- = ND.ImporteTotal
  Currency (string, MaxLength 3)                     -- ARS/DOL, espejo de PenaltyCurrencyAtEvent de la ND
  ExchangeRate (decimal(18,6)?)                      -- = ND.MonCotiz congelado (null en pesos)
  -- auditoría (regla #14, mismo patrón del módulo):
  RequestedByUserId (string, MaxLength 450)
  RequestedByUserName (string?, MaxLength 200)
  RequestedAt (DateTime)
  ArcaErrorMessage (string?, MaxLength 1000)         -- motivo de rechazo de ARCA si Status=Failed
  CreatedAt (DateTime)

  ÍNDICE ÚNICO FILTRADO chk_one_live_annulment_per_nd:
     UNIQUE (AnnulledDebitNoteInvoiceId) WHERE Status <> 2 (Failed)
     -- a lo sumo UNA anulación viva (Pending) o consumada (Succeeded) por ND. Las Failed no cuentan
     -- (se puede reintentar deshacer). Implementa idempotencia + regla dura #10 a nivel base.
```

Enum nuevo `DebitNoteAnnulmentStatus` (Domain). Migración esperada:
`Adr044_M_Undo1_AddBookingCancellationDebitNoteAnnulment` (tabla + FKs + índice único filtrado + enum).
Aditiva; `Down()` = drop table. Sin migrar datos.

**Por qué tabla hija y no escalares en el BC:** el ciclo puede repetirse; los escalares se pisarían en la
2da vuelta y perderíamos la cadena de comprobantes. La tabla historiza cada vuelta con su motivo y su par
NC↔ND. La cadena CbtesAsoc fiscal ya queda en los propios `Invoice` (NC→ND, ND→factura); esta tabla es el
índice operativo/auditoría que los ata al evento de negocio.

### 4.2 Estados nuevos del paso (enum `OperatorPenaltySituationState`, aditivo)

```
DebitNoteAnnulling         = 9   -- Done + hay una anulación Pending: "deshaciendo la multa…" (familia PROCESANDO, con polling)
DebitNoteAnnulmentFailed   = 10  -- Done + la última anulación quedó Failed: "no se pudo deshacer, reintentar"
```

Mapeo `ToOutcome`: ambos colapsan a `Confirmed` (la multa sigue confirmada mientras se deshace; sólo cambia
cuando la anulación consuma y desvincula la ND → `ConfirmedNoDebitNote` = `Confirmed`).

---

## 5. Mapa de estados del paso (contra `OperatorPenaltySituationRules`)

Se agrega a `Fields` (path mono-operador `Derive`) dos primitivos nuevos:
`HasPendingDebitNoteAnnulment`, `HasFailedDebitNoteAnnulment` (el service los calcula con una query chica a
la tabla hija). Regla nueva, sólo dentro de la rama `Confirmed + DebitNoteStatus.Issued` (hoy `Done`):

```
Confirmed + Issued + HasPendingDebitNoteAnnulment  -> DebitNoteAnnulling        (procesando, polling)
Confirmed + Issued + HasFailedDebitNoteAnnulment    -> DebitNoteAnnulmentFailed  (retry deshacer)
Confirmed + Issued (sin anulación)                  -> Done                       (ofrece "Deshacer")
```

Cuando la anulación **consuma** (`Succeeded`), el service desvincula la ND: `DebitNoteInvoiceId=null`,
`DebitNoteStatus=NotApplicable` → la regla existente ya deriva **`ConfirmedNoDebitNote`** (el estado
objetivo, con *Cobrarle la multa ahora / Corregir monto y moneda / No cobró*). Cero regla nueva para el
estado final: reusa el path que ya existe (V8+V9).

`DeriveForOperator` (multi-operador T3a) recibe los mismos dos primitivos y aplica la misma sub-regla
dentro de su rama `Confirmed + BcDebitNoteStatus.Issued`.

---

## 6. Flujo end-to-end

### 6.1 Solicitud (`UndoIssuedDebitNoteAsync`, síncrono hasta encolar la NC)

Nuevo método en `IBookingCancellationService` / `BookingCancellationService`, modelado sobre
`CorrectPenaltyAsync` (V10):

1. **Validaciones de entrada (400/409):** `reason` requerido (trim, no vacío → 400). Flag maestro
   `EnableCancellationDebitNote` OFF → 409 con mensaje neutro (misma voz que correct-penalty).
2. **Cargar el BC** (Includes: `OriginatingInvoice` + `Tributes`, `DebitNoteInvoice` + sus `InvoiceItems`,
   `Supplier`, `Reserva`). 404 si no existe.
3. **Permiso elevado** (defensa en profundidad): `userCanClassifyAgencyPenalty` (o Admin), igual gate que
   confirm/correct/retry/waive. 409 `INV-UNDO-PERM` si no.
4. **Guards duros (reglas #9/#10/#11), fuera de transacción para el 409 rápido, RE-evaluados dentro del
   lock:**
   - La multa debe estar `Confirmed` con ND vinculada y `DebitNoteStatus == Issued` y la ND con CAE
     (`DebitNoteInvoice.Resultado == "A" && CAE != null`). Si la ND está `Pending` (sin CAE) → 409
     `INV-UNDO-001` "todavía se está emitiendo; esperá o cancelá desde administración" (regla #9: sin CAE
     no hay nada fiscal que anular).
   - Si ya hay una anulación viva de esa ND (`Pending` o `Succeeded` en la tabla hija) → 409
     `INV-UNDO-002` (regla #10, no anular dos veces).
   - Si la **factura original** (`bc.OriginatingInvoice`) tiene `AnnulmentStatus == Succeeded` → **revisión
     manual**, no auto-deshacer (regla #11): se rutea con motivo claro, NO se emite NC automática.
   - **RG 4540 (regla #12): avisar, no bloquear.** Si pasaron > 15 días corridos desde `ND.IssuedAt`, se
     agrega un aviso informativo (no frena la emisión). El reloj arranca en la detección; para el MVP se
     usa `ND.IssuedAt` como referencia y el aviso es informativo (ver §10, abierto).
5. **Bajo `RunUnderParentLockAsync(bc.Id)`** (atómico, serializa contra retry/callback/correct concurrentes):
   - `ReloadAsync(bc)` + re-evaluar los guards (mismo patrón anti-carrera de `CorrectPenaltyAsync:6544`).
   - **Armar la NC-anula-ND** espejo de la ND (regla #3/#4): leer `nd.InvoiceItems`, mapearlos 1:1 a
     `InvoiceItemDto` (mismos importes, misma `AlicuotaIvaId`, misma tipificación). Construir
     `CreateInvoiceRequest`:
     ```
     ReservaId       = reserva.PublicId
     IsCreditNote    = true
     IsDebitNote     = false
     OriginalInvoiceId = ND.PublicId        // ← LA ND, nunca la factura (regla #1)
     Items           = espejo de nd.InvoiceItems
     MonId           = nd.MonId
     MonCotiz        = nd.MonCotiz           // TC congelado de la ND (regla #4), NO recotiza
     ExchangeRateSource/FetchedAt/Justification = heredados de la ND
     ```
     `CanMisMonExt` NO se setea: `CreatePendingInvoice` lo espeja del `OriginalInvoice` (la ND) igual que
     hoy la ND lo espeja de su factura (V11). La letra (13/3/8) la deriva `AfipService` sola (V1, regla #2).
   - `await _invoiceService.CreateAsync(request, userId, userName, ct)` → NC en estado Pending (CAE async).
   - **Crear la fila hija** `BookingCancellationDebitNoteAnnulment` = `{ BookingCancellationId, AnnulledDebitNoteInvoiceId = nd.Id, AnnulmentCreditNoteInvoiceId = nc.Id, Status = Pending, Reason, Amount = nd.ImporteTotal, Currency, ExchangeRate = nd.MonCotiz, RequestedBy…, RequestedAt }`.
   - **Auditoría staged** (misma SaveChanges): acción dedicada `OperatorPenaltyDebitNoteUndoRequested` con
     motivo, comprobantes vinculados (ND tipo+PtoVta+Nro+CAE; NC id), importe+moneda+TC, estado previo del
     paso. (regla #14, no exponer jerga al usuario — ver gate data-exposure.)
   - `SaveChangesAsync`.
6. El paso pasa a `DebitNoteAnnulling` (procesando). El front hace polling (reusa
   `useOperatorPenaltyPolling`, ya existe para la familia procesando).

**NO se toca la plata en la solicitud.** La reversión ocurre recién al consumar el CAE (§6.2).

### 6.2 Consumación del CAE (async, reusa el hook de `ProcessInvoiceJob`)

Cuando la NC-anula-ND resuelve en ARCA, `ProcessInvoiceJob` hoy corre, para toda NC:
`ApplyCreditNoteEconomicReversalAsync` (V5) **y** `TryReconcileLinkedCancellationDebitNoteAsync`. Cambios:

**(a) Interceptar `ApplyCreditNoteEconomicReversalAsync` (BLOQUEANTE #1, regla #5).** Al inicio del método,
después de `Include(i.OriginalInvoice)` (ya existe, línea 1680), agregar:
```
if (invoice.OriginalInvoice != null &&
    InvoiceComprobanteHelpers.Categorize(invoice.OriginalInvoice.TipoComprobante) == InvoiceComprobanteCategory.DebitNote)
{
    // Esta NC anula una Nota de Débito, no una factura: NO reversar cobros de factura
    // (eso anularía un recibo y crearía un -Payment, borrando el saldo a favor / duplicando la
    // devolución). El efecto de plata correcto es el que emerge del propio comprobante en el
    // extracto + la desvinculación de la ND (ver diseño 2026-07-14). Reconciliación aparte.
    return;
}
```
Es un único guard, mínimo, verificado. (El extracto se sana solo con la NC — V6; el saldo a favor del caso
pagado emerge del pago que queda vivo — V7.)

**(b) Nuevo reconciliador `DebitNoteAnnulmentReconciliation`** (clase pura, molde exacto de
`CancellationDebitNoteReconciliation` V3), llamado desde `ProcessInvoiceJob` junto a los otros dos:
- Buscar la fila hija por `AnnulmentCreditNoteInvoiceId == nc.Id && Status == Pending`. 0 filas → no-op
  barato (toda NC que no sea anula-ND).
- ARCA aprobó (`Resultado=="A" && CAE!=null`) → `Status = Succeeded`; y sobre el BC padre (bajo el mismo
  criterio de escritura):
  - `DebitNoteInvoiceId = null`, `DebitNoteStatus = NotApplicable`, `DebitNoteArcaErrorMessage = null`
    (desvincula la ND → el paso deriva `ConfirmedNoDebitNote`, V8; libera INV-ADR013-002, V9).
  - `nd.AnnulmentStatus = Succeeded` (marca la ND como anulada: idempotencia + evita que alguien intente
    anularla por el path estándar; el extracto la sigue mostrando compensada por su NC, V6).
  - Multi-operador (T3a): limpiar también los `line.DebitNoteStatus` de las líneas que compartían esa ND
    (vuelven a poder re-emitir). Ver §9 (alcance MVP).
  - Auditoría `OperatorPenaltyDebitNoteUndone` con el efecto (deuda revertida / saldo a favor emergente).
- ARCA rechazó (`Resultado=="R"`) → `Status = Failed`, `ArcaErrorMessage` truncado; el BC **no se toca**
  (la ND sigue viva y `Issued`) → el paso deriva `DebitNoteAnnulmentFailed` (retry deshacer).
- `Resultado` PENDING/null → no-op (sigue en vuelo).

**Concurrencia:** el reconciliador escribe el BC → correrlo bajo `RunUnderParentLockAsync(bc.Id)` (o el
patrón `own SaveChanges` del reconciliador existente si InMemory). Sin segundo recurso → sin deadlock.

---

## 7. Contrato API

**Endpoint nuevo** (mismo estilo que correct-penalty/retry):
```
POST /api/cancellations/{publicId:guid}/undo-debit-note
Body: { "reason": string }        // requerido, no vacío
```
- **Permiso:** `[Authorize]` + `ReservasCancel` para llegar + `CancellationsClassifyAgencyPenalty` (o
  Admin) resuelto server-side + ownership. Igual gate que confirm/correct/retry/waive.
- **200:** `BookingCancellationDto` (con el paso ya en `DebitNoteAnnulling`).
- **404:** BC no existe.
- **409:** `INV-UNDO-PERM` (sin permiso) / `INV-UNDO-001` (no hay ND con CAE para deshacer) /
  `INV-UNDO-002` (ya hay una anulación en curso o consumada) / `INV-UNDO-MANUAL` (factura original ya
  anulada → revisión manual) / flag OFF. Mapeo por `GlobalExceptionHandler` (mismo criterio que
  correct-penalty).
- **Aviso RG 4540:** campo informativo en el DTO (no bloquea).

Request DTO: `UndoDebitNoteRequest { string Reason }`. El DTO de situación
(`OperatorPenaltySituationDto`) suma los tokens `DebitNoteAnnulling` / `DebitNoteAnnulmentFailed`; el front
mapea a castellano ("Deshaciendo la multa…" / "No se pudo deshacer, reintentar"). **Nunca** exponer el
enum crudo, IDs, ni texto de ARCA al usuario final (gate data-exposure).

---

## 8. Efecto en la plata, por moneda y por estado de cobro

| Caso | Qué pasa | Mecánica |
|------|----------|----------|
| **Impaga** | La deuda de la multa desaparece. | La NC (abono con CAE) compensa el cargo de la ND en el extracto (V6). El cartel deja de contar la ND (desvinculada) → `ConfirmedNoDebitNote` con pendiente re-ofrecible. Cero `Payment` nuevo. |
| **Pagada** | Queda **saldo a favor reutilizable** (ADR-036), sin devolución física automática. | El pago de la multa queda vivo; la obligación desaparece; `ComputePendingPenaltyForDisplay` netea → pendiente 0 y el saldo por moneda queda a favor. La devolución física (si el cliente la pide) es un paso posterior separado. |
| **Parcialmente pagada** | La parte pagada → saldo a favor; la parte impaga → deja de ser deuda. | Es la combinación natural de las dos filas de arriba: el netting por moneda (V7) ya reparte. **No hay lógica especial**: el pendiente = `Min(gross, balance)` clamp 0 se recalcula solo tras desvincular la ND. |
| **USD (TC congelado)** | La anulación neta a cero en la moneda del comprobante; no genera diferencia de cambio nueva. | La NC hereda `MonCotiz`/`CanMisMonExt` de la ND (regla #4). Si la multa se cobró en ARS a otro TC, la dif. de cambio realizada de ese cobro se trata al devolver/convertir el saldo → **asiento contable, sin ND fiscal** (pendiente contador, §10). |

**Invariante de plata (BLOQUEANTE #1):** la NC-anula-ND **jamás** debe pasar por
`ApplyTotalCreditNoteReversalAsync`/`ApplyPartialCreditNoteReversalAsync` (§6.2.a). Test obligatorio
que lo fija (§9).

Anti-doble-conteo (regla #8): tras deshacer + re-emitir conviven ND_vieja (anulada, `AnnulmentStatus=Succeeded`,
**desvinculada**) + ND_nueva (viva, vinculada en `DebitNoteInvoiceId`). El cartel usa el escalar
`DebitNoteInvoiceId` → cuenta sólo la ND_nueva. El extracto muestra ND_vieja(cargo)+NC(abono)=0 + ND_nueva(cargo).
Correcto.

---

## 9. Tests obligatorios

**Unit (dominio, sin DB):**
1. `OperatorPenaltySituationRules.Derive`: `Confirmed+Issued+PendingAnnulment → DebitNoteAnnulling`;
   `+FailedAnnulment → DebitNoteAnnulmentFailed`; sin anulación → `Done`. Y `DeriveForOperator` idem.
2. Tras desvincular (`DebitNoteInvoiceId=null`, `NotApplicable`) → `ConfirmedNoDebitNote` (regresión del
   estado objetivo).
3. `DebitNoteAnnulmentReconciliation`: A+CAE → Succeeded + desvincula + `nd.AnnulmentStatus=Succeeded`;
   R → Failed + BC intacto; PENDING → no-op. (molde de `CancellationDebitNoteReconciliationTests`.)

**Unit/Integración (BLOQUEANTE #1, el más importante):**
4. **`ApplyCreditNoteEconomicReversalAsync` con una NC cuyo `OriginalInvoice` es una ND → NO crea ningún
   `Payment` de reversión y NO anula ningún recibo.** (contra-test: la misma NC contra una factura SÍ
   los crea — regresión de que el guard es específico.)
5. Caso pagado: multa cobrada → deshacer con CAE → NO se voidea el recibo del cobro de la multa; el saldo
   por moneda queda a favor por el importe de la multa; `pendiente == 0`.
6. Caso impago: deshacer con CAE → la deuda de la multa deja de figurar; `ConfirmedNoDebitNote`.

**Integración (flujo):**
7. Emitir ND (CAE) → deshacer → la NC sale con la letra espejo (ND C 12 → NC 13) y `OriginalInvoiceId`
   apuntando a la ND (CbtesAsoc → ND), mismo importe/moneda/`MonCotiz`/`CanMisMonExt`.
8. Idempotencia/concurrencia: doble `POST undo-debit-note` casi simultáneo → una sola fila hija Pending
   (índice único filtrado + parent lock); el segundo → 409 `INV-UNDO-002`.
9. Deshacer con ND aún `Pending` (sin CAE) → 409 `INV-UNDO-001`, no emite NC.
10. Deshacer con anulación ya `Succeeded` → 409 `INV-UNDO-002`.
11. Factura original ya anulada del todo → `INV-UNDO-MANUAL` (revisión manual), no auto-deshacer.
12. Ciclo emitir→deshacer→re-emitir→deshacer: dos vueltas dejan dos filas hija + la cadena de comprobantes;
    el cartel cuenta sólo la ND viva (anti-doble-conteo).
13. Extracto: tras deshacer con CAE, la ND y la NC-anula-ND aparecen como cargo+abono que netean 0.
14. Data-exposure: los mensajes 409 y el token del paso no filtran enum/ID/texto de ARCA
    (molde `CancellationErrorMessageLeakUnitTests`).

**RG 4540:** test de que > 15 días desde `ND.IssuedAt` produce el aviso informativo pero NO bloquea.

---

## 10. Alcance MVP y qué queda anotado (no ignorado)

**Entra ahora:**
- Deshacer una ND `Issued` con CAE (mono-operador y multi-operador que comparten la ND del BC padre).
- Emisión de la NC-anula-ND espejo (letra/importe/moneda/TC/CanMisMonExt), CbtesAsoc→ND.
- Reversión de plata correcta (intercept del bloqueante #1; deuda baja / saldo a favor emergente).
- Vuelta del paso a `ConfirmedNoDebitNote`; ciclo repetible; auditoría con motivo; aviso RG 4540.
- Estados `DebitNoteAnnulling` (procesando+polling) y `DebitNoteAnnulmentFailed` (retry deshacer).

**Queda anotado (fuera de MVP):**
- **Reloj RG 4540 desde la "detección" real** (spec: el hecho documentable es cuándo se detecta el error,
  distinto del reloj de la anulación del viaje). MVP usa `ND.IssuedAt` como referencia del aviso; afinarlo
  a un timestamp de detección propio es refinamiento posterior. **[IR]**
- **Devolución física del saldo a favor** (bancarización Ley 25.345 si supera umbral): paso posterior
  separado y explícito, no automático.
- **Diferencia de cambio realizada** cuando la multa USD se cobró en ARS a otro TC: asiento contable sin
  ND fiscal — pendiente de firma del contador (§ riesgos).
- **IIBB provincial** de la ND (si devengó): su anulación debería ajustar esa base; no modelado, depende de
  la jurisdicción. Pendiente contador.
- **Freno de negocio "cliente en disputa"** (regla #4 de la spec, no fiscal): decisión de Gastón, no MVP.

---

## 11. Qué NO tocar (para no romper lo sano)

- `EnqueueAnnulmentAsync` / `ProcessAnnulmentJob` (V4): el deshacer NO pasa por ahí; su rechazo de ND/NC/M
  se mantiene. Los casos legacy 3→2/8→7/13→12 de `ProcessAnnulmentJob` NO se tocan.
- `ReservaAccountStatementBuilder` / `AddInvoiceLines` (V6): el extracto se auto-sana; cero cambios.
- `ApplyTotalCreditNoteReversalAsync` / `ApplyPartialCreditNoteReversalAsync` / `CalculateCreditNoteReversalCapAsync`:
  su lógica de reversar cobros de **factura** queda intacta; sólo se agrega el guard de salida temprana en
  `ApplyCreditNoteEconomicReversalAsync` (§6.2.a) para que la NC-anula-ND no entre.
- `LiveDebitNotePredicate` / `ComputePendingPenaltyForDisplay`: no cambian; la desvinculación de la ND ya
  produce el estado y el netting correctos.
- La regla firmada "el TC del comprobante es el congelado del original": la NC hereda el `MonCotiz` de la ND
  (regla #4), nunca recotiza.

---

## 12. Riesgos y confirmaciones pendientes (para Gastón / reviewer / contador)

- **[NV — RIESGO]** Comportamiento literal de WSFEv1 al validar el `MonCotiz` congelado de la NC-anula-ND
  contra la banda oficial del día, y al aceptar/rechazar por el plazo de 15 días. Mismo test de homologación
  pendiente que ya arrastran la NC total y la ND; no es nuevo, sigue sin cerrar.
- **[IR]** Punto de asociación NC→ND (elegido porque el error vive en la ND). Es criterio fundado, no norma
  explícita; si un matriculado prefiere NC→factura, es cambio de una línea (`OriginalInvoiceId`) pero cambia
  toda la cadena CbtesAsoc. Confirmar con contador.
- **`Necesita confirmación profesional contable`:** cuenta exacta del saldo a favor cuando la multa estaba
  pagada, y tratamiento de la diferencia de cambio realizada al reversar un cobro en ARS de una multa USD.
- **`Necesita confirmación: jurisdicción`:** IIBB de la ND anulada.
- **[IR] Concurrencia** cubierta por `RunUnderParentLockAsync(bc.Id)` + índice único filtrado; validar en
  integración Postgres (InMemory no serializa).

**Gates antes de mergear:** `software-architect-reviewer` (challenge de este diseño) →
`travel-agency-accountant-argentina` (fórmula de plata + reglas fiscales) → backend/frontend + reviewers →
`security-data-risk-reviewer` + `data-exposure-reviewer` (mensajes/tokens nuevos) → `qa-automation`.
Gate UX con Gastón para el botón "Deshacer" y los carteles nuevos ANTES de implementar el front.

---

## Desafío (software-architect-reviewer, 2026-07-14) — APROBADO CON CAMBIOS

3 bloqueantes + 4 mejoras mayores. Esencial:

- **B1 (multa PAGADA):** la promesa "saldo a favor reutilizable" NO se cumplía: el balance negativo de la
  reserva NO es un bolsillo `ClientCreditEntry` consumible. Decisión tomada (deriva de ADR-036): opción (a)
  — al consumarse la NC de anulación con una multa que estaba pagada (total o parcial), **acuñar un
  `ClientCreditEntry`** por lo efectivamente cobrado de la multa, en su moneda, reusando el mecanismo real
  y espejándolo con origen propio "multa deshecha". Especificar el caso parcial.
- **B2 (multi-operador):** NO barrer `line.DebitNoteStatus` de todo el BC. Resetear SOLO las líneas cuyos
  cargos alimentaron LA ND anulada; JAMÁS tocar una línea `ManualReview` (es la ND complementaria de otro
  operador — perderla = multa que nunca se cobra). Test obligatorio. Si hay ambigüedad irresoluble →
  bloquear con "la tiene que revisar una persona" (anotar para la tanda multi-operador futura).
- **B3:** el reconciliador que desvincula la ND toma `RunUnderParentLockAsync(bc.Id)` SIEMPRE en Postgres
  (own-SaveChanges solo como fallback de tests InMemory).
- **M1:** el front shipea JUNTO (obligatorio); declarar la ventana DTO-viejo.
- **M2:** el guard va inmediatamente después del `Include` de `OriginalInvoice`, con comentario explícito.
- **M3:** verificar los lectores de `Invoice.AnnulmentStatus` para una ND `Succeeded` y documentar.
- **M4:** rastro visible del ciclo emitir→deshacer→re-emitir, patrón del rastro de waived, gate UX.
- Extra: test de que ningún cálculo de deuda suma ND directo (todo por el escalar del BC); test de
  especificidad del guard (NC contra factura SIGUE creando reversal); idempotencia del reconciliador ante
  retry de Hangfire.

---

## REVISIÓN 2 (2026-07-14) — cierre de los 3 bloqueantes + 4 mejoras

Código real re-inspeccionado para decidir: `ClientCreditEntry.cs` (íntegro),
`ClientCreditService.CreateEntryAsync` (`:140-186`), `PaymentService.ConvertOverpaymentToClientCreditAsync`
(minteo sin allocation, `:848`), `BookingCancellationLineOperatorCharge.cs:118` (`TargetInvoiceId`),
`ReservaMoneyCalculator.cs` (NO tiene términos de multa), `InvoiceService.cs` (lectores de `AnnulmentStatus`:
`:221, 249-251, 402-404, 772-776, 1271-1370`), `ReservaService.DeriveCancelledMoneyContextAsync` (`:5160+`).

### B1 — acuñar un `ClientCreditEntry` de origen "multa deshecha" (caso pagado / parcial)

**Hecho verificado que habilita la solución limpia:** `ClientCreditEntry` YA soporta un origen SIN
`OperatorRefundAllocation` — el de **sobrepago** (ADR-022 §4.9): `OperatorRefundAllocationId` es nullable
(`:39`), la traza va por `SourcePaymentId`/`SourceReservaId`/`CreatedByUserId` (`:68-81`), y el minteo NO
pasa por `CreateEntryAsync` (que exige allocation no-null, `:150-160`) sino por
`ConvertOverpaymentToClientCreditAsync` (`PaymentService.cs:848`). Ese es el mecanismo real a espejar.

**Decisión (opción a, cerrada por el reviewer):**
1. **Tercer origen en `ClientCreditEntry`:** columna nueva `SourceDebitNoteAnnulmentId (int?, FK ->
   BookingCancellationDebitNoteAnnulment)`, nullable, junto a los discriminadores existentes
   (allocation / sobrepago). Migración aditiva `Adr044_M_Undo2_AddDebitNoteUndoOriginToClientCreditEntry`,
   sin backfill. Comentario cruzado en la entidad: "3 orígenes — cancelación (allocation), sobrepago,
   multa deshecha".
2. **Minteo nuevo `ClientCreditService.CreateEntryFromDebitNoteUndoAsync(...)`**, espejo de
   `ConvertOverpaymentToClientCreditAsync` (NO de `CreateEntryAsync`): construye el entry con
   `OperatorRefundAllocationId = null`, **`BookingCancellationId = null`** (a propósito — ver abajo),
   `SourceReservaId = reserva.Id`, `SourceDebitNoteAnnulmentId = annulment.Id`,
   `Currency = annulment.Currency`, `CreditedAmount = RemainingBalance = collectedPenaltyPortion`,
   `CreatedByUserId/Name = actor`. NO hace `SaveChanges` (lo commitea el reconciliador en su unidad).
3. **`BookingCancellationId = null` es deliberado (verificado):** el guard B5 de FC1.2
   (`OnAllCreditConsumedAsync`, `docs/…/2026-05-18-fc1-2-implementacion.md:187`) cierra el BC cuando TODOS
   sus `ClientCreditEntry` llegan a 0. Si atáramos este crédito al BC, gastarlo cerraría el BC — pero el
   "deshacer" deja el paso ABIERTO (`ConfirmedNoDebitNote`), no lo cierra. Mismo criterio que el crédito de
   sobrepago, que también deja `BookingCancellationId = null` para NO disparar B5. El BC se traza igual vía
   `annulment.BookingCancellationId` en la fila hija.
4. **Monto acuñado (porción efectivamente cobrada de la multa):**
   `collectedPenaltyPortion = max(0, PenaltyAmountAtEvent − pendingPenalty)`, donde `pendingPenalty` es lo
   que `ComputePendingPenaltyForDisplay(grossPenalty, penaltyCurrencyIso, balanceByCurrency)` devuelve
   **inmediatamente antes** de desvincular la ND (impaga → pending=gross → collected=0, no se acuña nada;
   pagada total → pending=0 → collected=gross; **parcial → collected = gross − balance**). El resto impago
   NO se acuña: simplemente deja de ser deuda (la NC lo compensa en el extracto).
5. **Idempotencia dura (retry de Hangfire + doble-minteo):** el minteo se guardea con
   "no existe ya un `ClientCreditEntry` con `SourceDebitNoteAnnulmentId == annulment.Id`". Como el
   reconciliador puede re-correr (Hangfire), este guard garantiza **a lo sumo un crédito por evento de
   deshacer**. (Se puede reforzar con índice único filtrado sobre `SourceDebitNoteAnnulmentId`.)

**GATE DE VERIFICACIÓN BACKEND — DURO, antes de implementar (riesgo de doble-minteo, honesto):** hay que
confirmar en código cómo se cobra hoy una multa y si ese cobro **ya** dispara
`ConvertOverpaymentToClientCreditAsync` (porque la multa NO está en `ReservaMoneyCalculator` — verificado:
ese calculador solo suma servicios —, así que un pago de multa sobre una reserva anulada puede verse como
sobrepago y convertirse en crédito EN EL MOMENTO DEL PAGO). Dos posibilidades y su tratamiento:
- **(i) el pago de la multa YA se convirtió en `ClientCreditEntry` de origen sobrepago:** entonces el
  dinero del cliente YA está protegido como bolsillo consumible; el "deshacer" **NO debe re-acuñar** (sería
  doble). En ese caso `collectedPenaltyPortion` efectivo = 0 para el minteo, y sólo se registra la traza.
- **(ii) el pago quedó como saldo negativo de la reserva sin bolsillo:** entonces el "deshacer" SÍ acuña
  (opción a). Este es el caso que B1 vino a cerrar.
El diseño soporta ambos: el minteo se calcula como `max(0, collectedPenaltyPortion − créditosYaExistentes
DeEsaReservaYMonedaSinConsumir)`. **El QA fiscal + backend deben fijar cuál de (i)/(ii) es el real con un
test antes de shipear** (no se asume). El invariante que NO se negocia: tras deshacer una multa pagada, el
cliente termina con **exactamente** el saldo a favor de lo que pagó de multa — ni de más, ni de menos.

### B2 — reset de `line.DebitNoteStatus` acotado a la ND anulada; nunca tocar `ManualReview`

**Hecho verificado:** `line.DebitNoteStatus == ManualReview` (marcador T3a, `OperatorPenaltySituationRules.cs:104-114`)
significa "este operador confirmó DESPUÉS de que la ND del principal ya había salido → necesita una ND
complementaria a mano". Por definición **esa línea nunca entró a la ND que estamos anulando**. Resetearla =
borrar una multa complementaria pendiente = multa que jamás se cobra. Prohibido.

**Criterio EXACTO de reset (en el reconciliador, al consumar la anulación):**
- Reset (`line.DebitNoteStatus = NotApplicable`) SOLO en las líneas de este BC que estuvieron EN la ND
  anulada:
  - **T3b (cargos con `TargetInvoiceId`):** líneas cuyos cargos trasladables (`Kind != Withholding`) tienen
    `OperatorCharge.TargetInvoiceId == nd.Id` (verificado que el campo existe, `:118`).
  - **T3a legacy (cargos sin `TargetInvoiceId`):** la ND única del BC (`bc.DebitNoteInvoiceId == nd.Id`) →
    las líneas confirmadas del BC que NO estén en `ManualReview`.
- **JAMÁS** tocar una línea con `DebitNoteStatus == ManualReview` (es la ND complementaria de otro operador,
  ajena a esta ND).
- **Guard conservador (ambigüedad irresoluble):** si la ND anulada mezcla cargos de 2+ operadores Y hay al
  menos una línea `ManualReview` cuyo vínculo con la ND no se puede determinar con `TargetInvoiceId` (cargos
  legacy sin ese campo) → **bloquear el deshacer** con `INV-UNDO-MULTIOP` "esta multa la tiene que revisar
  una persona", y anotarlo como parte de la tanda multi-operador futura. Nunca resetear a ciegas.
- **Test obligatorio (B2):** BC con operador A (en la ND anulada) + operador B (`ManualReview`, ND
  complementaria pendiente) → deshacer la ND de A **NO** altera el `DebitNoteStatus` de la línea de B.

### B3 — el reconciliador que desvincula toma `RunUnderParentLockAsync(bc.Id)` SIEMPRE en Postgres

`DebitNoteAnnulmentReconciliation` (§6.2.b), cuando transiciona `Succeeded` y escribe el BC (desvincular ND
+ reset de líneas B2 + minteo B1 + auditoría), corre **envuelto en `RunUnderParentLockAsync(bc.Id)`** en
Postgres (mismo `SELECT … FOR UPDATE` + `lock_timeout` que confirm/correct/retry). El own-`SaveChanges` del
reconciliador queda documentado **SOLO** como fallback del proveedor InMemory de los tests (donde
`RunUnderParentLockAsync` ya cae a ejecutar el cuerpo directo, `:4588-4592`). Sin alternativa a criterio del
implementador. La idempotencia del minteo (B1.5) + este lock hacen el reconciliador seguro ante retry de
Hangfire concurrente.

### M1 — el front shipea JUNTO (obligatorio) + ventana DTO-viejo declarada

Backend y frontend salen en la MISMA tanda. Front:
- `DebitNoteAnnulling` → familia **"procesando"** con polling (reusa `useOperatorPenaltyPolling` +
  `OperatorPenaltyStepPanel`), cartel "Deshaciendo la multa…".
- `DebitNoteAnnulmentFailed` → familia **"acción trabada"** con botón **Reintentar** (reusa el patrón de
  `DebitNoteFailed`).
- Botón **"Deshacer esta multa"** en la familia `Done` → modal con **motivo obligatorio** (mismo componente
  de motivo que correct-penalty).
- **Ventana DTO-viejo (degradación sin romper):** si por orden de deploy el front viejo recibe un token
  `DebitNoteAnnulling`/`DebitNoteAnnulmentFailed` que no conoce, el panel debe degradar a "paso activo sin
  acción" (mostrar el estado genérico "en proceso", nunca romper el render). El mapa del front trata todo
  token desconocido como estado informativo neutro (ya es el patrón defensivo de `operatorPenaltyBanner.js`).

### M2 — el guard del intercept va inmediatamente después del `Include` de `OriginalInvoice`

En `ApplyCreditNoteEconomicReversalAsync`, el guard de salida temprana (§6.2.a) se coloca **justo después**
del `.Include(i => i.OriginalInvoice)` existente (`AfipService.cs:1680`) y **antes** de la detección
parcial/total, con comentario explícito: "esta NC anula una Nota de Débito, no una factura → NO reversar
cobros de factura (anularía un recibo y crearía un −Payment, borrando el saldo a favor / duplicando la
devolución). La reversión económica correcta la hace `DebitNoteAnnulmentReconciliation`; acá se sale sin
tocar cobros. Ver diseño 2026-07-14."

### M3 — lectores de `Invoice.AnnulmentStatus` para una ND: DECISIÓN = NO setear `AnnulmentStatus` en la ND

Verifiqué los lectores y el resultado **cambia la recomendación de la Rev 1** (que proponía
`nd.AnnulmentStatus = Succeeded`):
- **Extracto** (`AddInvoiceLines`) y **cuadre de facturación** (`ReservaInvoicingCuadreCalculator`, vía
  `CountsInNetBilled(Resultado)`) cuentan por `Resultado`, **NO** por `AnnulmentStatus` (`ReservaService.cs:221`,
  `:2538`). La ND sigue como cargo y la NC como abono sin tocar `AnnulmentStatus`.
- **Guard "una PENDING in-flight por reserva"** (`InvoiceService.cs:402-404`) filtra `AnnulmentStatus !=
  Succeeded` pero SOLO sobre `Resultado == "PENDING"`; nuestra ND está en `"A"` → no la toca.
- **RetryEmission** (`:772-776`) bloquea reintentar un comprobante `Succeeded`, pero nadie reintenta la
  emisión de una ND por ahí.
- **`EnqueueAnnulmentAsync`/`ProcessAnnulmentJob`:** la ND ni entra (`IsSupportedForAnnulment` la rechaza).
- **DTO** (`:221`) expone `AnnulmentStatus.ToString()`: si lo marcáramos `Succeeded`, la ND aparecería como
  "anulada" en su propia ficha/lista.

**Conclusión:** setear `nd.AnnulmentStatus = Succeeded` es **innecesario** (ningún número de plata lo
necesita) y solo agrega ruido en lectores keyed por `AnnulmentStatus`. **Decisión: NO tocar
`AnnulmentStatus` de la ND.** El registro de que "esta ND fue anulada" vive en la fila hija
`BookingCancellationDebitNoteAnnulment` (Status=Succeeded) + la NC en el extracto. Se elimina el paso
"`nd.AnnulmentStatus = Succeeded`" del §6.2.b de la Rev 1.

### M4 — rastro visible del ciclo emitir→deshacer→re-emitir (por default, gate UX)

El DTO de situación de multa incluye el historial de anulaciones (de la tabla hija: fecha, motivo). La
ficha muestra, por default, una línea chica bajo el paso (patrón del rastro de "waived"):
"Se deshizo el comprobante anterior el {fecha} — motivo: {motivo}". Contemplado por default (no configurable,
no una pregunta), a **validación del gate UX con Gastón** antes de implementar el front. No expone jerga
interna (número de ND en formato usuario, nunca IDs/enum).

### Tests añadidos en Rev 2 (se suman a los §9)

15. **B1 pagada:** multa cobrada total → deshacer con CAE → se acuña UN `ClientCreditEntry`
    (`SourceDebitNoteAnnulmentId` seteado, `OperatorRefundAllocationId`/`BookingCancellationId` null) por el
    importe de la multa, en su moneda, consumible (aparece en el saldo a favor del cliente).
16. **B1 parcial:** multa cobrada a medias → se acuña crédito SOLO por la porción cobrada
    (`gross − balance`); la porción impaga no genera crédito.
17. **B1 idempotencia:** re-correr el reconciliador (retry de Hangfire) NO acuña un segundo crédito para el
    mismo `annulment.Id`.
18. **B1 no-double-mint:** si el pago de la multa ya había generado un crédito de sobrepago, el deshacer no
    acuña de más (resuelve el gate de verificación (i)/(ii) con un test explícito).
19. **B2:** deshacer la ND del operador A no altera el `DebitNoteStatus == ManualReview` de la línea del
    operador B; el guard conservador `INV-UNDO-MULTIOP` bloquea el caso ambiguo.
20. **B3:** el reconciliador de anulación corre bajo el lock del BC en integración Postgres (estilo
    `Adr042MultiInvoiceConcurrency`).
21. **M2 especificidad del guard (contra-test):** una NC contra una FACTURA (no ND) SIGUE creando su
    `CreditNoteReversal` (el guard es específico, no rompe la anulación normal de facturas).
22. **Ningún cálculo de deuda suma ND directo:** test de que la deuda de multa se lee del escalar del BC
    (`DebitNoteInvoiceId`/`PenaltyAmountAtEvent` vía `LiveDebitNotePredicate`), nunca sumando `Invoice`
    de tipo ND directo — así conviven ND_vieja(anulada)+ND_nueva sin doble conteo.

---

## Lista final por archivo / método (para backend-dotnet-senior + frontend-senior)

**Dominio (`TravelApi.Domain`):**
- `Entities/DebitNoteAnnulmentStatus.cs` (NUEVO enum: Pending/Succeeded/Failed).
- `Entities/BookingCancellationDebitNoteAnnulment.cs` (NUEVA entidad, §4.1).
- `Entities/ClientCreditEntry.cs`: + `SourceDebitNoteAnnulmentId (int?)` + navegación (tercer origen, B1).
- `Reservations/OperatorPenaltySituationState.cs`: + `DebitNoteAnnulling = 9`, `DebitNoteAnnulmentFailed = 10`.
- `Reservations/OperatorPenaltySituationRules.cs`: `Fields`/`LineFields` + `HasPendingDebitNoteAnnulment`,
  `HasFailedDebitNoteAnnulment`; sub-regla dentro de la rama `Confirmed + Issued` (§5); `ToOutcome` → Confirmed.

**Infraestructura (`TravelApi.Infrastructure`):**
- `Persistence/AppDbContext.cs`: `DbSet` + config de la entidad nueva (FKs, índice único filtrado
  `WHERE Status <> Failed`) + FK de `ClientCreditEntry.SourceDebitNoteAnnulmentId`.
- Migraciones (aditivas, en orden):
  `Adr044_M_Undo1_AddBookingCancellationDebitNoteAnnulment`,
  `Adr044_M_Undo2_AddDebitNoteUndoOriginToClientCreditEntry`.
- `Services/BookingCancellationService.cs`:
  - `UndoIssuedDebitNoteAsync(publicId, reason, userId, userName, ct, userCanClassifyAgencyPenalty)` (§6.1),
    molde de `CorrectPenaltyAsync` (`:6458`); bajo `RunUnderParentLockAsync(bc.Id)`; arma la NC vía
    `_invoiceService.CreateAsync` (IsCreditNote, OriginalInvoiceId=ND); crea la fila hija; auditoría staged.
  - Guards `EnsureUndoAllowed…` (reglas #9/#10/#11 + guard conservador multi-operador B2).
  - Cálculo de `HasPending/FailedDebitNoteAnnulment` al mapear la situación del paso (query chica a la hija).
  - Reset de líneas B2 en el reconciliador (criterio `TargetInvoiceId == nd.Id`).
- `Services/DebitNoteAnnulmentReconciliation.cs` (NUEVO, molde de `CancellationDebitNoteReconciliation.cs`):
  al resolver la NC → Succeeded (desvincular ND: `DebitNoteInvoiceId=null`, `DebitNoteStatus=NotApplicable`,
  limpiar error; reset líneas B2; minteo B1 vía `ClientCreditService`; auditoría) / Failed (Status=Failed +
  ArcaError, BC intacto). Envuelto en `RunUnderParentLockAsync(bc.Id)` en Postgres (B3).
- `Services/AfipService.cs`: guard de intercept en `ApplyCreditNoteEconomicReversalAsync` justo tras el
  `Include(OriginalInvoice)` (§6.2.a / M2); llamar `DebitNoteAnnulmentReconciliation` en `ProcessInvoiceJob`
  junto a los otros reconciliadores (líneas ~1568/1573 y ~2649/2655).
- `Services/ClientCreditService.cs`: `CreateEntryFromDebitNoteUndoAsync(...)` (B1, espejo de
  `ConvertOverpaymentToClientCreditAsync`, sin allocation, `BookingCancellationId=null`).

**Aplicación (`TravelApi.Application`):**
- `DTOs/OperatorPenaltySituationDto.cs`: + tokens nuevos + historial de anulaciones (M4).
- `DTOs/…/UndoDebitNoteRequest.cs` (NUEVO: `{ string Reason }`).
- `Interfaces/IBookingCancellationService.cs`: firma de `UndoIssuedDebitNoteAsync`.

**API (`TravelApi`):**
- `Controllers/CancellationsController.cs`: `POST {publicId:guid}/undo-debit-note` (§7), mismo gate de
  permiso/ownership que correct-penalty; mapeo de errores 404/409 (`INV-UNDO-*`).

**Frontend (`TravelWeb`, shipea JUNTO — M1):**
- `features/cancellations/operatorPenaltyBanner.js` + `components/OperatorPenaltyStepPanel.jsx`: mapear
  `DebitNoteAnnulling` → procesando+polling; `DebitNoteAnnulmentFailed` → acción trabada + Reintentar;
  botón "Deshacer esta multa" en `Done` → modal motivo; rastro M4; degradación de token desconocido.
- `features/cancellations/api/cancellationsApi.js`: `undoDebitNote(publicId, reason)`.
- `hooks/useOperatorPenaltyPolling.js`: incluir `DebitNoteAnnulling` como estado que sigue en polling.

**Tests:** los §9 (1-14) + Rev 2 (15-22). Gate fiscal + gate UX + reviewers de seguridad/exposición como
en §12.
