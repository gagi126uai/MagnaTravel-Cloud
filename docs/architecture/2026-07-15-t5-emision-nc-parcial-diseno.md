# Diseño — "T5-emisión": emisión real de la Nota de Crédito de una anulación PARCIAL + pantalla de confirmación

Fecha: 2026-07-15. Autor: software-architect. Estado: **propuesto, pendiente de
`software-architect-reviewer` + gate UX (Gastón) + gate fiscal (contador) + reviewers de seguridad/exposición**.
NO implementar hasta review verde.

Base fáctica: código real inspeccionado (commit `dca101c`, HEAD). Molde de forma:
`docs/architecture/2026-07-14-deshacer-multa-emitida-diseno.md`. Fuente fiscal:
`docs/fiscal/2026-07-13-investigacion-multas-3-temas.md` (leída entera).

---

## 1. Qué se pide (flujo de producto)

Hoy, cancelar UN servicio de una reserva facturada deja el crédito **capturado pero no emitido**: la línea
del `BookingCancellation` queda con su factura destino y su monto bruto congelado, el BC queda `Drafted`, y
aparece en la bandeja "Comprobantes por resolver" como **"Pendiente de emisión"**. Falta el último paso: que
el back-office **confirme y emita la Nota de Crédito real** contra ARCA por ese servicio anulado, dejando la
factura de venta **viva por el resto** de los servicios.

Esta tanda cubre: la pantalla de confirmación (contrato), la emisión real por el pipeline existente, la
reconciliación al obtener CAE, el efecto de plata, y los 3 bloqueantes técnicos ya documentados.

---

## 2. Hechos verificados en el código (base fáctica, no supuestos)

| # | Hecho | Evidencia |
|---|-------|-----------|
| V1 | La captura T5 ya persiste, por línea, `TargetInvoiceId` (a qué factura de venta va el crédito) y `ConfirmedGrossCreditAmount` (monto BRUTO congelado, criterio matriculado 2026-06-01). El monto NO se recalcula: se muestra. | `BookingCancellationLine.cs:202-238`. |
| V2 | El cap por remanente ya existe y ya está probado: `ComputeInvoiceRemainingCreditableAmountAsync` descuenta (A) las hijas ADR-042 con NC vinculada/Pending y (B) las reservas por-línea T5 con monto confirmado, con anti-doble-conteo línea-vs-hija (`bcIdsWithEmittedChild`). | `BookingCancellationService.cs:4886-4960`. |
| V3 | El guard de moneda ya vive en la captura: si la moneda de la línea no mapea a la moneda ARCA de la factura destino (`ArcaCurrencyMapper`), o el TC de una factura extranjera es incoherente (cotiz 1), la línea queda resuelta SIN monto → pendiente de resolución manual, jamás inventa TC (INV-118/120). | `Adr044T5PartialCancellationTests.cs:856-905` (FRENTE A). |
| V4 | La bandeja "Comprobantes por resolver" ya lista los T5: BC `Drafted` con ≥1 línea `Partial` y NINGUNA `Full`; monto = SUM de líneas Partial resueltas; etiqueta saneada "Pendiente de emisión". Excluye un `Drafted` con línea `Full` (anular-total en curso). | `BookingCancellationService.cs:4320-4408`. |
| V5 | **BLOQUEANTE #3 (verificado, corazón de la tanda):** el pipeline FC1.3 de NC parcial (`EnqueuePartialCreditNoteAsync` → `EmitPartialCreditNoteAsync`) marca la **factura de venta** `AnnulmentStatus = Pending` al encolar (`:2057`) y `AnnulmentStatus = Succeeded` al conseguir CAE (`:2594`, `:2935`). Eso es correcto para el FC1.3 legacy (se anula la reserva ENTERA, con NC de valor parcial por multas/no-reintegrables), pero **letal para T5**: la factura tiene que seguir viva para los servicios que NO se cancelaron. | `InvoiceService.cs:2057, 2594, 2935`. |
| V6 | El estado `AnnulmentStatus.Succeeded` de una factura la saca de "viva": el gate de cobranza/facturación (`hasLiveCae`) y los guards de mutación (`MutationGuards.HasLiveCaeForReserva`) leen `!IsCreditNote && CAE != null && AnnulmentStatus != Succeeded`. Marcar Succeeded en una parcial apagaría cobro/facturación/corrección del resto. | `ReservaService.cs:2645-2648, 1249, 1810-1816, 2554, 2648, 4198`; `AnnulmentStatus.cs:15-18`. |
| V7 | El extracto y el cuadro de facturación NO leen `AnnulmentStatus`: cuentan por `Resultado=="A"` con la regla `CountsInNetBilled` (factura suma, NC resta). Una NC parcial con CAE **ya** compensa su parte en el extracto sin tocar `AnnulmentStatus`. | `ReservaService.cs:248-259`. |
| V8 | La reversión económica de una NC ya distingue parcial vs total: `ApplyCreditNoteEconomicReversalAsync` → si el BC (por `CreditNoteInvoiceId==nc.Id`) tiene `CreditNoteKind==PartialOnOriginal` → `ApplyPartialCreditNoteReversalAsync` (NO cascade-void de recibos; capea contra lo REALMENTE cobrado en esa moneda). Sin BC → fallback por monto: `nc.ImporteTotal < OriginalInvoice.ImporteTotal` ⇒ parcial. | `AfipService.cs:1689-1759, 1773-1785, 2112`. |
| V9 | La reconciliación en `ProcessInvoiceJob` es por **lookup barato e independiente**: cada reconciliador busca por SU vínculo (`CreditNoteInvoiceId`, `DebitNoteInvoiceId`, `AnnulmentCreditNoteInvoiceId`) y da 0 filas para lo que no le corresponde. El guard "esta NC anula una ND → salir sin tocar cobros" es específico (`OriginalInvoice.TipoComprobante` = DebitNote). | `AfipService.cs:1566-1577, 1712-1722`; `DebitNoteAnnulmentReconciliation.cs`. |
| V10 | Existe el molde de emisión de bajo nivel reutilizable: `InvoiceService.CreateAsync` (IsCreditNote, `OriginalInvoiceId`) + `ProcessInvoiceJob`; la letra de la NC la deriva ARCA sola del `TipoComprobante` del comprobante asociado; `CreatePendingInvoice` espeja `CanMisMonExt`/moneda del `OriginalInvoice`. Es el mismo camino de la ND-multa y de la NC-anula-ND del "Deshacer". | Diseño 2026-07-14 §6.1 (V1/V2/V11). |
| V11 | Existe el prorrateo de líneas de NC parcial preservando alícuotas (una línea por alícuota, residuo absorbido exacto), parametrizado por un `FiscalAmountToCredit`. Hoy lo alimenta el VO `FiscalLiquidation`; es reutilizable pasándole `ConfirmedGrossCreditAmount`. | `BookingCancellationService.BuildPartialCreditNoteLines` (`:11914`); `PartialCreditNoteIvaCalculator`. |
| V12 | La captura T5 NO construye un `FiscalSnapshot` (el BC queda `Drafted` sin snapshot). El `ConfirmAsync` del camino total SÍ lo arma con `FiscalSnapshotData` (moneda, TC, fuente, condiciones fiscales agencia/operador/cliente). | `Adr044T5PartialCancellationTests.cs:151-160`; `ConfirmCancellationRequest`/`FiscalSnapshotData`. |
| V13 | Cada anulación parcial abre su PROPIO BC (Decisión C, verificado); `Closed`/`Aborted` se excluyen del índice único; INV-081 sigue bloqueando un segundo BC "en curso". | `Adr044T5PartialCancellationTests.cs` (Tests 2-4). |

