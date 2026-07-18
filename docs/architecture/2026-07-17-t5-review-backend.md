# ADR-048 T5 — Review backend (materialización de ejes secundarios)

> Revisor: backend-dotnet-reviewer. Fecha: 2026-07-17.
> Alcance revisado (diff sin commitear): `Reserva.cs`, `AppDbContext.cs`,
> `AppDbContextModelSnapshot.cs`, `ReservaMoneyPersister.cs`, `ReservaService.cs`,
> `ReservaDerivedAxesProjector.cs` (nuevo), migración
> `20260718012634_Adr048_M2_AddDerivedStatusColumnsToReserva` (+ Designer), y 3 archivos de test.
> Contexto: `docs/architecture/2026-07-17-modelo-estados-derivados-DISENO-implementacion.md` §T5.
> **No se corrió la app real ni el CI**: es review sobre código estático. Todo lo que afirmo
> "anda / no anda" está anclado a `archivo:línea` que leí.

---

## 1. Veredicto

**Changes required.**

Un bloqueante concreto (B1): el eje de facturación materializado se queda **viejo** al emitir una
factura de venta, y como el listado ahora **confía en la columna**, el listado va a mostrar "Sin
facturar" mientras el detalle muestra "Facturada total" — exactamente la mentira #2 que ADR-048 vino
a matar, reintroducida por T5. Además, la premisa del brief de que "el test de integración compara
SQL-backfill vs C#" es **falsa**: ningún test ejercita el SQL crudo del backfill (riesgo central de la
tanda), por lo que la equivalencia SQL↔C# queda verificada solo por inspección.

La lógica de equivalencia rama-por-rama (proyector C# ↔ SQL backfill ↔ derivación en vivo) la revisé
y **coincide** para todos los datos reales; el escritor único y el B3 están bien resueltos. El
bloqueante no es la matemática: es la **cobertura del refresco** del eje de facturación.

---

## 2. Hechos verificados

- **Escritor único, mismo `SaveChanges` (regla 9).** `ReservaMoneyPersister.PersistAsync`
  (`ReservaMoneyPersister.cs:101-102`) escribe las dos columnas antes del `SaveChangesAsync`
  (`:116`), en la misma pasada que el escalar y la tabla hija. El proyector no hace queries extra:
  el cobro sale del `ReservaMoneySummary` ya calculado y la facturación de `reserva.Invoices` ya
  incluido (`:70,78`).
- **B3 respetado y blindado.** `ReservaDerivedAxesProjector.ProjectCollectionStatus`/`ProjectInvoicingStatus`
  (`ReservaDerivedAxesProjector.cs:42-70`) NO leen `Reserva.Status`; derivan del saldo por moneda y
  del cuadre de comprobantes. El test `Persist_ConReservaEnCualquierEstadoDelParTerminal_EscribeElMismoEje`
  (`Adr048T5DerivedAxesPersisterTests.cs:100-123`) fuerza el par `PendingOperatorRefund`→`Cancelled`
  sin tocar la plata y exige el mismo eje. **El claim del concern #3 es correcto.**
- **Equivalencia eje de COBRO (con filas hijas).** Proyector `ProjectCollectionStatus`
  (`:44-49`, `hasCharges: ConfirmedSale>0`, `hasPayments: TotalPaid>0`) == derivación en vivo del
  listado (`ReservaService.cs:2399-2403`) == SQL backfill 1
  (`migración:82-100`, `bool_or(Balance>0.005)`, `bool_or(Balance<-0.005)`, `bool_or(ConfirmedSale>0 OR TotalPaid>0)`).
  Prioridad `ConDeuda>SaldoAFavor>Saldado>SinMovimientos` idéntica en las tres. Umbral `0.005`
  idéntico (`ReservaCollectionStatus.cs:65`).
- **Equivalencia eje de COBRO (fallback sin filas hijas).** SQL backfill 1b
  (`migración:114-125`, `Balance<>0 OR TotalPaid>0` para actividad) == fallback en vivo
  (`ReservaService.cs:2359-2362`, `hasCharges = Balance != 0`, `hasPayments = TotalPaid>0`). Traza
  de bordes (Balance en `[-0.005, 0.005]`, `0.003`, `0`) coincide. `NOT EXISTS` en vez de `NOT IN`
  correcto (evita el bug de NULL); comentario acertado (`migración:107-112`).
- **Equivalencia eje de FACTURACIÓN.** Proyector `ProjectInvoicingStatus`
  (`ReservaDerivedAxesProjector.cs:58-70`) usa `ReservaInvoicingCuadreCalculator.Calculate` +
  `ReservaInvoicingStatus.Derive`, misma cadena que el detalle (`ReservaService.cs:2654`). SQL
  backfill 2 (`migración:133-151`) replica: `NC IN (3,8,13,53)` resta para neto y aporta 0 para
  bruto; ramas `neto<=0.005 → (bruto>0.005 ? FullyReturned : NotInvoiced)`, `neto>=TotalSale-0.005 →
  FullyInvoiced`, sino `PartiallyInvoiced` — **byte-idéntico** a `ReservaInvoicingStatus.Derive`
  (`ReservaInvoicingStatus.cs:80-89`). Backfill 2b (`migración:163-170`) `NotInvoiced` para reservas
  sin comprobante `Resultado='A'` == default del DTO. `FullyReturned` (bruto>0, neto≈0) verificado en
  las tres superficies.
- **Nombres reales en el SQL.** `TravelFiles` (PK `Id`), `ReservaMoneyByCurrency.ReservaId` sin
  remapeo, `Invoices.TravelFileId` (la propiedad C# `ReservaId` remapea a `HasColumnName("TravelFileId")`,
  `AppDbContext.cs:735`). El SQL usa `"TravelFileId"` en el join de facturas (`migración:135,168`) —
  **correcto**, no cae en la trampa clásica del repo.
- **`Invoices` sin query filter.** Los únicos `HasQueryFilter(!IsDeleted)` son `Payment`
  (`AppDbContext.cs:637`) y `SupplierPayment` (`:1309`). `Invoice` no tiene filtro, así que el
  `Include(Invoices)` del proyector, la query del listado y el SQL crudo del backfill ven el **mismo
  conjunto** de comprobantes (sin divergencia por soft-delete).
- **Migración aditiva, `Down` limpio, sin 42701.** `Up` solo `AddColumn` nullable + índices +
  `UPDATE` idempotentes; `Down` (`migración:174-191`) dropea índices y columnas. Los tres
  bootstrappers (`BnaExchangeRateSchemaBootstrapper`, `OperationalFinanceSchemaBootstrapper`,
  `RefreshTokenSchemaBootstrapper`) no tocan el schema de `TravelFiles`; columnas nuevas ⇒ imposible
  el `42701`. Designer con `[Migration]` y `BuildTargetModel` correctos; snapshot actualizado
  (2 props + 2 índices).
- **El eje de COBRO no se queda viejo.** Toda mutación de plata pasa por
  `ReservaMoneyPersister.PersistAsync` (verificado: `PaymentService:1394`, `AfipService:2268`,
  `BookingCancellationService:486/555/851`, `ReservaService:1374/5247`, `ClientCreditService`,
  `OverpaymentCreditConverter:137`, `DebitNoteAnnulmentReconciliation:255`, `CoherenceMoneyRecalculator:159`).
  Como el cobro deriva solo del saldo, su columna siempre se re-escribe. **El detalle quedó intacto**
  (`ReservaService.cs:2627,2654` siguen recalculando en vivo).

---

## 3. Blocking issues

### B1 — `DerivedInvoicingStatus` se queda viejo al emitir una factura de venta ⇒ el listado vuelve a mentir

**Qué pasa.** La emisión de un comprobante de venta corre en `AfipService.ProcessInvoiceJob`: setea
`Resultado="A"` y guarda (`AfipService.cs:1586-1601`). Para una **factura de venta** (no NC/ND),
`IsCreditNote` es false ⇒ NO entra a `ApplyCreditNoteEconomicReversalAsync` (que es el único lugar
que llama al persister, vía `RecalculateReservaBalanceAsync`, `AfipService.cs:1605→1857→2268`). Los
otros reconciliadores del bloque (`:1610,1614,1621`) son no-ops para una factura de venta. Y como
facturar **no cambia el saldo** (desacople ADR-037), ningún otro camino refresca la columna.

**Consecuencia.** Tras emitir la factura, `DerivedInvoicingStatus` queda en su valor viejo
(típicamente `NotInvoiced`, escrito en el último movimiento de plata). El **listado** ahora lee la
columna directa cuando no es null (`ReservaService.cs:2439-2441`) ⇒ muestra "Sin facturar". El
**detalle** sigue recalculando en vivo (`:2654`) ⇒ muestra "Facturada total". **El listado y el
detalle divergen** = la mentira #2 que ADR-048 cierra, reintroducida por T5.

**Cuándo se da (común).** Reserva ya cobrada al 100% y después facturada (flujo habitual de facturar
post-pago): no hay movimiento de plata posterior que auto-sane la columna, así que la mentira es
**permanente** hasta la próxima mutación de plata. Solo se auto-sana si un pago/cancelación ocurre
DESPUÉS de la factura.

**Por qué es bloqueante.** El norte declarado del dueño (memoria 2026-07-17) es frenar features hasta
que las pantallas dejen de mentir. T5 tal cual **empeora** la coherencia del eje de facturación en el
listado. El listado hoy (pre-T5) recalcula en vivo y es correcto; T5 lo hace confiar en una columna
que ningún camino de emisión actualiza.

**Fixes posibles (elige negocio/arquitectura; no toqué código):**
1. Al final de la emisión exitosa de un comprobante de venta (`AfipService.cs:1601`, tras el
   `SaveChanges` del CAE), llamar a `ReservaMoneyPersister.PersistAsync` (o un refresco liviano
   solo-de-ejes) para re-materializar `DerivedInvoicingStatus`. Es el chokepoint que ya usan NC/ND.
2. O NO materializar el eje de facturación y dejar que el listado lo siga recalculando en vivo
   (`FillInvoicingStatusForListAsync` ya lo hace bien): materializar **solo** el cobro, que sí pasa
   siempre por el persister. Pierde el "filtrar por Facturada y devuelta indexado", pero elimina la
   fuente de mentira.

Cualquiera de las dos exige el test de regresión de la sección 6 (MT2).

---

## 4. Non-blocking improvements

- **N1 — Divergencia conocida por tipo de comprobante corrupto (documentada, real-data-safe).** El
  SQL backfill y el listado en vivo tratan cualquier `TipoComprobante ∉ {3,8,13,53}` como factura
  (suma neto y bruto), mientras el proyector del persister usa el calculador de dominio
  (`InvoiceComprobanteHelpers.Categorize` ⇒ `Unknown` no cuenta, `ReservaInvoicingCuadreCalculator.cs:181`).
  Ya está anotado en `ReservaService.cs:2472-2476` (M2 de review previo) como pre-existente. Efecto
  nuevo de T5: una reserva **rellenada por la migración** vs **re-escrita por el persister** podría
  FLIPear su eje de facturación **solo** si tiene un CAE aprobado de tipo desconocido (no debería
  existir en datos reales). Riesgo bajo; conviene que el SQL backfill y el proyector usen el MISMO
  criterio de "qué tipo suma" para no tener dos familias.
- **N2 — `Include(Invoices)` en cada mutación de plata.** El persister ahora carga todos los
  comprobantes de la reserva en cada pago/cancelación/recalc (`ReservaMoneyPersister.cs:78`), aunque
  la plata no dependa de ellos. Costo chico (pocas facturas por reserva), pero es carga nueva en el
  camino caliente. Aceptable; dejar anotado.
- **N3 — Query extra en el listado.** `FetchMaterializedDerivedAxesAsync` (`ReservaService.cs:2277-2295`)
  hace una segunda pasada sobre las mismas reservas de la página solo para traer 2 columnas. Se podría
  plegar en la proyección paginada. Es 1 query batcheada (no N+1), impacto menor.
- **N4 — Índices de baja cardinalidad.** Los dos índices btree son sobre columnas de 4 valores
  distintos (`AppDbContext.cs:503-504`): poco selectivos. Sirven para el filtro/orden futuro, pero
  su beneficio real es marginal; ojo con no asumir que aceleran mucho.
- **N5 — cosmético.** El timestamp de la migración es `20260718...` (2026-07-18, un día "en el
  futuro" respecto de hoy). Sin efecto funcional.

---

## 5. Security / data risks

- Sin exposición nueva de datos sensibles: las columnas guardan un enum-string de negocio
  ("ConDeuda", "FullyReturned", etc.), no IDs internos ni montos. El listado ya exponía estos ejes.
- Sin riesgo de integridad en la migración: es **aditiva pura** (columnas nacen null, el backfill
  solo rellena, `Down` dropea). No toca `Status` ni plata. Deploy stop-then-start (§3 del diseño)
  evita solape de escritura.
- **Ojo de coherencia (no seguridad clásica):** B1 crea un estado inconsistente
  paga-facturada-pero-listado-dice-sin-facturar. Es riesgo de **integridad de lectura**, no de PII.

---

## 6. Missing tests

- **MT1 (importante) — El SQL crudo del backfill NO tiene cobertura automatizada.** La premisa del
  brief ("el de integración lo hace") es **incorrecta**: `Adr048T5DerivedAxesIntegrationTests` escribe
  la columna vía `ReservaMoneyPersister.PersistAsync` (`:123`), NO corriendo el `UPDATE`/`CASE` de la
  migración. Las 11 pruebas de equivalencia (`ReservaDerivedAxesProjectorTests`) cubren **solo el
  proyector C#**. Así, el riesgo central de la tanda (SQL == C#) queda verificado solo por inspección.
  Falta un test de integración Postgres que: siembre reservas legacy con columnas NULL y datos
  variados (con filas hijas / sin filas hijas / con CAE / sin CAE / NC total con bruto>0 y neto≈0 /
  bordes epsilon / ConDeuda+SaldoAFavor simultáneos), **corra el SQL del backfill**, y asserte que cada
  columna == lo que produce la derivación en vivo para el mismo dato. Debe cubrir las **4 ramas**
  (backfill 1, 1b, 2, 2b).
- **MT2 (liga con B1) — Falta el test que expone la mentira de emisión.** Agregar: reserva pagada al
  100% + emitir factura de venta ⇒ assert `listado.InvoicingStatus == detalle.InvoicingStatus ==
  FullyInvoiced`. Con el código actual **fallaría** (listado quedaría `NotInvoiced`), evidenciando B1.
- **MT3 — Falta cubrir el fallback sin filas hijas.** Los unit tests del proyector solo ejercen el
  camino con filas (via `summary.PorMoneda`). La equivalencia SQL 1b ↔ fallback en vivo
  (`ReservaService.cs:2359`) no tiene test directo.

---

## 7. Domain concerns

- El propósito de ADR-048 es coherencia de estados: una sola verdad, todas las pantallas la obedecen.
  B1 rompe eso para el eje de facturación en el listado. Hasta resolverlo, T5 no cumple su propia
  invariante INV-048-10 en la práctica (la columna NO refleja la derivación en vivo tras emitir).
- El eje de COBRO sí es coherente (siempre pasa por el persister) — la asimetría es que facturar está
  desacoplado de la plata (ADR-037) y por eso su materialización necesita un disparador propio.
- B3 (par `{Cancelled, PendingOperatorRefund}` con el mismo eje) está correctamente resuelto y
  testeado; no materializa la mentira B3.

---

## 8. Suggested fixes

1. **Resolver B1** con la opción 1 o 2 de la sección 3. Recomiendo la opción 1 (refrescar en la
   emisión exitosa del comprobante) para conservar el beneficio de listado indexado, salvo que negocio
   prefiera no materializar facturación.
2. **Agregar MT2** (regresión de emisión) — que hoy fallaría — y correrlo verde tras el fix.
3. **Agregar MT1** (backfill SQL real vs derivación en vivo, 4 ramas) contra Postgres, o —si no se
   agrega— **corregir la afirmación** del brief/tanda de que el de integración lo cubre (no lo hace).
4. **N1**: unificar el criterio "qué tipo suma" entre el SQL backfill y el proyector de dominio (o
   documentar explícito que se acepta la divergencia solo-para-datos-corruptos).
5. Verificar en PROD el conteo pre/post backfill (lección 2026-07-09 sobre SQL crudo).

---

## 9. Commands that should be run

- `dotnet test` de la suite unit (incluye `ReservaDerivedAxesProjectorTests` y
  `Adr048T5DerivedAxesPersisterTests`) — **no lo corrí**.
- Suite de integración Postgres (CI) incluyendo `Adr048T5DerivedAxesIntegrationTests` — **no lo corrí**
  (requiere el Postgres del fixture, no arranca local; DB en VPS).
- Tras aplicar la migración en un entorno con datos: conteo pre/post
  (`SELECT "DerivedInvoicingStatus", count(*) FROM "TravelFiles" GROUP BY 1`) contra PROD, y una
  verificación manual del flujo "facturar reserva pagada → mirar el listado" (el que expone B1).

---

## 10. No verificado

- **No corrí la app real ni el CI**: es review estático. No afirmo que la suite pase.
- **T1 (`ReservaTerminalTransitionApplier`, `allowTerminalCorrectionWithinPar`)**: aparece en el mismo
  `PersistAsync` pero es de T1, fuera del alcance de esta review; asumo que ya fue revisado.
- **`ReservaMoneyCalculator.Calculate`** internamente: asumí que `summary.PorMoneda` refleja lo que el
  persister escribe en `ReservaMoneyByCurrency` (leí `SyncMoneyByCurrencyRowsAsync`, es upsert directo
  del `PorMoneda`), pero no tracé el calculador completo.
- **No confirmé** que exista/aplique una migración M1 previa de T1 en el historial desplegado; el
  snapshot solo muestra los cambios de columnas de M2, consistente con que T1 usa `Status` (no agrega
  columna).
- **Cardinalidad real y volumen** de `TravelFiles`/`Invoices` en PROD: no medido (afecta el costo del
  backfill y del `Include(Invoices)`).

---

## 11. Re-review (2026-07-17) — fix de B1 + MT1/MT2/N1

**Veredicto: APROBADO con comentarios (Approved with comments).** El bloqueante B1 quedó resuelto
correctamente; lo que queda es no-bloqueante. **Corrijo una afirmación del coordinador (punto b).**

### (a) Call-sites — ¿son 3 y solo 3 los que meten un comprobante al cuadre? ✔ verificado

`.Resultado = "A"` tiene **exactamente 3** sitios de asignación en producción (el resto de los hits del
grep son comentarios): `AfipService.cs:1586` (POST directo), `AfipService.cs:2798`
(`HandleStaleInvoiceIdempotencyKeyAsync`, recovery de idempotencia) e `InvoiceService.cs:2589` (recovery
de NC parcial FC1.3). Los tres están cableados:
- `AfipService.cs:1603-1616`: NC → `ApplyCreditNoteEconomicReversalAsync` → `PersistAsync` **completo**
  (la NC sí mueve plata); no-NC → `RefreshInvoicingAxisForNonCreditNoteAsync` (refresco liviano).
- `AfipService.cs:2819-2830`: misma bifurcación en el path de recovery.
- `InvoiceService.cs:2609-2613`: refresco liviano de facturación.

**Reversas/anulaciones no se escapan.** `CountsInNetBilled` = `Resultado=="A"`; anular una factura NO
cambia su `Resultado` (la anulación vive en `AnnulmentStatus`), así que una factura anulada **sigue
contando** en el cuadre y su NC compensadora entra por el sitio 1/2 (rama NC → `PersistAsync` completo,
que también refresca facturación). Un rechazo `PENDING→"R"` nunca estuvo en el cuadre. Y los cambios de
`TotalSale` (alta/edición/cancelación de servicio) pasan por `PersistAsync` (refresca facturación en
`ReservaMoneyPersister.cs:102`). **No hay evento que cambie el cuadre por fuera de estos caminos.**

### (b) ¿El refresco corre en la MISMA `SaveChanges` de la emisión (regla 9)? ✖ NO literalmente

**Corrección al coordinador:** el refresco liviano **NO** comparte la `SaveChangesAsync` de la emisión.
La emisión commitea `Resultado="A"` en `AfipService.cs:1601` (su propia `SaveChanges`), y
`RefreshInvoicingAxisOnlyAsync` hace **otra** `SaveChangesAsync` (`ReservaMoneyPersister.cs:164`),
inmediatamente después. Es un commit **separado e inmediato** (misma ejecución del job, línea siguiente),
no atómico con el CAE.

Impacto (no-bloqueante): si el proceso crashea entre `:1601` y `:164`, la factura queda `"A"` pero la
columna queda vieja, y el **guard de reintento del job** (`AfipService.cs:1136`,
`if (invoice.Resultado == "A") return;`) hace que el retry de Hangfire **salga sin refrescar** → la
columna solo se auto-sana en la próxima mutación de plata (que en una reserva ya pagada podría no llegar).
Ventana angosta (dos `await` seguidos, sin I/O externo entre medio), y es **el mismo patrón** que ya usan
los otros side-effects post-CAE del job (`TryReconcileLinkedCancellationDebitNoteAsync`, etc.: todos
commitean aparte, blindados, y también se saltarían en el retry) — o sea, **consistente con el repo** y
**estrictamente mejor** que el B1 original (que mentía SIEMPRE, no solo tras un crash).

No lo marco bloqueante porque: (1) el "CAE primero, efectos después" es deliberado y correcto (nunca se
arriesga el CAE); (2) el refresco es **inmediato**, no "corrección diferida por la próxima mutación" — no
viola el espíritu de la regla 9; (3) la exposición es un crash sub-segundo. **Recomendación** (opcional,
endurecimiento): setear `reserva.DerivedInvoicingStatus` en memoria **antes** del `SaveChanges` de `:1601`
(reusando la reserva ya cargada) para un único commit atómico, o que el guard de reintento re-refresque
cuando detecte la columna desalineada.

### (c) ¿MT2 prueba el camino real? ¿MT1 compara de verdad SQL vs vivo?

- **MT1 (`Adr048T5BackfillSqlIntegrationTests`): sí, de verdad.** Corre las 4 constantes de
  `Adr048T5BackfillSql` vía `ExecuteSqlRawAsync` contra Postgres real y compara contra el oráculo en vivo
  (el persister para las 3 ramas que pueden pasar por él; `ReservaCollectionStatus.Derive` directo para el
  fallback puro, que el persister no puede oraculizar porque crearía filas hijas). Son las **mismas**
  constantes que usa la migración (clase compartida — verificado que la migración las referencia), así que
  no hay copia que se desincronice. Cubre las 4 ramas + el caso N1 (tipo 999 con CAE). **Cierra mi MT1.**
- **MT2 (`Adr048T5B1FacturaDeVentaRegressionIntegrationTests`): NO ejercita el `ProcessInvoiceJob` real.**
  Siembra la factura como `"A"` y llama `RefreshInvoicingAxisOnlyAsync` directo (`:131`). Prueba el
  **efecto** (listado==detalle==`FullyInvoiced`), no el wiring emisión→refresco. Su propio XML-doc lo
  admite: no hay infraestructura para fakear el SOAP de WSFE/WSAA en este repo (verificado; el precedente
  citado `AfipServiceCascadeReceiptVoidTests` usa el mismo artificio de invocación directa). El wiring
  (`ProcessInvoiceJob`/`HandleStale`/`InvoiceService` **llaman** al refresco) queda verificado por
  **lectura de código** (yo lo confirmé en `:1615`, `:2829`, `:2611`), no por un test ejecutado. Además
  `AfipServiceRefreshInvoicingAxisTests` cubre el wrapper `RefreshInvoicingAxisForNonCreditNoteAsync` con
  un `AfipService` real (factura, ND, y no-op sin reserva). **Aceptable** dada la ausencia de infra SOAP;
  lo dejo como limitación declarada, no bloqueante.

### (d) ¿La alineación Unknown=0 cambia algún caso real? ✔ no

El cambio (`ReservaService.cs:2481-2497`, backfill `Adr048T5BackfillSql.cs:95-103`, proyector vía
`SignedNetAmount`/`IsInvoiceOrDebitNote`) solo difiere para un comprobante con `Resultado="A"` y
`TipoComprobante` **fuera de los 12 tipos AFIP conocidos** — dato corrupto que no puede existir en un
comprobante con CAE real (ARCA no emite CAE para tipos inválidos). En datos reales **no cambia ningún
caso**. Lo que sí logra: proyector, query del listado y backfill SQL ahora usan el **mismo** criterio ⇒
**elimina la divergencia N1** que había marcado. Correcto.

### Estado de mis hallazgos previos

- **B1 — RESUELTO** (con el matiz de atomicidad del punto b, no-bloqueante).
- **N1 (Unknown) — RESUELTO** (alineado en las 3 superficies + test que lo blinda).
- **MT1 — RESUELTO** (test SQL-vs-vivo real, 4 ramas).
- **MT2 — parcialmente cubierto** (efecto sí, wiring por inspección; sin infra SOAP).
- **Nuevos no-bloqueantes:** N-RR1 (atomicidad del refresco, punto b). N-RR2: `InvoiceService.cs:2611`
  (recovery NC parcial) refresca **solo** facturación; el circuito de **cobro** no se re-aplica en ese
  camino de recovery — **limitación pre-existente**, documentada, fuera del alcance de T5 (el eje de
  facturación sí queda correcto; el cobro podría quedar viejo en esa rama rara de idempotencia).

**No verificado en el re-review:** no corrí los 3862 tests (el coordinador reporta verde); no ejercité el
`ProcessInvoiceJob` real (sin infra SOAP); no medí volumen PROD.