**Consecuencia de V5+V6+V7 (clave):** la emisión T5 **NO puede** pasar por `EnqueuePartialCreditNoteAsync`
tal cual, porque ese camino marca la factura de venta como anulada del todo. La emisión T5 necesita el mismo
pipeline de **bajo nivel** (`CreateAsync` + `ProcessInvoiceJob`) que usan la ND-multa y el "Deshacer", con un
reconciliador propio que **derive** el estado de la factura en vez de forzarlo (§6, §8, blocker #3).

---

## 3. Decisión abierta: ¿UNA NC por factura acumulando varios servicios, o UNA NC por cada anulación parcial?

### Opción A — UNA NC por factura, acumulando varios servicios anulados
Esperar y juntar varias líneas/servicios de la misma factura en una sola NC al emitir.
- **Contra:** cada anulación es su propio BC (V13, Decisión C ya cerrada); acumular obliga a mirar N BCs para
  armar una NC → rompe el modelo "1 BC = 1 evento fiscal". Requiere decidir *cuándo se cierra el lote*
  (¿espero al próximo servicio? ¿cuánto?) — indefinido y en conflicto con el reloj RG 4540, que corre **por
  hecho** (cada anulación tiene su fecha; juntar dos hechos de fechas distintas en un comprobante rompe el
  "un comprobante por hecho"). Dos anulaciones pendientes de la misma factura tendrían que coordinarse antes
  de emitir (candado nuevo, más superficie de carrera).
- **A favor:** menos comprobantes ARCA.

### Opción B — UNA NC por cada anulación parcial (por BC / por evento de confirmación) — **RECOMENDADA**
Cada BC parcial confirmado emite su propia NC, vinculada por `CbtesAsoc` a la misma factura de venta.
- **A favor:** respeta "1 BC = 1 evento fiscal" (invariante viva); reloj RG 4540 por hecho, natural y "un
  aviso por comprobante" (fiscal §Tema 3); trazabilidad por BC directa (cada NC ↔ su BC ↔ su servicio);
  ARCA admite **N NCs asociadas a una factura** sin problema; **el cap por remanente ya está construido y
  probado para exactamente esto** (V2: varias reservas por-línea contra la misma factura, anti-doble-conteo);
  dos anulaciones parciales pendientes a la vez ya conviven hoy en el cap sin coordinación extra.
- **Contra:** más comprobantes (aceptable; es lo que la realidad fiscal pide: cada devolución/rescisión es su
  propio hecho documentable, RG 4540).

**Recomendación única: Opción B — una NC por cada anulación parcial.** El sistema ya está construido alrededor
de este modelo (BC-por-evento + cap por remanente); la Opción A pelearía contra la arquitectura existente y
contra el criterio de plazo fiscal. No hay decisión de negocio que resolver acá: la evidencia técnica y
fiscal apuntan a B sin ambigüedad.

---

## 4. Los 3 bloqueantes técnicos, con su solución concreta

### Bloqueante #1 — Cap flag-independiente del camino legacy `EnqueueAnnulmentAsync`
**Verificado:** el cap (`ComputeInvoiceRemainingCreditableAmountAsync`, V2) NO depende de ningún flag: descuenta
por hijas ADR-042 y por reservas de línea T5 leyendo estado persistido, y **ya lo consume el camino total**
(`ConfirmAsync` capea `CancellationAmount` al remanente real, y `EditLiquidation` idem — `:2021`, `:4500-4510`,
test B1(a)). El `EnqueueAnnulmentAsync` legacy (NC total) recorre `ConfirmAsync`, que **ya** llama al cap bajo
`RunUnderInvoiceLockAsync`. **Solución:** ninguna llave nueva; la emisión T5 (§6) invoca el **mismo** cap bajo
el **mismo** lock por factura ANTES de emitir, y crea el vínculo hijo que hace que la NC emitida cuente en (A) y
deje de contarse por la línea en (B). Así el legacy y el T5 comparten una única fuente de verdad del remanente,
sin flags. Test §9 fija que total y parcial conviven sobre la misma factura sin sobre-acreditar.

### Bloqueante #2 — Anti-doble-conteo por-línea al crear la hija
**Verificado:** el cap ya evita doble conteo con `bcIdsWithEmittedChild` (V2, sección A vs B): un BC cuya hija
ya emitió NC (con `CreditNoteInvoiceId`) NO vuelve a contar su reserva por-línea. **Solución:** al emitir la NC
T5, se crea **una fila hija `BookingCancellationCreditNote`** (molde ADR-042) con `OriginatingInvoiceId =
TargetInvoiceId`, `Status = Pending`; al conseguir CAE, el reconciliador la pasa a `Succeeded` y setea
`CreditNoteInvoiceId = nc.Id`. Desde ese momento el cap la cuenta en (A) por el monto real de la NC y la
descuenta de (B) por `bcIdsWithEmittedChild`. **Momento crítico:** la creación de la hija y la emisión ocurren
en la MISMA transacción, bajo `RunUnderInvoiceLockAsync(TargetInvoiceId)`, tras re-leer el remanente fresco —
así dos emisiones concurrentes sobre la misma factura se serializan y el cap nunca cuenta dos veces. Test §9
(concurrencia Postgres, molde `Adr044T5PartialCancellationConcurrencyIntegrationTests`).

### Bloqueante #3 — `AnnulmentStatus` de la factura de venta + test del invariante con el flujo REAL
**Semántica decidida (verificada contra los lectores, V5/V6/V7):**
- Anulación **TOTAL** de una factura → `AnnulmentStatus = Succeeded` (comportamiento actual, correcto: la
  factura entera se acredita y sale de "viva").
- Anulación **PARCIAL** que deja servicios vivos → la factura **NO** se marca. **No se agrega un estado
  "PartiallyAnnulled"**: el remanente ya se **deriva** del cap (V2) y el extracto ya se auto-sana por
  `Resultado`/`CountsInNetBilled` (V7). Agregar un estado nuevo obligaría a tocar todos los lectores que hoy
  hacen `switch`/comparación sobre `AnnulmentStatus` (V6) por cero beneficio.
- **Regla derivada, no forzada:** la factura de venta pasa a `Succeeded` **sólo cuando queda totalmente
  acreditada** — es decir, cuando el remanente acreditable llega a 0 tras esta NC (última porción de la
  factura). Mientras el remanente sea > 0, `AnnulmentStatus` queda como estaba (`None`) y la factura sigue
  viva y cobrable/facturable por el resto. Esto reusa el cálculo del cap: `remainingAfterThisNc == 0`.

Esto explica y corrige el bug de prod citado por la tanda: los tests que armaban facturas con
`AnnulmentStatus=None` a mano ocultaban esta regla. **El test obligatorio del invariante corre el flujo REAL
end-to-end** (captura T5 → confirmar/emitir → CAE simulado por ARCA fake), no seeds a mano, y verifica:
(a) parcial con servicios vivos → factura `AnnulmentStatus != Succeeded`, sigue viva; (b) parcial que consume
el 100% del remanente → factura `Succeeded`; (c) total por el camino legacy → `Succeeded`. Test §9 (integración
Postgres).

---

## 5. Snapshot fiscal del usuario al confirmar

La captura T5 no congela contexto fiscal (V12): sólo el monto por línea. La **pantalla de confirmación** captura
el `FiscalSnapshot` en el acto de emitir (mismo `FiscalSnapshotData` que `ConfirmAsync`):

- `CurrencyAtEvent` + `ExchangeRateAtOriginalInvoice`: **heredados de la factura destino** (`TargetInvoice.MonId`
  → ISO, `TargetInvoice.MonCotiz`). NO se recotiza: la NC hereda moneda y TC congelado del comprobante asociado
  (fiscal §Tema 3.c; regla firmada e inamovible). Para pesos, TC = 1.
- `Source` + `ManualJustification`: si la factura destino es extranjera, la justificación/origen del TC vienen
  del snapshot original de esa factura (INV-120); el usuario no inventa TC.
- Condiciones fiscales (`AgencyTaxConditionAtEvent`, `SupplierTaxConditionAtEvent`, `CustomerTaxConditionAtEvent`):
  se toman de la foto al momento de emitir (mismo patrón que el total). Para **Monotributo (agencia hoy)** la NC
  es Factura C: no discrimina IVA, una sola línea al bruto. Para **RI** (producto multi-condición): se preserva
  la alícuota por prorrateo (V11).
- Auditoría: `RequestedByUserId`/`Name`/`At` + acción dedicada, staged en la misma unidad de trabajo.

El snapshot se sella en el BC al confirmar (el BC pasa de `Drafted` a `AwaitingFiscalConfirmation`), igual que
el total. Mientras no se confirma, el BC sigue `Drafted` y editable (el usuario puede corregir monto/factura
destino desde la ficha antes de emitir).

---

## 6. Flujo end-to-end

### 6.1 Solicitud (`ConfirmPartialCancellationEmissionAsync`, síncrono hasta encolar la NC)

Nuevo método en `IBookingCancellationService`/`BookingCancellationService`, modelado sobre `ConfirmAsync` +
`EmitRealPartialCreditNoteAsync` (pero SIN marcar la factura anulada, V5):

1. **Validaciones de entrada (400/409):** `snapshotData` requerido y coherente (misma validación que
   `ConfirmAsync`). BC debe existir (404) y estar `Drafted`, puramente parcial (≥1 línea `Partial`, ninguna
   `Full`) → si no, 409 con mensaje neutro.
2. **Permiso elevado** (defensa en profundidad, §11): gate del módulo de cobranzas/facturación (mismo que
   la anulación total: emisión fiscal). Ownership. 409 si no.
3. **Guards duros:**
   - Todas las líneas Partial resueltas deben tener `TargetInvoiceId` y `ConfirmedGrossCreditAmount` no nulos.
     Si alguna quedó ambigua (V3: factura no elegida, moneda mismatch, TC incoherente) → 409
     `INV-T5-EMIT-UNRESOLVED` "esta cancelación necesita que una persona resuelva la factura o el monto antes
     de emitir" (no se emite a ciegas).
   - Guard de moneda NC == moneda ARCA de la factura destino (reusa el guard de `EmitRealPartialCreditNoteAsync`,
     `:11764`): NC en la moneda del comprobante asociado, nunca ARS sobre factura USD.
   - **RG 4540 (avisar, no bloquear):** si pasaron > 15 días corridos desde la fecha del hecho (la anulación,
     `bc.DraftedAt` como referencia MVP) se agrega un aviso informativo en el DTO; NO frena la emisión (fiscal
     §Tema 3, Riesgo F).
   - **Gate RI ↔ firma de alícuota:** para agencia RI la emisión automática sigue **bloqueada** hasta firma de
     matriculado (el sistema ya lo bloquea; fiscal §Tema 1). Para **Monotributo** (Factura C) NO aplica (no
     discrimina IVA): la ND/NC C se emite sin definir alícuota. El gate de alícuota debe ser **sólo para RI**.
4. **Bajo `RunUnderInvoiceLockAsync(TargetInvoiceId)`** (por cada factura destino distinta involucrada —
   normalmente una), atómico:
   - Re-leer el remanente fresco (`ComputeInvoiceRemainingCreditableAmountAsync`, `excludeBookingCancellationId:
     bc.Id`) y re-validar que `ConfirmedGrossCreditAmount <= remanente` (anti-carrera: otra emisión pudo consumir
     el remanente entre la captura y ahora). Si excede → 409 `INV-T5-EMIT-CAP` "el saldo de la factura cambió;
     revisá el monto".
   - Sellar el `FiscalSnapshot` en el BC (§5); transicionar BC `Drafted → AwaitingFiscalConfirmation`;
     `ConfirmedWithClientAt`/`ConfirmedByUserId` como el total.
   - **Armar las líneas de la NC** con `BuildPartialCreditNoteLines` (V11) usando `FiscalAmountToCredit =
     ConfirmedGrossCreditAmount` (o la suma de líneas Partial contra ESA factura destino, si el BC anuló varios
     servicios de la misma factura en un solo evento). Prorrateo por alícuota preservado (RI) / línea única C
     (Monotributo).
   - `await _invoiceService.CreateAsync(request, userId, userName, ct)` con `IsCreditNote=true`,
     `OriginalInvoiceId = TargetInvoice.PublicId`, `MonId/MonCotiz` heredados, `CanMisMonExt` espejado (V10). La
     NC Pending ya existe como `Invoice` al volver.
   - **Crear la fila hija `BookingCancellationCreditNote`** (`OriginatingInvoiceId = TargetInvoiceId`,
     `Status = Pending`, `ArcaCurrency = TargetInvoice.MonId`, **`CreditNoteInvoiceId = nc.Id`**) — el vínculo
     por el que la reconciliación T5 y el cap encuentran la NC. Setear `bc.CreditNoteKind = PartialOnOriginal`
     en el BC padre (discriminador de plata, §6.2(a)).
     > **INVARIANTE DURA (B2): la hija nace con `CreditNoteInvoiceId = nc.Id` en la MISMA transacción, nunca
     > diferido.** Verificado (`BookingCancellationService.cs:4930-4934`): el cap trata una hija `Pending` SIN
     > `CreditNoteInvoiceId` como **NC TOTAL en camino** y reserva el `ImporteTotal` COMPLETO de la factura. Si
     > se dejara el link para la reconciliación, cada NC parcial en vuelo bloquearía la factura ENTERA entre el
     > encolado y el CAE: ninguna otra parcial podría emitir y el remanente derivado sería 0 transitoriamente
     > (riesgo de marcar `AnnulmentStatus` mal si un lector corre en esa ventana). Test dedicado T-B2 (§9).
   - **DECISIÓN B3 (tomada, con evidencia): NO se setea el escalar `CreditNoteInvoiceId` del BC PADRE.** Sólo la
     hija lleva el link a la NC. Razón (verificado): el re-vinculador de ND huérfana itera BCs por el predicado
     `CreditNoteInvoiceId != null && DebitNoteInvoiceId == null && PenaltyStatus == Confirmed`
     (`BookingCancellationService.cs:4003-4008`). El BC T5 SÍ tiene `OriginatingInvoiceId` seteado
     (`:1098`, `anchorInvoiceId`), SÍ puede tener `DebitNoteInvoiceId == null`, y SÍ puede llegar a
     `PenaltyStatus == Confirmed` (una parcial admite multa de operador confirmada — ver §"no verificado"
     cerrado). El ÚNICO grado de libertad que controlamos por construcción para mantener el BC T5 FUERA del
     predicado es dejar `CreditNoteInvoiceId (padre) == null`. Así la colisión clase-"Deshacer" es **imposible
     por diseño**, sin agregar cláusulas de exclusión al re-vinculador. **El costo NO es sólo la plata** (que se
     cubre en §6.2(a) leyendo la HIJA, lo que además elimina la fragilidad del borde "última porción"): el escalar
     del padre tiene ~5 lectores más de la pata de multa/fee que exigen verlo seteado; dejarlo `null` los rompería
     (deadlock silencioso de la multa de una parcial). Ese radio se cierra haciendo **child-aware** esos lectores
     — ver **§6.4 (C1)**. Tests T-B3 + T-C1a/b (§9).
   - **NO** marcar `TargetInvoice.AnnulmentStatus` (Bloqueante #3): la factura sigue viva; su estado se derivará
     en la reconciliación sólo si el remanente llega a 0.
   - Auditoría staged (acción dedicada `PartialCreditNoteEmissionRequested`, sin jerga al usuario).
   - `SaveChangesAsync`.
5. El BC queda `AwaitingFiscalConfirmation` con la NC Pending. El front hace polling (reusa el patrón
   "procesando" del módulo).

**NO se toca la plata en la solicitud.** La reversión ocurre al consumar el CAE (§6.2).

### 6.2 Consumación del CAE (async, reusa `ProcessInvoiceJob`)

Cuando la NC parcial resuelve en ARCA, `ProcessInvoiceJob` corre para toda NC (V9):
`ApplyCreditNoteEconomicReversalAsync(nc.Id)` **y** los reconciliadores por lookup. Comportamiento para la NC T5:

**(a) Reversión de plata (reusa `ApplyPartialCreditNoteReversalAsync`, V8) — con un endurecimiento del
discriminador (B3).** El guard "esta NC anula una ND" (`:1718`) NO dispara: el `OriginalInvoice` de la NC T5 es
una **factura de venta** (1/6/11), no una ND. Pero el discriminador parcial/total actual lee el **BC padre** por
`CreditNoteInvoiceId == nc.Id` (`AfipService.cs:1729-1736`), y por la Decisión B3 el padre T5 NO tiene ese
escalar → `bc == null` → caería al **fallback por monto** (`:1745-1746`,
`nc.ImporteTotal < OriginalInvoice.ImporteTotal ⇒ parcial`), que es **frágil en el borde "última porción"**
(`==` por redondeo cruzaría a reversal TOTAL con cascade-void — incorrecto para T5, donde el recibo cubre toda
la reserva, no ese servicio).

**Endurecimiento (cambio aditivo, específico):** en `ApplyCreditNoteEconomicReversalAsync`, ANTES del fallback
por monto, agregar un segundo lookup — la **hija** `BookingCancellationCreditNote` por
`CreditNoteInvoiceId == nc.Id` cuyo BC sea **puramente parcial** (≥1 Partial, 0 Full). Si existe →
`isPartialNc = true` **determinístico** (independiente del monto). Así la NC T5 SIEMPRE recorre
**`ApplyPartialCreditNoteReversalAsync`** (correcto: NO cascade-void de recibos; el reversal se **capea contra
lo REALMENTE cobrado** en esa moneda vía `CalculateCreditNoteReversalCapAsync`, así facturar sin cobrar no
genera plata fantasma, ADR-037), **incluso en la última porción** (monto == total): para T5 el cascade-void
nunca es correcto porque el recibo cubre toda la reserva, no un servicio. Esto resuelve el borde §15-IR de raíz.
Contra-test: una NC contra factura por el total (anulación total genuina, sin hija T5) SIGUE recorriendo el
camino total con cascade-void (el guard es específico, no rompe lo sano). El monto ya está congelado; la
reversión es económica, no recalcula el bruto.

**(b) Reconciliador T5 DEDICADO + derivación de `AnnulmentStatus` (Bloqueantes #1/#2/#3).**

> ⚠️ **NO llamar `OnArcaSucceededAsync` (ni `ApplyChildResultAndReevaluateAsync`) desde el camino T5.**
> Verificado (B1): `CreateAsync` encola `ProcessInvoiceJob`, que al aprobar SOLO llama a
> `ApplyCreditNoteEconomicReversalAsync` + `TryReconcileLinkedCancellationDebitNoteAsync` +
> `TryReconcileDebitNoteAnnulmentAsync` (`AfipService.cs:1566-1577`). **NO** invoca el reconciliador de hijas
> ADR-042. Y reusarlo es PELIGROSO: `OnArcaSucceededAsync` → `HandleArcaAnnulmentCallbackAsync` →
> `ApplyChildResultAndReevaluateAsync` (`BookingCancellationService.cs:4730-4763, 4967-4975`), con la única
> hija del BC T5 en `Succeeded`, evalúa `AllSucceeded` → transiciona el BC como **anulación TOTAL de la
> reserva** (`AwaitingOperatorRefund`) y dispara `TryEmitDebitNotePostCompletionAsync` (`:4761`) — **puede
> emitir una ND fantasma**. Eso es una anulación total sobre una cancelación PARCIAL. Prohibido.

Se **construye un reconciliador T5 nuevo, dedicado** (`TryReconcilePartialCreditNoteT5Async`), molde EXACTO de
`TryReconcileDebitNoteAnnulmentAsync` (`AfipService.cs:1627-1639` + `DebitNoteAnnulmentReconciliation.cs`), y se
**cablea en `ProcessInvoiceJob`** junto a los otros dos `TryReconcile*` (blindado: si falla, el CAE ya está
persistido). **Lookup específico e inequívoco:** hija `BookingCancellationCreditNote` por
`CreditNoteInvoiceId == nc.Id && Status == Pending`, **filtrando** además a que su BC sea **puramente parcial**
(≥1 línea `Scope=Partial`, 0 líneas `Scope=Full` — el mismo predicado que usa la bandeja, V4). Ese filtro
garantiza que este reconciliador NUNCA toque una hija de una anulación total ADR-042 (que se reconcilia por su
propio camino `OnArcaSucceededAsync`, jamás por `ProcessInvoiceJob`). Da 0 filas para cualquier otra NC → no-op
barato. Corre bajo `RunUnderInvoiceLockAsync`/lock del BC en Postgres (own-SaveChanges sólo fallback InMemory).

Al conseguir CAE (`Resultado=="A"` + CAE):
- Hija `BookingCancellationCreditNote` → `Succeeded` (su `CreditNoteInvoiceId` YA está seteado desde la
  creación, invariante B2 — el cap ya la cuenta en (A)).
- **Derivar `AnnulmentStatus` de la factura destino:** recalcular el remanente FRESCO bajo el lock; si
  `remainingAfterThisNc == 0` → `TargetInvoice.AnnulmentStatus = Succeeded` + `AnnulledAt`; si > 0 → **no
  tocar** (sigue viva y cobrable/facturable). Nunca `Pending`.
- Avanzar el BC T5 por **SU** circuito (reembolso del operador / cierre), **nunca** `OnArcaSucceededAsync`,
  **nunca** ND automática, **nunca** marcar la reserva cancelada.
- Auditoría `PartialCreditNoteEmitted` con el efecto de plata.
- Rechazo ARCA (`Resultado=="R"`) → hija `Failed`; BC vuelve a un estado reintentable; la factura destino nunca
  se tocó, sigue viva; el remanente se libera solo (la hija `Failed` no cuenta en el cap, V2). El paso muestra
  "no se pudo emitir, reintentar".
- Idempotencia (redelivery Hangfire): re-lee la hija fresca; si ya `Succeeded`/`Failed`, no-op.

**Concurrencia:** el reconciliador que escribe el BC + la factura corre bajo `RunUnderInvoiceLockAsync`/
`RunUnderParentLockAsync` en Postgres (own-SaveChanges sólo como fallback InMemory). Idempotente ante redelivery
de Hangfire (re-lee la hija fresca; si ya `Succeeded`, no-op).

### 6.3 Verificación anti-colisión de reconciliadores (encuadre corregido, B3)

La tanda advierte del patrón "dos piezas que no se conocen" (el re-vinculador de ND huérfana de ADR-014 pisó al
"Deshacer" esta semana). **La colisión NO se prueba mirando "¿la NC entra?": el re-vinculador itera
`BookingCancellations` por un PREDICADO, no por la NC.** El análisis correcto es contra la forma del BC T5.

Reconciliadores por lookup de NC (estos sí se prueban por la forma de la NC):
- `ApplyCreditNoteEconomicReversalAsync`: con el endurecimiento §6.2(a), detecta la hija T5 → camino parcial
  determinístico. ✔
- `CancellationDebitNoteReconciliation`: BC por `DebitNoteInvoiceId == invoice.Id`. La NC T5 no es una ND → 0
  filas. ✔
- `DebitNoteAnnulmentReconciliation`: hija por `AnnulmentCreditNoteInvoiceId == invoice.Id`. La NC T5 no anula
  una ND → 0 filas. ✔
- `TryReconcilePartialCreditNoteT5Async` (nuevo, §6.2(b)): hija por `CreditNoteInvoiceId == nc.Id` + BC
  puramente parcial → único que actúa sobre la NC T5. ✔

**Re-vinculador de ND huérfana** (`GetCancellationsWithMissingDebitNoteAsync`) — el análisis real por predicado:
- Predicado: `CreditNoteInvoiceId != null && DebitNoteInvoiceId == null && PenaltyStatus == Confirmed`
  (`:4003-4008`); si matchea, adopta una ND suelta con `OriginalInvoiceId == bc.OriginatingInvoiceId &&
  ReservaId == bc.ReservaId` (`:4016-4027`).
- Forma del BC T5 (verificado por lectura): `OriginatingInvoiceId` **seteado** (`:1098`); `DebitNoteInvoiceId`
  puede ser **null**; `PenaltyStatus` **puede ser `Confirmed`** (una parcial admite multa de operador
  confirmada). Es decir, el BC T5 **caería EXACTO en el perfil de riesgo** — el mismo que produjo el limbo de
  prod de esta semana (F-2026-1043) — **si tuviera `CreditNoteInvoiceId (padre) != null`**.
- **Por eso la Decisión B3 deja `CreditNoteInvoiceId (padre) == null` (§6.1):** rompe la PRIMERA condición del
  predicado → el BC T5 **nunca** es candidato del re-vinculador. Colisión imposible por construcción, sin tocar
  el re-vinculador. (Nota: el re-vinculador ya tiene además un guard ADR-044 que descarta NDs bajo anulación,
  `:4020-4025`, pero no dependemos de él: nuestra defensa es no entrar al predicado.)
- **GATE DE VERIFICACIÓN DURO (test T-B3, obligatorio):** emitir una parcial T5 **CON multa de operador
  confirmada** y correr `GetCancellationsWithMissingDebitNoteAsync` → verificar que NO adopta ninguna ND sobre
  el BC T5. Es el test que atrapa la colisión clase-"Deshacer". No se asume: se prueba con el peor caso.

### 6.4 C1 — lectores child-aware de la pata de multa/fee (consecuencia de la Decisión B3)

**Decisión C1: Opción 1 — escalar del padre `null` + hacer child-aware los lectores del escalar.** Elegida por
**robustez / defensa por construcción** (el criterio explícito del dueño tras el limbo de esta semana). Las
otras dos salidas se descartaron con razón:
- *Opción 2 (setear el escalar + excluir el BC T5 del re-vinculador):* re-mete el BC T5 DENTRO del predicado
  peligroso y confía en una cláusula de exclusión — defensa por lógica frágil, en el MISMO punto que rompió prod
  esta semana. Un refactor futuro del predicado reabre el limbo. Rechazada.
- *Opción 3 (una parcial no lleva multa/fee en el MVP):* viola la regla del producto "contemplar todo con
  defaults, no con preguntas" — una parcial donde el operador cobra multa es un caso normalísimo (cancelar un
  hotel de una reserva multi-servicio con penalidad). Estrecharía el alcance en vez de contemplarlo. Rechazada.

**El problema que resuelve (verificado):** el escalar `bc.CreditNoteInvoiceId` del padre significa, para ~5
lectores, "la NC al cliente YA se emitió → la pata de multa/ND puede avanzar". Con B3 (escalar `null` en T5)
esos lectores quedarían en **deadlock silencioso** — una parcial con multa de operador nunca podría
confirmar/renunciar/(re)emitir su ND:
- `ConfirmPenaltyAsync` (`:6268`, INV-ADR014-001): `... || bc.CreditNoteInvoiceId is null` → rechaza siempre.
- `WaivePenaltyAsync` (`:7431`, INV-WAIVE-001): mismo gate.
- `RetryDebitNoteAsync` (`:6643`, INV-ADR014-RETRY-003): mismo gate.
- `EvaluateCanConfirmPenalty` (`:5581`): devuelve `CreditNoteNotYetIssued` → botón bloqueado para siempre.
- Bandeja de fee de agencia (`:4110-4118`): predicado `CreditNoteInvoiceId != null && ... ConceptKind ∈ {fee}`
  → una parcial con fee de agencia nunca aparece en su bandeja.

**La solución (con precedente en el código):** ya existe el helper `BcHasLiveCreditNoteChildAsync(bcId)`
(`:1681-1687`, devuelve true si hay una hija con `Status==Succeeded` **o** `CreditNoteInvoiceId != null`) y ya se
usa exactamente por esta razón en `:1513-1514` ("el puntero singular puede ser null y aún así existir una hija
con NC viva", ADR-042 §3.4). C1 **extiende ese mismo patrón** a los 5 lectores: la condición
`bc.CreditNoteInvoiceId != null` pasa a `bc.CreditNoteInvoiceId != null || <hay hija con NC viva>`.

**Trazado por lector:**
- **Lectores de UN BC** (`ConfirmPenaltyAsync`, `WaivePenaltyAsync`, `RetryDebitNoteAsync`,
  `EvaluateCanConfirmPenalty`): reusan el helper existente `BcHasLiveCreditNoteChildAsync(bc.Id, ct)`. Cambio
  mecánico de una condición por sitio.
- **Lectores de LISTA** (bandeja de fee `:4110-4118` y cualquier proyección de read-model que corra sobre una
  colección): NO llamar el helper por fila (N+1). La condición child-aware se expresa como **subconsulta
  traducible** dentro del `.Where`: `(b.CreditNoteInvoiceId != null || _db.BookingCancellationCreditNotes.Any(c
  => c.BookingCancellationId == b.Id && (c.Status == Succeeded || c.CreditNoteInvoiceId != null)))`. Mismo
  criterio semántico que el helper, sin N+1.

**Efecto:** una parcial T5 con multa de operador confirmada funciona de punta a punta (confirmar → (re)emitir ND
→ renunciar), **sin** meter el BC T5 en el predicado del re-vinculador (que sigue leyendo el escalar crudo
`CreditNoteInvoiceId != null`, que para T5 es `null`). La defensa por construcción contra el limbo se mantiene
intacta; sólo la pata de multa/fee "ve" la NC viva por la hija. Tests T-C1a/T-C1b (§9).

**Corrección de la contradicción del §17 (señalada por el reviewer):** con C1, `PenaltyStatus == Confirmed`
**SÍ** es alcanzable en una parcial — porque `ConfirmPenaltyAsync` ahora es child-aware y ve la NC viva por la
hija aunque el escalar del padre sea `null`. Es decir: la confirmación de multa NO depende del escalar del padre;
lo alcanza vía la hija. Y el re-vinculador SÍ depende del escalar crudo (que es `null`) → no matchea. Las dos
cosas son verdad a la vez, sin contradicción: el BC T5 puede llegar a `Confirmed + DebitNoteInvoiceId==null` y
aun así quedar fuera del predicado del re-vinculador por `CreditNoteInvoiceId (padre) == null`. La justificación
de riesgo de B3 se sostiene y el deadlock desaparece.

---

## 7. Contrato para la pantalla (la diseña el gate UX con Gastón DESPUÉS de este diseño)

**Datos que muestra (solo lectura, el monto NO se recalcula — se muestra):**
- Reserva (número + cliente), operador del/los servicio(s) anulado(s).
- Lista de servicios anulados en este evento, con su monto bruto congelado (`ConfirmedGrossCreditAmount`).
- **Monto de la NC** = suma de las líneas Partial contra la factura destino (congelado).
- **Factura destino** (número de comprobante legible, nunca Id) y su **remanente vivo** antes de esta NC.
- **Moneda y TC congelado** de la factura destino (si es USD, se muestra el TC heredado; nunca se ofrece cambiar
  a ARS).
- **Aviso RG 4540** si pasaron > 15 días desde la anulación (informativo, no bloquea).
- Si la agencia es **RI**: aviso de que la emisión automática requiere firma de matriculado sobre la alícuota
  (bloqueo actual); si **Monotributo**: sin ese aviso (Factura C no discrimina IVA).

**Acciones:**
- **Confirmar y emitir** (permiso de emisión fiscal): sella el snapshot fiscal, emite la NC (§6.1).
- **Corregir** (monto / factura destino) antes de emitir: navega a la resolución en la **ficha** (no en la
  bandeja; la bandeja es lista pasiva).
- Cancelar/volver.

**Estados / errores (mensajes sin jerga, sin IDs/enum/texto ARCA crudo — gate data-exposure):**
- `Procesando` (NC Pending, polling) → "Emitiendo la nota de crédito…".
- `Emitida` (CAE) → "Nota de crédito emitida" (solo número de reserva en el aviso, criterio dueño 2026-07-08).
- `No se pudo emitir` (ARCA rechazó) → "No se pudo emitir, reintentar".
- 409 `INV-T5-EMIT-UNRESOLVED` → "Falta resolver la factura o el monto antes de emitir".
- 409 `INV-T5-EMIT-CAP` → "El saldo de la factura cambió; revisá el monto".
- 409 permiso / RI-firma → mensajes neutros del módulo.

---

## 8. Efecto en la plata, por moneda y por estado de cobro

| Caso | Qué pasa | Mecánica |
|------|----------|----------|
| **Impaga** | La parte del servicio anulado deja de ser deuda; el resto de la factura sigue como deuda. | La NC (abono con CAE) compensa su parte en el extracto (V7); `ApplyPartialCreditNoteReversalAsync` capea a 0 lo cobrado (no había cobro) → sin `Payment` nuevo. Factura viva por el resto. |
| **Pagada** | Queda **saldo a favor reutilizable** por lo cobrado de ese servicio (ADR-036), sin devolución física automática. | `ApplyPartialCreditNoteReversalAsync` crea el `Payment` reversal capeado a lo REALMENTE cobrado en esa moneda; NO cascade-voidea recibos (política F2.3); el saldo a favor emerge por moneda. |
| **Parcialmente pagada** | Combinación natural: lo cobrado → saldo a favor; lo impago → deja de ser deuda. | El cap del reversal (`CalculateCreditNoteReversalCapAsync`) ya reparte; sin lógica especial. |
| **USD (TC congelado)** | La NC neta a cero en la moneda del comprobante; no genera diferencia de cambio nueva. | La NC hereda `MonId/MonCotiz/CanMisMonExt` de la factura destino. Si se cobró en ARS a otro TC, la dif. de cambio realizada al devolver el saldo = **asiento contable, sin ND fiscal** para Monotributo / concepto no gravado (fiscal §Tema 2); RI con concepto gravado → a "Comprobantes por resolver" (fuera de MVP, §10). |

**Invariante de plata (Bloqueante #3):** la NC parcial T5 **jamás** debe marcar la factura de venta
`AnnulmentStatus = Succeeded` mientras el remanente sea > 0; y el cap (§4) garantiza que la suma de NCs vivas
contra una factura nunca supera su `ImporteTotal`. Tests §9 lo fijan.

---

## 9. Tests obligatorios

**Unit (dominio / servicio con InMemory):**
1. `BuildPartialCreditNoteLines` con `FiscalAmountToCredit = ConfirmedGrossCreditAmount`: Σ líneas == monto
   exacto (residuo absorbido); RI multi-alícuota → una línea por alícuota; Monotributo → línea única.
2. Guard `INV-T5-EMIT-UNRESOLVED`: línea con `TargetInvoiceId` null, o monto null (mismatch moneda/TC) → no
   emite.
3. Guard `INV-T5-EMIT-CAP`: `ConfirmedGrossCreditAmount > remanente` (otra NC consumió) → 409, no emite.
4. Guard de moneda: NC en moneda ≠ moneda ARCA de la factura destino → aborta (reusa `:11764`).
5. RG 4540: > 15 días → aviso informativo, NO bloquea.

**Integración (Postgres, flujo REAL — no seeds a mano):**
6. **Invariante `AnnulmentStatus` (el crítico, blocker #3):** captura T5 → confirmar/emitir → CAE (ARCA fake) →
   (a) parcial con servicios vivos ⇒ factura destino `AnnulmentStatus != Succeeded`, sigue viva/cobrable;
   (b) parcial que consume el 100% del remanente ⇒ factura `Succeeded`; (c) total legacy ⇒ `Succeeded`.
7. **Reversión parcial (blocker #1):** la NC T5 pasa por `ApplyPartialCreditNoteReversalAsync` (no
   `ApplyTotalCreditNoteReversalAsync`): NO cascade-voidea recibos; con multa/servicio pagado → saldo a favor
   capeado a lo cobrado; impago → sin `Payment` nuevo. Contra-test: una NC contra factura por el total sí hace
   el camino total.
8. **Cap conviven total+parcial (blocker #1):** una NC total legacy y una NC parcial T5 sobre facturas de la
   misma reserva → la suma de acreditaciones nunca supera `ImporteTotal`; anti-doble-conteo hija-vs-línea.
9. **Anti-colisión de reconciliadores (blocker de la tanda):** emitir la NC parcial T5 y correr
   `ProcessInvoiceJob` → NINGÚN otro reconciliador (re-vinculador de ND huérfana ADR-014,
   `CancellationDebitNoteReconciliation`, `DebitNoteAnnulmentReconciliation`,
   `TotalCreditNoteBridge`/`PartialCreditNotePostingReconciliationJob`) reclama ni pisa la NC T5.
10. **Concurrencia:** dos emisiones casi simultáneas sobre la misma factura destino → se serializan bajo el
    lock por factura; la segunda ve el remanente reducido y respeta el cap (molde
    `Adr044T5PartialCancellationConcurrencyIntegrationTests`).
11. **Idempotencia del reconciliador:** redelivery de Hangfire de `ProcessInvoiceJob` → una sola transición de
    la hija a `Succeeded`, un solo reversal.
12. **Rechazo ARCA:** NC T5 `Resultado=="R"` → hija `Failed`, factura destino intacta y viva, remanente
    liberado, paso "reintentar".
13. **Herencia moneda/TC:** factura destino USD → NC sale en DOL con `MonCotiz`/`CanMisMonExt` heredados;
    `CbtesAsoc` → factura destino.
14. **Data-exposure:** los 409 y los tokens del paso no filtran enum/ID/texto ARCA (molde
    `CancellationErrorMessageLeakUnitTests`).

**Tests obligatorios del reviewer (Rev 2, blindan B1/B2/B3):**
15. **T-B1 (crítico, contra-test de la reutilización peligrosa):** correr `ProcessInvoiceJob` sobre la NC T5
    aprobada → verificar que **NO** se dispara el camino de completitud ADR-042: el BC NO pasa a
    `AwaitingOperatorRefund`, NO se emite ninguna ND, la reserva NO queda marcada cancelada. Confirma que la NC
    T5 se reconcilia SOLO por `TryReconcilePartialCreditNoteT5Async` y jamás por `OnArcaSucceededAsync`.
16. **T-B2 (link de la hija no diferido):** dos parciales sobre la misma factura; primera hija `Pending` **con
    `CreditNoteInvoiceId` seteado** → verificar que el remanente que ve la segunda es
    `ImporteTotal − monto_real_NC1`, **NO 0**, y que respeta su propio cap. Contra-regresión documentada: una
    hija `Pending` SIN `CreditNoteInvoiceId` haría que el cap reserve el `ImporteTotal` completo (`:4930-4934`).
17. **T-B3 (colisión clase-"Deshacer", el que faltaba):** parcial CON multa de operador **confirmada**
    (`PenaltyStatus=Confirmed` a nivel BC) emitida por T5 → correr `GetCancellationsWithMissingDebitNoteAsync` →
    verificar que el re-vinculador de ND huérfana **NO adopta ninguna ND** sobre el BC T5 (porque
    `CreditNoteInvoiceId (padre) == null` lo deja fuera del predicado). Cubre el peor caso, no sólo la parcial
    sin multa.
18. **T-derivación borde (última porción por redondeo):** `ConfirmedGrossCreditAmount == remanente` por
    redondeo → verificar coherencia entre (a) el discriminador de plata, que por §6.2(a) recorre
    `ApplyPartialCreditNoteReversalAsync` (NO cascade-void) **igual** en este borde, y (b) la derivación del
    cap, que marca `TargetInvoice.AnnulmentStatus = Succeeded` porque `remainingAfterThisNc == 0`. Los dos
    lados consistentes: reversal parcial + factura totalmente acreditada.

**Tests obligatorios de C1 (blindan que la pata de multa/fee no queda en deadlock — §6.4):**
19. **T-C1a (deadlock de multa):** parcial T5 emitida con CAE (escalar del padre `null`, hija con NC viva) →
    `ConfirmPenaltyAsync` y `WaivePenaltyAsync` sobre su operador **resuelven** (NO tiran INV-ADR014-001 /
    INV-WAIVE-001 perpetuo); `RetryDebitNoteAsync` puede (re)emitir la ND. Verifica que los lectores son
    child-aware (ven la NC viva por la hija). Es el contra-test del deadlock silencioso.
20. **T-C1b (bandeja de fee de agencia):** parcial T5 con `ConceptKind = AgencyManagementFee` y NC viva por la
    hija (escalar padre `null`) → **aparece** en la bandeja de fee (`GetCancellationsWithMissingDebitNoteAsync`,
    rama `:4110-4118` child-aware), sin N+1 (subconsulta traducible). Verifica que el fee de una parcial no queda
    invisible.

---

## 10. Alcance MVP y qué NO entra (explícito)

**Entra ahora:**
- Pantalla de confirmación (contrato §7; la diseña el gate UX con Gastón).
- Emisión real de la NC parcial de un evento T5 por el pipeline `CreateAsync`+`ProcessInvoiceJob`, con snapshot
  fiscal, herencia moneda/TC, letra derivada por ARCA, `CbtesAsoc` → factura destino.
- Una NC por evento de anulación parcial (Opción B, §3).
- Cap flag-independiente compartido con el legacy; anti-doble-conteo hija/línea; derivación de `AnnulmentStatus`.
- Reversión de plata parcial correcta (impaga / pagada / parcial / USD).
- Aviso RG 4540; gate RI ↔ firma de alícuota (bloqueado para RI, libre para Monotributo).

**NO entra (anotado, no ignorado):**
- **Caso `TotalPlusNewInvoice`** (NC total + factura nueva por el remanente, `CreditNoteKind` casos 4/7): sigue
  rechazado en confirm, igual que Fase 1 (`CreditNoteKind.cs:23`). No se construye acá.
- **Diferencia de cambio realizada** cuando el servicio USD se cobró en ARS a otro TC: asiento contable sin ND
  fiscal (Monotributo / no gravado); para RI con concepto gravado → a "Comprobantes por resolver". Pendiente
  firma contador (fiscal §Tema 2).
- **Emisión automática para RI**: bloqueada hasta firma de matriculado sobre la alícuota de la multa/servicio
  (fiscal §Tema 1). El sistema ya la bloquea; esta tanda NO la habilita.
- **IIBB provincial**: no modelado, depende de jurisdicción (fiscal §riesgo 6).
- **Devolución física** del saldo a favor (bancarización Ley 25.345): paso posterior separado, no automático.
- **Contador de plazo RG 4540 en la bandeja** con semáforo verde/amarillo/rojo: MVP muestra el aviso; el
  semáforo fino queda anotado (fiscal §Tema 3 recomendación).

---

## 11. Permisos

Mismo patrón del módulo: la **emisión fiscal** es gate del módulo de cobranzas/facturación (el mismo permiso
que la anulación total y que la bandeja "Comprobantes por resolver" — `cobranzas.invoice_annul`, verificado en
`PendingCreditNoteReviewDto` y `EditLiquidationAsync`). `[Authorize]` + permiso resuelto server-side +
ownership. **Verificar en implementación** el permiso exacto del anular-total y reusarlo idéntico (no inventar
uno nuevo). Corregir monto/factura destino antes de emitir usa el permiso de clasificación/edición de la
cancelación (el mismo que ya rige la resolución en la ficha).

---

## 12. Qué NO tocar (para no romper lo sano)

- `EnqueueAnnulmentAsync` / `ProcessAnnulmentJob`: la emisión T5 NO pasa por ahí (rechaza NC/ND/M).
- `EnqueuePartialCreditNoteAsync` / `EmitRealPartialCreditNoteAsync` **legacy**: NO se reusan para T5 (marcan la
  factura `Succeeded`, V5). Su camino FC1.3 (anulación total con NC de valor parcial por multas/no-reintegrables)
  queda intacto. La emisión T5 usa el pipeline de bajo nivel (`CreateAsync`), como la ND-multa y el Deshacer.
- `ApplyPartialCreditNoteReversalAsync` / `CalculateCreditNoteReversalCapAsync`: se **reusan** tal cual (la NC
  T5 entra por el discriminador parcial, V8); no se modifican.
- `ComputeInvoiceRemainingCreditableAmountAsync`: se **reusa** tal cual (ya contempla hijas + líneas T5, V2).
- `ReservaAccountStatementBuilder` / `CountsInNetBilled`: el extracto se auto-sana por `Resultado`; cero cambios
  (V7).
- La regla firmada "el TC del comprobante es el congelado del original": la NC hereda `MonCotiz` de la factura
  destino, nunca recotiza.
- **Sin llaves nuevas** ("basta de llaves"): la emisión T5 sale directa, sin feature flag propio. Los flags
  FC1.3 existentes (`EnablePartialCreditNotes`, `EnablePartialCreditNoteRealEmission`) NO se extienden a este
  camino ni se agregan nuevos; su retiro es deuda separada, fuera de esta tanda.

---

## 13. Plan de implementación por fases

- **F1 — Dominio + reconciliación (backend):** método `ConfirmPartialCancellationEmissionAsync` (§6.1) modelado
  sobre `ConfirmAsync`; creación de la hija `BookingCancellationCreditNote` + `CreditNoteKind=PartialOnOriginal`;
  emisión vía `CreateAsync`; reconciliador de la hija T5 + derivación de `AnnulmentStatus` (§6.2); guards
  `INV-T5-EMIT-*`. Reusa cap + reversión parcial existentes.
- **F2 — Contrato API + DTO:** `POST /api/cancellations/{publicId}/emit-partial-credit-note` (body = snapshot
  fiscal); DTO de confirmación (§7) con monto/remanente/moneda/TC/avisos; mapeo de errores 404/409 sin jerga.
- **F3 — Gate UX (Gastón) + Frontend:** el gate UX diseña la pantalla desde `docs/ux/guia-ux-gaston.md` sobre
  este contrato; `frontend-senior` implementa lo aprobado (procesando/emitida/falló, corregir en la ficha).
- **F4 — Tests + reviews:** unit + integración Postgres (§9), incluyendo el invariante `AnnulmentStatus` con el
  flujo REAL y la anti-colisión de reconciliadores; luego `backend-dotnet-reviewer`,
  `security-data-risk-reviewer`, `data-exposure-reviewer`, `travel-agency-accountant-argentina` (fórmula de
  plata + fiscal), `qa-automation`.

Migraciones: **ninguna nueva imprescindible** (la hija `BookingCancellationCreditNote` y los campos T5 ya
existen). Si el reconciliador T5 necesita distinguir "hija de origen parcial-T5" de "hija ADR-042", se resuelve
con el `CreditNoteKind` del BC (ya persistido) — sin columna nueva. Cualquier campo que surja debe ser
**aditivo** y sin backfill.

---

## 14. Preguntas de negocio (minimizadas — solo lo que un default no resuelve)

1. **Firma del contador para RI (fiscal §Tema 1, riesgo residual 1):** habilitar la emisión automática de NC
   parcial para agencia **Responsable Inscripto** requiere que un matriculado firme el tratamiento de IVA de la
   porción trasladada (no gravado vs 21%). Para **Monotributo (agencia hoy) NO aplica** y la tanda avanza. Esto
   NO bloquea el MVP (Monotributo), pero SÍ bloquea prender RI. ¿Se deja el bloqueo RI como está (recomendado) o
   Gastón quiere impulsarlo con un contador antes?

Todo lo demás se resuelve con defaults verificados (una NC por evento, herencia moneda/TC, derivación de
`AnnulmentStatus`, avisar-no-bloquear RG 4540). No hay más preguntas abiertas de negocio.

---

## 15. Riesgos y confirmaciones pendientes

- **[NV — RIESGO]** Comportamiento literal de WSFEv1 al validar el `MonCotiz` congelado de la NC parcial contra
  la banda oficial del día y al aceptar/rechazar por el plazo de 15 días. Mismo test de homologación pendiente
  que ya arrastran la NC total y la ND; no es nuevo.
- **[GATE DURO — verificar en implementación, no asumir]** Que el estado del BC al reconciliar coincida con el
  que espera el reconciliador de hijas ADR-042 (`AwaitingFiscalConfirmation`); si no, agregar un reconciliador
  T5 espejo de `DebitNoteAnnulmentReconciliation` (lookup por `OriginatingInvoiceId`). Cubierto por test §9.6/9.9.
- **[GATE DURO]** Confirmar con test que el re-vinculador de ND huérfana (ADR-014) y los bridges/posting jobs no
  reclaman la NC parcial T5 (el mismo tipo de colisión que pisó al Deshacer esta semana).
- **`Necesita confirmación profesional contable`:** cuenta exacta del saldo a favor cuando el servicio estaba
  pagado, y la diferencia de cambio realizada al reversar un cobro ARS de un servicio USD.
- **[IR]** El caso "ConfirmedGrossCreditAmount == remanente total de la factura" cruza a reversión total
  (cascade-void) y a `AnnulmentStatus=Succeeded`. Es correcto (es la última porción), pero el reviewer debe
  validar que el límite parcial↔total por monto no deje un hueco (ej. redondeo). Test §9.6(b)/9.7.

**Gates antes de mergear:** `software-architect-reviewer` → `travel-agency-accountant-argentina` →
backend/frontend + reviewers → `security-data-risk-reviewer` + `data-exposure-reviewer` → `qa-automation`. Gate
UX con Gastón para la pantalla ANTES de implementar el front.

---

## 16. Review de arquitectura (software-architect-reviewer, 2026-07-15)

**Veredicto: APROBADO CON CONDICIONES.** El diseño está bien investigado y la mayoría de sus afirmaciones
fácticas se verificaron correctas contra el código (commit HEAD). La dirección es la correcta: NO reusar el
pipeline legacy que marca la factura `Succeeded`, sí reusar `CreateAsync`+`ProcessInvoiceJob` de bajo nivel,
derivar `AnnulmentStatus` en vez de forzarla, y reusar el cap ya construido. Pero hay **3 bloqueantes** que
tocan emisión fiscal real contra ARCA y plata, y por lo tanto deben cerrarse ANTES de codificar. No apruebo
para construir tal como está: el diseño describe la reconciliación T5 como "reusar/espejar el de ADR-042" y esa
reutilización, tomada literal, es **activamente peligrosa** (detalle en B1).

### Hechos verificados (confirmados en código)

- **V5 correcto.** `EnqueuePartialCreditNoteAsync` marca la factura `AnnulmentStatus=Pending` (`InvoiceService.cs:2057`)
  y `Succeeded` al CAE (`:2594`, `:2935`). El legacy total (`ProcessAnnulmentJob`→`HandleCreditNoteAnnulmentResultAsync`)
  también marca `Succeeded` incondicional (`InvoiceService.cs:1786`). Reusar cualquiera de los dos mataría la
  factura para el resto de servicios. La decisión de NO reusarlos es correcta.
- **V6 correcto.** `hasLiveCae` lee `!IsCreditNote && CAE!=null && AnnulmentStatus != Succeeded`
  (`ReservaService.cs:2645-2648`). Binario: mientras T5 deje `AnnulmentStatus` sin tocar, la factura sigue viva.
- **V7 correcto.** El extracto categoriza por `Resultado`/tipo de comprobante (`ReservaService.cs:240-264`), no
  por `AnnulmentStatus`. La NC parcial con CAE compensa su parte sola. Confirmado.
- **V2/cap correcto.** `ComputeInvoiceRemainingCreditableAmountAsync` (`BookingCancellationService.cs:4886-4960`)
  descuenta hijas ADR-042 en (A) y reservas por-línea T5 en (B) con anti-doble-conteo `bcIdsWithEmittedChild`.
  El lock por factura (`RunUnderInvoiceLockAsync`, `:4818`) es el MISMO que toma la captura T5
  (`CancelServiceAsync`, `:555-559`) → **la respuesta de concurrencia (item 6) es sólida**: dos emisiones sobre la
  misma factura se serializan y re-leen el remanente fresco. Correcto.
- **V8 parcialmente correcto (ver B3).** `ApplyCreditNoteEconomicReversalAsync` sí discrimina parcial/total y sí
  tiene fallback por monto (`AfipService.cs:1729-1756`); `ApplyPartialCreditNoteReversalAsync` existe, capea
  contra lo cobrado (`CalculateCreditNoteReversalCapAsync`, `:1785`) y NO cascade-voidea recibos. Pero el
  discriminador primario lee el **BC padre** por `CreditNoteInvoiceId==nc.Id`, no la hija — matiz que el diseño
  no resuelve (B3).
- **§6.3 parcialmente correcto.** Los dos reconciliadores que SÍ corren en `ProcessInvoiceJob`
  (`TryReconcileLinkedCancellationDebitNoteAsync` `:1573`, `TryReconcileDebitNoteAnnulmentAsync` `:1577`) son
  lookup-específicos: el segundo busca por `BookingCancellationDebitNoteAnnulment.AnnulmentCreditNoteInvoiceId`
  (`DebitNoteAnnulmentReconciliation.cs:53-57`), tabla que una NC-de-venta T5 nunca puebla → no-op seguro.
  Verificado. PERO el análisis del re-vinculador de ND huérfana está mal encuadrado (B3).

### Bloqueantes

**B1 — La reconciliación T5 NO está cableada, y "reusar el de ADR-042" es peligroso, no neutro.**
`CreateAsync` encola `_afipService.ProcessInvoiceJob` (`InvoiceService.cs:480`). Ese job, al aprobar
(`AfipService.cs:1566-1577`), llama SOLO a `ApplyCreditNoteEconomicReversalAsync` +
`TryReconcileLinkedCancellationDebitNoteAsync` + `TryReconcileDebitNoteAnnulmentAsync`. **NO llama
`OnArcaSucceededAsync` ni el reconciliador de hijas ADR-042.** Ése solo se invoca desde los caminos
especializados (`HandleCreditNoteAnnulmentResultAsync` `:1857`, `ProcessPartialCreditNoteJob` `:2969`,
`SyncBcAfterReconciledPartialCreditNoteAsync` `:3287`). Consecuencia: por default NADA marcaría la hija T5
`Succeeded`, ni derivaría `AnnulmentStatus`, ni avanzaría el BC.
Peor: si el implementador toma el diseño literal ("reusa/espeja el reconciliador de hijas ADR-042") y llama
`OnArcaSucceededAsync`, dispara `HandleArcaAnnulmentCallbackAsync`→`ApplyChildResultAndReevaluateAsync`
(`BookingCancellationService.cs:4730-4763, 4967-4975`): con la única hija del BC T5 en `Succeeded`, evalúa
`AllSucceeded` → transiciona el BC como **anulación TOTAL de la reserva** (`AwaitingOperatorRefund`) y dispara
`TryEmitDebitNotePostCompletionAsync` (`:4761`) — **puede emitir una ND** de multa. Eso es una anulación total
fantasma sobre una cancelación PARCIAL.
*Remediación (obligatoria):* comprometer un **reconciliador T5 dedicado** cableado dentro de
`ProcessInvoiceJob` (molde exacto: `TryReconcileDebitNoteAnnulmentAsync`, lookup por `OriginatingInvoiceId` +
hija Pending), con semántica propia: (a) hija→`Succeeded`; (b) derivar `AnnulmentStatus=Succeeded` SOLO si
`remainingAfterThisNc==0`; (c) avanzar el BC T5 por SU circuito (nunca `OnArcaSucceededAsync`, nunca ND
automática, nunca marcar la reserva cancelada). El diseño debe decir "NO llamar `OnArcaSucceededAsync`", no
"reusarlo". §6.2(b) hoy dice lo contrario y es el mayor riesgo de la tanda.

**B2 — La hija DEBE nacer con `CreditNoteInvoiceId=nc.Id` (no diferido). Hoy es "Recomendado"; es obligatorio.**
El cap trata una hija `Pending` SIN `CreditNoteInvoiceId` como **NC TOTAL en camino** y reserva el
`ImporteTotal` COMPLETO de la factura (`BookingCancellationService.cs:4930-4934`:
`alreadyCommitted += importeTotal`). Si T5 crea la hija Pending y deja `CreditNoteInvoiceId` para la
reconciliación, entre el encolado y el CAE cada NC parcial en vuelo bloquea la factura ENTERA: ninguna otra
parcial de esa factura podría emitir, y el remanente derivado sería 0 transitoriamente (riesgo de marcar
`AnnulmentStatus` mal si algún lector corre en esa ventana). El diseño ya intuye esto ("Recomendado: setear
`CreditNoteInvoiceId=nc.Id` ya en la creación") pero debe volverlo **requisito duro**. Test dedicado: dos
parciales sobre la misma factura, la primera Pending; verificar que la segunda ve el remanente correcto (no 0)
y respeta su propio cap.

**B3 — Decisión no tomada: ¿se setea el `CreditNoteInvoiceId` del BC PADRE? Cada opción tiene una consecuencia
que el diseño no traza, y §6.3 prueba mal la no-colisión.**
El reversal de plata discrimina leyendo el **BC padre** por `CreditNoteInvoiceId==nc.Id`
(`AfipService.cs:1729-1736`). Con el modelo de hija (ADR-042) que propone el diseño, ese puntero escalar del
padre NO se setea salvo que se decida explícitamente. Dos caminos, ambos con costo:
- *No setear el escalar del padre:* el discriminador V8 por `CreditNoteKind` NUNCA dispara; cae al fallback por
  monto (`AfipService.cs:1745-1746`, `nc.ImporteTotal < OriginalInvoice.ImporteTotal ⇒ parcial`). Funciona para
  parciales genuinas, pero es frágil en el borde "última porción" (`==` por redondeo → cruza a reversal TOTAL /
  cascade-void; el propio §15 IR lo anota). Y contradice la afirmación V8 tal como está escrita.
- *Setear el escalar del padre a `nc.Id`:* habilita al **re-vinculador de ND huérfana** a adoptar el BC T5. Su
  predicado real es `CreditNoteInvoiceId != null && DebitNoteInvoiceId == null && PenaltyStatus == Confirmed`
  (`BookingCancellationService.cs:4003-4008`), y re-vincula una ND suelta que matchee `bc.OriginatingInvoiceId`
  (`:4016-4027`). Si la cancelación parcial también lleva una **multa de operador confirmada**
  (`PenaltyStatus=Confirmed` a nivel BC se deriva de las líneas, `:6097-6099`), el BC T5 cae EXACTO en ese
  perfil — el mismo que produjo el limbo de prod de esta semana (F-2026-1043 / BC 13, ver `AuditActions.cs:324-334`).
El §6.3 argumenta que "el re-vinculador opera sobre NDs, no NCs de venta → la NC T5 no entra". Eso **misencuadra
la colisión**: el re-vinculador itera `BookingCancellations` por ese predicado, no por la NC. La prueba escrita
es exactamente el tipo de sobre-confianza que precedió la regresión del "Deshacer". *Remediación:* (1) decidir y
documentar si se setea el escalar del padre; (2) reemplazar la "prueba" de §6.3 por un análisis del predicado
real contra la forma del BC T5 (¿`OriginatingInvoiceId` seteado? ¿`PenaltyStatus` puede ser `Confirmed` en una
parcial? ¿`DebitNoteInvoiceId` null?); (3) el test §9.9 debe cubrir el caso "parcial CON multa de operador
confirmada" además del caso sin multa.

### Mejoras mayores (no bloqueantes)

- **M1 — `AnnulmentStatus` derivado del cap: verificar TODOS los lectores binarios, no solo los citados.** V6
  cita `ReservaService`, pero hay lectores en `MutationGuards`, gates de facturar-remanente, anular-total
  posterior, el "Deshacer" de multa y reportes. Un lector que asuma "`None` ⇒ factura 100% viva" no se rompe
  hoy (la factura SÍ sigue viva por el resto), pero el riesgo es el inverso: un lector que en el futuro
  interprete "`!=Succeeded` ⇒ nada acreditado" facturaría/cobraría sobre plata ya devuelta. El diseño deriva el
  remanente del cap (correcto y barato para el gate de emisión), pero debería enumerar explícitamente qué
  lectores NUNCA deben usar `AnnulmentStatus` como proxy de "cuánto queda vivo" y cuáles sí. Test de barrido.
- **M2 — Reintento visible ante muerte del job entre confirm y creación de la Invoice.** §6.1 sella el snapshot
  y transiciona el BC a `AwaitingFiscalConfirmation` bajo el lock, y luego llama `CreateAsync`. Si el proceso
  muere entre el commit del BC y el `CreatePendingInvoice`, el BC queda `AwaitingFiscalConfirmation` sin NC ni
  hija: ¿quién lo re-detecta? El diseño no nombra un barrendero para ese hueco (el de FC1.3
  `PartialCreditNotePostingReconciliationJob` opera sobre NCs ya creadas, no sobre BCs sin NC). Definir el
  camino de recuperación o el orden de escritura que lo hace imposible.
- **M3 — El guard RG 4540 usa `bc.DraftedAt` como "fecha del hecho".** Es un proxy razonable (la captura ocurre
  al cancelar el servicio), pero el "hecho" fiscal es la fecha de la **anulación del servicio**, no la del
  draft; si difieren (p.ej. draft editado días después), el aviso de 15 días podría contar mal. Aceptable para
  MVP si se documenta el supuesto; anotarlo como deuda.

### Tests que agregaría a la lista obligatoria (§9)

- **T-B1 (crítico):** correr `ProcessInvoiceJob` sobre la NC T5 aprobada y verificar que **NO** se dispara el
  camino de completitud ADR-042 (BC NO pasa a `AwaitingOperatorRefund`, NO se emite ND). Es el contra-test de
  la reutilización peligrosa.
- **T-B2:** dos parciales sobre la misma factura; primera hija Pending CON `CreditNoteInvoiceId` seteado →
  verificar que el remanente que ve la segunda es `ImporteTotal − monto_real_NC1`, NO 0. Y el contra-caso: si
  por bug la hija queda Pending sin `CreditNoteInvoiceId`, el cap reserva el total (documentar como regresión a
  atrapar).
- **T-B3:** parcial CON multa de operador confirmada (`PenaltyStatus=Confirmed`) emitida por T5 → correr la
  bandeja de NDs (`GetCancellationsWithMissingDebitNoteAsync`) y verificar que el re-vinculador de ND huérfana
  NO adopta ninguna ND sobre el BC T5. Es el test que atrapa la colisión clase-"Deshacer".
- **T-derivación borde:** `ConfirmedGrossCreditAmount == remanente` por redondeo (última porción) → verificar
  que el cruce parcial↔total y el `AnnulmentStatus=Succeeded` son consistentes entre el discriminador de plata
  y la derivación del cap (§15 IR).

### Sobre la Opción B (una NC por evento) — de acuerdo, no es bloqueante

Verificado que el sistema ya está construido alrededor de BC-por-evento + cap por remanente. El costo (5
anulaciones = 5 NCs) es el correcto fiscalmente (RG 4540, un comprobante por hecho); ARCA admite N NCs por
factura vía `CbtesAsoc`; el extracto sigue legible (cada NC resta su parte por `Resultado`). La decisión es
sólida y no reabre nada.

### No verificado (queda para implementación/otros gates)

- Comportamiento literal de WSFEv1 con el `MonCotiz` congelado y el plazo de 15 días (homologación; no es nuevo).
- Que `CancelServiceAsync` setee o no `OriginatingInvoiceId` en el BC T5 y que `PenaltyStatus=Confirmed` sea
  alcanzable en una parcial (insumo directo de B3 — no lo terminé de rastrear; el implementador debe cerrarlo
  con el test T-B3, no por lectura).
- La fórmula exacta del saldo a favor y la diferencia de cambio realizada (gate contable
  `travel-agency-accountant-argentina`).

---

## 17. Cierre de la revisión (Rev 2, 2026-07-15) — 3 bloqueantes resueltos + insumos verificados

Estado: **los 3 bloqueantes del reviewer quedaron cerrados en el diseño** (secciones §6.1, §6.2, §6.3, §9
actualizadas). Detalle de cómo se cerró cada uno:

- **B1 — reconciliación T5 no cableada + reutilización peligrosa.** §6.2(b) reescrita: se construye un
  reconciliador **dedicado** `TryReconcilePartialCreditNoteT5Async` (molde `TryReconcileDebitNoteAnnulmentAsync`),
  cableado en `ProcessInvoiceJob`, lookup por hija `CreditNoteInvoiceId == nc.Id` + BC puramente parcial. Se dejó
  con advertencia dura en callout: **NO llamar `OnArcaSucceededAsync`/`ApplyChildResultAndReevaluateAsync`**
  (dispararían anulación TOTAL fantasma + ND fantasma). Test T-B1 (contra-test) obligatorio.
- **B2 — link de la hija diferido.** §6.1: pasó de "Recomendado" a **INVARIANTE DURA** en callout — la hija
  `BookingCancellationCreditNote` nace con `CreditNoteInvoiceId = nc.Id` en la MISMA transacción (si no, el cap
  reserva el `ImporteTotal` completo, `:4930-4934`). Test T-B2 obligatorio.
- **B3 — decisión del escalar del padre + §6.3 mal encuadrada.** Decisión tomada con evidencia: **NO se setea
  `CreditNoteInvoiceId` del BC padre** (§6.1). Eso deja el BC T5 fuera del predicado del re-vinculador de ND
  huérfana por construcción (rompe `CreditNoteInvoiceId != null`), sin tocar el re-vinculador. El costo (la plata
  no discrimina por el padre) se cubre **endureciendo el discriminador** para que lea la HIJA (§6.2(a)), lo que
  además fija el borde "última porción" determinísticamente (siempre reversal parcial para T5). §6.3 reescrita:
  el análisis ahora es contra el **predicado real** del re-vinculador, no contra "la NC no entra". Tests T-B3 +
  T-derivación-borde obligatorios.

**Insumos que el reviewer dejó "no verificado" — CERRADOS por lectura ahora (no diferidos al test):**
- `CancelServiceAsync` **SÍ setea `OriginatingInvoiceId`** en el BC T5 (`BookingCancellationService.cs:1098`,
  `= anchorInvoiceId`; columna `int` no-nullable, siempre poblada).
- `PenaltyStatus == Confirmed` **SÍ es alcanzable en una parcial** (default del BC `Estimated`,
  `BookingCancellation.cs:301`). **Corrección Rev 3 (contradicción que señaló el reviewer):** el flujo que la
  lleva a `Confirmed` es `ConfirmPenaltyAsync`, hoy gateado por `bc.CreditNoteInvoiceId != null` (`:6268`) — el
  escalar que B3 deja `null`. Sin C1 eso sería un deadlock (nunca `Confirmed`). **Con C1 (§6.4)**
  `ConfirmPenaltyAsync` es child-aware: ve la NC viva por la HIJA, así que la multa SÍ se confirma **sin** setear
  el escalar del padre. Por eso las dos cosas son verdad a la vez: el BC T5 llega a `Confirmed +
  DebitNoteInvoiceId==null` (alcanzable), y aun así queda FUERA del predicado del re-vinculador porque
  `CreditNoteInvoiceId (padre) == null`. La defensa robusta es esa combinación (escalar padre null + lectores
  child-aware), no confiar en que `Confirmed` sea inalcanzable.
- `DebitNoteInvoiceId` de un BC T5 sin ND emitida es `null` → las OTRAS dos condiciones del predicado del
  re-vinculador SÍ pueden cumplirse. Confirma que B3 (no setear el escalar del padre) es la única defensa por
  construcción. El test T-B3 lo fija con el peor caso (parcial CON multa confirmada).

**Mejoras no bloqueantes del reviewer (registradas):**
- **M1 (barrido de lectores de `AnnulmentStatus`):** se agrega a §9 un test de barrido que enumera qué lectores
  NUNCA deben usar `AnnulmentStatus` como proxy de "cuánto queda vivo" (el remanente se deriva SIEMPRE del cap,
  V2). Riesgo real es el inverso ("`!=Succeeded` ⇒ nada acreditado"): ningún lector actual lo hace; el test lo
  canda hacia adelante. Se aborda en implementación (backend-dotnet-senior enumera y el reviewer valida).
- **M2 (muerte del job entre confirm y `CreateAsync`):** **orden de escritura que lo hace imposible** — la NC
  Pending (`CreateAsync`/`CreatePendingInvoice`) y la hija con `CreditNoteInvoiceId=nc.Id` se crean ANTES o en la
  misma unidad que la transición del BC a `AwaitingFiscalConfirmation`; si el proceso muere antes de crear la NC,
  el BC sigue `Drafted` y reaparece en la bandeja "Pendiente de emisión" (sin hueco). Se documenta el orden como
  requisito de implementación; se descarta depender de un barrendero nuevo.
- **M3 (`bc.DraftedAt` como fecha del hecho RG 4540):** aceptado como proxy MVP, anotado como deuda (afinar a la
  fecha real de anulación del servicio) en §10.

**Sin cambios en la recomendación central (una NC por evento) ni en el alcance MVP.** No se agregan migraciones
nuevas ni flags. Pendiente: re-review del `software-architect-reviewer` sobre esta Rev 2, luego el resto de los
gates (§15).

---

## 20. Re-review Rev 3 (software-architect-reviewer, 2026-07-15) — verificación final de C1

**Veredicto: APROBADO PARA CONSTRUIR con UNA condición de implementación vinculante (C2).** La elección de la
Opción 1 es la correcta y la dirección de C1 es sólida. Pero la verificación enfocada que pidió el coordinador
destapó un **matiz semántico real**: el helper que C1 reusa responde una pregunta distinta de la que estos gates
necesitan. No re-bloqueo el diseño (la integridad fiscal está protegida por el gate de estado), pero C2 debe
respetarse al codificar, o se reintroduce el anti-patrón "botón que rebota" en una bandeja fiscal.

### Pregunta (1) del coordinador — ¿el gate de la ND espera el CAE de la NC, o alcanza la hija creada?

**Los lectores originales exigían CAE, no "NC creada".** El mensaje de INV-ADR014-001 lo dice literal: "la nota
de crédito al cliente aún no está confirmada por la AFIP". En el flujo legacy el escalar `bc.CreditNoteInvoiceId`
del padre se setea SOLO al CAE (`OnArcaSucceededAsync`), así que "escalar != null" == "NC con CAE".

**El helper que C1 reusa NO tiene esa semántica.** `BcHasLiveCreditNoteChildAsync` (`:1683-1686`) devuelve true
si la hija está `Succeeded` **O** `CreditNoteInvoiceId != null`. Su doc (`:1677-1679`) es explícita: "NC viva =
hija Succeeded (CAE aprobado) **o Pending con una NC ya creada**". Fue construido para la pregunta de liberación
INV-081 ("¿hay alguna NC en vuelo que no debo pisar?"), donde **Pending cuenta a propósito**. Esa es la pregunta
OPUESTA a la del gate de orden de la ND ("¿la NC al cliente ya tiene CAE?").
Y por B2 la hija T5 nace con `CreditNoteInvoiceId = nc.Id` en estado `Pending`, **antes** del CAE. Entonces el
helper devuelve true para un BC T5 apenas se emite la NC (Pending), no cuando obtiene CAE.

**Por qué los 5 guards de UN BC quedan igualmente a salvo (verificado):** el gate real es
`!PostCreditNoteStatuses.Contains(bc.Status) || <cond escalar/hija>`, y `PostCreditNoteStatuses` = {AwaitingOperatorRefund,
ClientCreditApplied, Closed, AbandonedByOperator} (`:5524-5530`) — **excluye** `AwaitingFiscalConfirmation`, el
estado pre-CAE en el que queda el BC T5 al emitir (§6.1). El BC T5 sólo entra a un estado de ese set cuando
`TryReconcilePartialCreditNoteT5Async` lo avanza AL LLEGAR EL CAE (§6.2b), y en ese mismo momento la hija pasa a
`Succeeded`. O sea: el **gate de estado** es el que impone "post-CAE"; la condición de la hija nunca decide sola.
Correcto — pero **frágil**: la corrección depende de que el gate de estado co-gatee SIEMPRE. Reusar el helper
importa la semántica equivocada y la deja "accidentalmente segura".

### Pregunta (2) — la subconsulta de la bandeja de fee: NO tiene gate de estado → reintroduce el "botón que rebota"

La bandeja de fee de agencia (`:4110-4118`) filtra por `CreditNoteInvoiceId != null && DebitNoteInvoiceId == null
&& PenaltyStatus == Estimated && ConceptKind ∈ {fee}` — **sin** filtro de estado. Su intención documentada
(`:4091-4093`) es "cargo de agencia Estimado, **con la NC total ya emitida** (CAE) y sin ND". El único término
que codifica "NC con CAE" es `CreditNoteInvoiceId != null`.
La subconsulta child-aware que propone §6.4 —
`... || _db.BookingCancellationCreditNotes.Any(c => c.BookingCancellationId == b.Id && (c.Status == Succeeded ||
c.CreditNoteInvoiceId != null))` — incluye la rama `CreditNoteInvoiceId != null`, que para T5 es **true en
Pending (pre-CAE)**. Efecto: un BC T5 con fee de agencia aparecería en la bandeja **antes** de que su NC al
cliente tenga CAE. Al clickear la fila, el front abre `ConfirmPenaltyModal` → `ConfirmPenaltyAsync`, que SÍ tiene
el gate de estado (`:6268`) y **rebota** (el BC está en `AwaitingFiscalConfirmation ∉ PostCreditNoteStatuses`).
Resultado: fila fantasma que al tocarla da error — exactamente el anti-patrón "botón que rebota" que este módulo
combate y que la regla UX del dueño prohíbe. No rompe integridad fiscal (la emisión sigue gateada), pero es un
defecto real y evitable.

### C2 (condición de implementación, vinculante)

Para los lectores de **orden de la ND** (los 5 guards de multa + la bandeja de fee), la condición child-aware
debe keyear en **`hija.Status == Succeeded`** (CAE obtenido), **NO** en el helper "live-or-pending" ni en la rama
`CreditNoteInvoiceId != null`. Esa es la semántica exacta que los lectores originales exigían ("la NC al cliente
ya está confirmada por AFIP") y la que corresponde a la hija T5 (cuyo link vive en Pending por B2).
- **Bandeja de fee (`:4110`):** la subconsulta debe ser `_db.BookingCancellationCreditNotes.Any(c =>
  c.BookingCancellationId == b.Id && c.Status == Succeeded)`. Es lo que la saca del "botón que rebota". **Crítico**
  (esta bandeja no tiene gate de estado que la cubra).
- **5 guards de UN BC:** hoy quedan correctos por el gate de estado, pero preferir igualmente la condición
  `Status == Succeeded` (self-contained, no depende de que el gate de estado siga load-bearing ante un refactor).
  Si el equipo decide reusar el helper tal cual, debe **documentar** que el gate `PostCreditNoteStatuses` es
  load-bearing para el orden fiscal y **NO** puede relajarse sin re-derivar esta prueba.
- **NO tocar** el uso legacy del helper en `:1513` (liberación INV-081): ahí "Pending cuenta" es correcto. C2
  aplica sólo a las lecturas de orden de la ND que agrega C1 — no unifiquen las dos semánticas bajo un helper.

### Tests que agrego a la lista (obligatorios por C2)

- **T-C2a (gate pre-CAE):** BC T5 recién emitido, en `AwaitingFiscalConfirmation`, hija `Pending` con
  `CreditNoteInvoiceId` seteado → `ConfirmPenaltyAsync`/`WaivePenaltyAsync` rechazan (aún sin CAE) **y** la
  bandeja de fee **NO** lista el BC. Es el test que prueba que no reintrodujimos el "botón que rebota".
- **T-C2b (post-CAE):** llega el CAE (hija `Succeeded`, BC en `AwaitingOperatorRefund`) → confirmar/renunciar
  funcionan y la bandeja lista el fee. Cierra el ciclo de C1.

### Estado de los cierres previos
- **B1, B2:** cerrados y verificados (Rev 2). Sin cambios.
- **B3 / C1:** la Opción 1 (escalar padre `null` + lectores child-aware) es correcta y la contradicción del §17
  quedó bien resuelta (Confirmed alcanzable vía la hija; el re-vinculador lee el escalar crudo `null` → no
  matchea). Sólo resta el afinamiento semántico C2 (CAE vs NC-creada) al implementar.

**Con C2 respetado (condición child-aware = `Status == Succeeded` para los lectores de orden de la ND, empezando
por la bandeja de fee) + T-C2a/T-C2b: APROBADO PARA CONSTRUIR.** No quedan bloqueantes de arquitectura. Siguen
los gates fiscal/contable, seguridad, data-exposure, UX y QA (§15).

---

## 18. Re-review Rev 2 (software-architect-reviewer, 2026-07-15) — enfocada en los cierres

**Veredicto: APROBADO CON UNA CONDICIÓN DURA (C1) + verificaciones.** B1 y B2 quedan **cerrados y verificados
contra el código**. B3 está **cerrado sólo a medias**: el endurecimiento del discriminador de plata (§6.2a) es
correcto y necesario, pero la Decisión B3 (dejar `CreditNoteInvoiceId` del BC padre en `null`) tiene un **radio
de impacto mayor al auditado** — hay más lectores del escalar del padre que esperan verlo seteado, y §6.2a sólo
arregló uno (la plata). Emite comprobantes fiscales contra ARCA: no lo apruebo para construir hasta cerrar C1.

### B1 — CERRADO (verificado)
El reconciliador dedicado `TryReconcilePartialCreditNoteT5Async` cableado en `ProcessInvoiceJob`, con lookup por
hija `CreditNoteInvoiceId==nc.Id && Status==Pending` **filtrando a BC puramente parcial (≥1 Partial, 0 Full)**,
es correcto y el filtro excluye limpiamente las hijas de anulación total ADR-042 (esas tienen líneas `Full`,
`BuildCancellationLinesAsync(..., Full, ...)`). El callout "⚠️ NO llamar `OnArcaSucceededAsync`" es exacto:
verifiqué que `OnArcaSucceededAsync`→`ApplyChildResultAndReevaluateAsync` con la única hija en `Succeeded`
evalúa `AllSucceeded` y dispara `TryEmitDebitNotePostCompletionAsync` (`BookingCancellationService.cs:4759-4761`)
— ND fantasma sobre una parcial. Punto de inserción correcto (`AfipService.cs:1566-1577`), molde correcto
(`TryReconcileDebitNoteAnnulmentAsync:1627`). Contra-test T-B1 adecuado. Sólido.

### B2 — CERRADO (verificado)
La invariante "la hija nace con `CreditNoteInvoiceId=nc.Id` en la misma transacción" está bien fundada:
confirmé que el cap trata una hija `Pending` SIN link como **NC TOTAL en camino** y reserva el `ImporteTotal`
completo (`BookingCancellationService.cs:4930-4934`). Volverla invariante dura + T-B2 es la decisión correcta.
Sólido.

### B3 — CERRADO A MEDIAS (condición C1)
El endurecimiento del discriminador de plata (leer la HIJA en `ApplyCreditNoteEconomicReversalAsync` antes del
fallback por monto, y siempre reversal parcial para T5, incluso última porción) es **correcto** y de paso cierra
bien el borde §15-IR. Pero el diseño afirma (§6.2a) que "el costo — que la plata no pueda discriminar por el
padre — se cubre endureciendo el discriminador". **Ese NO es el único costo.** El escalar `bc.CreditNoteInvoiceId`
del padre tiene ~5 lectores de producción más, y varios significan "la NC al cliente YA se emitió, ahora la pata
de multa/ND puede avanzar". Dejarlo `null` para T5 los rompe:

- **`ConfirmPenaltyAsync` — `BookingCancellationService.cs:6268` (INV-ADR014-001):** exige
  `bc.CreditNoteInvoiceId != null`. Con B3 (escalar null para siempre en T5) tira **siempre** "la nota de crédito
  al cliente aún no está confirmada por la AFIP" — aunque SÍ lo esté. La multa de operador de una cancelación
  parcial **nunca podría confirmarse**.
- **`WaivePenaltyAsync` — `:7431` (INV-WAIVE-001):** idéntico gate → **nunca podría cerrarse "sin multa"**.
- **`RetryDebitNoteAsync` — `:6643` (INV-ADR014-RETRY-003):** idéntico gate → la ND de multa de una parcial
  **nunca podría (re)emitirse**.
- **`EvaluateCanConfirmPenalty` — `:5581`:** devuelve `CreditNoteNotYetIssued` → la ficha muestra el botón
  bloqueado para siempre.
- **Bandeja de multa de agencia (fee) — `:4110-4118`:** predicado
  `CreditNoteInvoiceId != null && DebitNoteInvoiceId == null && PenaltyStatus == Estimated && ConceptKind ∈
  {AgencyManagementFee, AgencyCancellationFee}`. Un BC T5 con escalar null **nunca aparece** aquí → un cargo de
  gestión/cancelación de la agencia sobre una parcial quedaría invisible en su bandeja.

**Contradicción interna en el propio §17:** el registro de cierre afirma (líneas 704-707) que
`PenaltyStatus == Confirmed` **es alcanzable** en una parcial "vía el mismo flujo `confirm-penalty` que lleva el
escalar del padre a Confirmed". Pero ese flujo ES `ConfirmPenaltyAsync`, que está gateado por
`bc.CreditNoteInvoiceId != null` (`:6268`) — el escalar que B3 garantiza null. O sea: **o** la multa NO se puede
confirmar para T5 (el flujo se rompe, deadlock), **o** existe otro camino que la confirma sin pasar por ese gate
y sin setear el escalar (y entonces el predicado del re-vinculador tampoco matchea, con lo cual la justificación
de riesgo de B3 se cae). Ambas ramas contradicen el texto actual. Hay que resolver cuál es verdad.

**Precedente en el código que marca el camino:** ya existe un lector que fue hecho **child-aware** exactamente
por esto — `:1513-1514` mira `existingBc.CreditNoteInvoiceId is not null || BcHasLiveCreditNoteChildAsync(...)`
porque "el puntero singular puede ser null y aún así existir una hija con NC viva". Ése es el patrón correcto.

**Lo que verifiqué que NO se rompe (para acotar C1):**
- **Extracto / cuadro de facturación:** NO leen el escalar del BC (V7 confirmado, `ReservaService.cs:240-264`
  categoriza por `Resultado`). Sin impacto.
- **Guard de revert de reserva Cancelada** (`ReservaService.cs:1602-1608, 1704-1710`, lee el escalar): sí lo lee,
  pero corre sólo con `Status==Cancelled` y **detrás** del hard-block `hasInvoiceWithCae` (`:1680-1682`) que
  bloquea cualquier reserva con un CAE vivo — y una parcial T5 emitida SIEMPRE tiene la venta + la NC con CAE. No
  hay hueco de revert. (Cosmético: el DTO de opciones podría ofrecer un revert que luego 409ea; menor.)
- **`isReusableDraft`** (`:1437-1440`): un BC T5 emitido ya no está `Drafted`; sin impacto.

### C1 (condición dura, previa a construir)
Resolver el radio de B3 sobre los lectores del escalar del padre. Dos caminos válidos, hay que **elegir uno y
trazarlo**:
1. **Escalar null + hacer child-aware a los guards de multa/fee** (patrón `:1513`): que INV-ADR014-001/RETRY-003/
   INV-WAIVE-001, `EvaluateCanConfirmPenalty` y la bandeja `:4110` lean "hija con NC viva" además del escalar.
   Mantiene la defensa por construcción contra el re-vinculador, pero toca 5 sitios (más superficie).
2. **Setear el escalar del padre + excluir el BC T5 del re-vinculador por otro medio** (p.ej. el guard ADR-044 ya
   presente en `:4020-4025`, o un check `CreditNoteKind==PartialOnOriginal` en el predicado `:4005`). Menos
   sitios que tocar, pero hay que probar la exclusión con el peor caso.
Si en cambio el equipo decide que **una parcial NUNCA lleva multa de operador ni fee de agencia en el MVP**,
entonces: (a) declararlo explícito en §10 (fuera de alcance), (b) corregir §17 porque la justificación de la
colisión B3 se apoya en un `PenaltyStatus==Confirmed` que dejaría de ser alcanzable, y (c) agregar un guard que
rechace confirmar multa sobre un BC puramente parcial hasta que exista el flujo — para que nadie quede en el
deadlock silencioso.

### Tests que agregaría por C1
- **T-C1a (deadlock de multa):** parcial T5 emitida con CAE + intento de `ConfirmPenaltyAsync`/`WaivePenaltyAsync`
  sobre su operador → debe poder resolverse (no INV-ADR014-001/INV-WAIVE-001 perpetuo). Si el MVP la excluye, el
  test verifica el rechazo EXPLÍCITO (no el 409 de "NC no emitida" mentiroso).
- **T-C1b (bandeja de fee de agencia):** parcial T5 con `ConceptKind=AgencyManagementFee` → aparece en su bandeja
  (o se documenta y prueba que está fuera de alcance).

### Cierre
B1/B2: aprobados. B3: el endurecimiento de plata es correcto pero incompleto; C1 debe cerrarse antes de escribir
código, porque toca emisión de ND fiscal y un deadlock silencioso de la pata de multa es exactamente la clase de
"flag stuck / no factura" que ya mordió a este módulo. Con C1 resuelto (cualquiera de las 3 salidas) → APROBADO
PARA CONSTRUIR.

---

## 19. Cierre de C1 (Rev 3, 2026-07-15) — decisión y trazado

**C1 resuelto por la Opción 1: escalar del padre `null` para T5 + lectores child-aware.** Trazado completo en
**§6.4** (nueva). Elegida por el criterio del dueño (robustez / defensa por construcción tras el limbo de esta
semana):
- **Opción 1 (elegida):** mantiene la defensa POR CONSTRUCCIÓN contra el re-vinculador de ND huérfana — el BC T5
  nunca entra al predicado porque `CreditNoteInvoiceId (padre) == null`. El costo (5 lectores de multa/fee) se
  cubre extendiendo el patrón child-aware que **ya existe** (`BcHasLiveCreditNoteChildAsync`, `:1681`, ya usado
  en `:1513-1514` por ADR-042). Cada cambio es mecánico y con precedente; baja consecuencia por sitio; totalmente
  testeable (T-C1a/b).
- **Opción 2 (descartada):** setear el escalar + excluir el BC T5 del re-vinculador es defensa por lógica frágil,
  en el mismo punto que rompió prod esta semana. Un refactor futuro reabre el limbo.
- **Opción 3 (descartada):** excluir multa/fee de una parcial viola "contemplar todo con defaults" — es un caso
  normal del negocio, no un opcional.

**Radio de B3 acotado y cerrado:**
- Lectores que se hacen child-aware (5): `ConfirmPenaltyAsync` (`:6268`), `WaivePenaltyAsync` (`:7431`),
  `RetryDebitNoteAsync` (`:6643`), `EvaluateCanConfirmPenalty` (`:5581`), bandeja de fee (`:4110-4118`). Los 4
  primeros (por-BC) reusan el helper existente; la bandeja (lista) usa la subconsulta traducible (sin N+1).
- Lectores verificados que NO se rompen (acotan el radio): extracto/cuadro de facturación (leen `Resultado`, no
  el escalar, V7); guard de revert de reserva Cancelada (corre detrás del hard-block `hasInvoiceWithCae`, sin
  hueco); `isReusableDraft` (un BC T5 emitido ya no está `Drafted`).

**Contradicción del §17 corregida:** `PenaltyStatus == Confirmed` es alcanzable en una parcial **gracias a C1**
(el `ConfirmPenaltyAsync` child-aware ve la NC por la hija, sin escalar del padre); el re-vinculador sigue
mirando el escalar crudo (`null`) → no matchea. Las dos verdades coexisten sin contradicción.

**Tests agregados:** T-C1a (no deadlock de multa: confirmar/renunciar/reemitir resuelven), T-C1b (el fee de una
parcial aparece en su bandeja). Se suman a §9 (tests 19-20).

Sin migraciones nuevas, sin flags, sin cambios en la recomendación central ni en el alcance MVP (una parcial SÍ
puede llevar multa de operador / fee de agencia — contemplado por default). Pendiente: re-review de esta Rev 3
por `software-architect-reviewer`; con el visto → APROBADO PARA CONSTRUIR y sigue la cadena de gates (§15).
