# Review backend — Tanda 1 (modelo de estados derivados, ADR-048)

> Revisor: backend-dotnet-reviewer. Fecha: 2026-07-17. Alcance: diff sin commitear +
> archivos nuevos sin trackear del checkout actual. NO se corrió la app real ni el CI:
> esto es review estático contra el código. Todo lo que no pude verificar se dice con
> esas palabras.

> **VER "Re-review 2" AL FINAL (2026-07-17, 3ra pasada) — VEREDICTO VIGENTE: APROBADO.**
> La 2da pasada rechazó el 1er intento de fix (no-op). La 3ra pasada (opt-in
> `allowCorrectionWithinPar`) SÍ corrige el bloqueante y deja byte-idéntico al resto de los
> callers. Lo de abajo (1ra y 2da pasada) se conserva como contexto histórico.

## Veredicto

**CHANGES REQUIRED (RECHAZADO con 1 bloqueante).**

El diseño está bien trasladado a código en casi todo (derivación pura sin `SaveChanges`,
enganche atómico en el persister, par terminal a nivel reserva, migración idempotente con
nombres reales, tests de dominio sólidos). Pero hay **un bloqueante de correctitud** que
reintroduce, en el camino real de cancelación por servicio, exactamente la mentira B1 que
esta tanda vino a matar: en el escenario "operador debe reembolso", la reserva termina en
**Anulada** en vez de **Esperando reembolso del operador**.

---

## Hechos verificados

- `ReservaDerivedState.HadServicesAndAllCancelled` (Domain) es pura, distingue "tuvo
  servicios y todos anulados" de "nunca tuvo" (`ReservaDerivedState.cs:46`), usa los
  predicados `ServiceResolutionRules.IsCancelled` reales. OK.
- `ReservaTerminalDerivation` (Domain) es pura, nivel-reserva N-BC, criterio
  `RefundCap>0 && RefundStatus!=Settled` idéntico al cierre existente
  (`ReservaTerminalDerivation.cs:40-42`). `Settled==2` verificado
  (`BookingCancellationLineRefundStatus.cs:36`). OK.
- `ReservaTerminalTransitionApplier.ApplyIfNeededAsync` no hace `SaveChanges`, corre por
  el punto único `ReservaStatusTransitioner.ApplyAsync`, es idempotente (no-op si ya está
  en el terminal), y `ReservaStatusTransitioner` SOLO escribe log + `Status` + cleanup —
  **no toca comprobantes ni plata** (`ReservaStatusTransitioner.cs:65-93`). **REGLA 3
  (INV-048-02) cumplida**: la transición automática no emite NC/ND ni mueve plata por
  ningún camino directo.
- El enganche atómico está donde el diseño lo pidió: `ReservaMoneyPersister.cs:82`, ANTES
  del `SaveChangesAsync` de `:86`. Plata + estado comparten commit. OK (con la salvedad
  del bloqueante de abajo, que es de ORDEN dentro del caller, no del enganche en sí).
- La query de líneas en el applier solo corre cuando la reserva realmente quedó
  "toda anulada" (short-circuit en `ReservaTerminalTransitionApplier.cs:56,59` antes del
  query `:64`): **no hay regresión de performance** en el camino normal de cobro/mutación.
- `ReservaService.cs:5008` pasa `skipTerminalDerivation: true` (M1). Verificado que no hay
  doble corrida: si el persister ya movió a terminal, `EvaluateAndApplyAsync` recarga la
  reserva (`ReservaAutoStateService.cs:85`), `isEngineState` da false para
  `{Cancelled, PendingOperatorRefund}` (`:103-105`) y saltea todo el bloque. OK.
- Callbacks B1: `CloseReservaIfOperatorRefundComplete` y `OnAllCreditConsumedAsync` ahora
  deciden el cierre a NIVEL RESERVA (`BookingCancellationService.cs:3846-3859`,
  `:4713-4733`), reusando `IsReservaOperatorRefundPendingAsync` que combina líneas de BD
  de otras BC + líneas en memoria de la BC actual (patrón HC1). Lógicamente equivalente al
  criterio viejo pero extendido a todas las BC. OK.
- Migración `20260717182416`: no cambia schema (Designer sin cambios de modelo), idempotente
  (`CREATE TABLE IF NOT EXISTS ... AS` + guardas `tf."Status" = c.from_status`), detector de
  cancelado por tabla idéntico al precedente `RepairLegacyAnnulledReservaServices` y al
  mapeo C# (`WorkflowStatusHelper`: vuelos `('UN','UC','HX','NO')`, resto `LIKE 'cancel%'`).
  Nombres REALES correctos: `TravelFiles`/`TravelFileId`, genérico en `Reservations`,
  `BookingCancellations."ReservaId"`. INSERT a `ReservaStatusChangeLogs` cubre todas las
  columnas NOT NULL (`ReservaStatusChangeLog.cs`: FromStatus/ToStatus/Direction required, el
  resto nullable). `Down` no-op forense. No toca el bootstrapper (bug 42701 evitado: 0
  `ADD COLUMN`). OK.
- Tests: los 3 ajustes cargan datos reales, no relajan asserts. `Adr025` usa el param
  pre-existente `addSecondOperatorService: true` para conservar un servicio vivo y que la
  reserva no se auto-anule (`Adr025PartialAndMultiOperatorCancellationTests.cs:208,229`) —
  correcto. `Adr020Lifecycle`/`Adr020StatusUnlock` actualizan el assert al terminal nuevo y
  agregan verificación del log de auditoría. Los 2 test files nuevos de dominio son tablas
  de verdad correctas (incluye el caso N-BC y el negativo "reserva vacía no se anula").
  E2E-2 y E2E-3 prueban lo que dicen (cierre a nivel reserva / vía atómica en el persister).

---

## Blocking issues

### B-1 (BLOQUEANTE) — En `CancelServiceAsync`, el terminal se deriva ANTES de crear la línea de cancelación con su `RefundCap`: la reserva mis-deriva a **Anulada** cuando debería quedar **Esperando reembolso del operador**

**Qué pasa.** En `BookingCancellationService.RunCancellationUnitAsync` el orden es:

1. Paso 2/2-bis: marca el servicio cancelado + `SaveChanges` (`:477`).
2. Paso 3: `ReservaMoneyPersister.PersistAsync` (`:482`) → dentro corre
   `ReservaTerminalTransitionApplier.ApplyIfNeededAsync`, que decide el terminal leyendo
   `BookingCancellationLines` con **`AsNoTracking()` (round-trip a BD)**
   (`ReservaTerminalTransitionApplier.cs:64-69`).
3. Paso 5: `ApplyServiceCancellationCreditLineAsync` (`:502`) → recién **acá** se crea el
   `BookingCancellation` + la línea con su `RefundCap` (`GetOrCreateServiceCancellationBcAndLineAsync`
   → `BuildCancellationLinesAsync` → `AssignRefundCapsAsync`, `:1120-1126`, `:12271`,
   `:12433-12441`).

Todo corre en UNA transacción (atómico), pero el paso 3 lee un estado que **todavía no
incluye la línea del paso 5**. En el momento de derivar el terminal, la BC de la
cancelación en curso no existe aún → `IsOperatorRefundPending` no la ve.

**Cuándo mis-deriva (concreto):** reserva cuyo ÚLTIMO (o único) servicio vivo se cancela y
sobre ESE servicio el operador debe reembolso (la agencia le había pagado → `RefundCap>0`),
y ninguna BC previa de la reserva ya arrastraba una línea pendiente. Resultado:

- Paso 3 no ve líneas pendientes → `DetermineTerminalStatus` = **`Cancelled`**.
- Paso 5 crea la línea `RefundCap>0 / PendingOperatorRefund`.
- **La reserva queda `Cancelled` mientras el operador todavía debe plata.**

`CancelServiceAsync` NO vuelve a correr el motor ni el persister después del paso 5
(verificado: `:591-606` solo loguea/cuenta/retorna). Y `CloseReservaIfOperatorRefundComplete`,
cuando el operador reembolse, tiene guarda `bc.Reserva.Status != PendingOperatorRefund`
(`:3821`) → como la reserva ya está `Cancelled`, retorna sin hacer nada. **El estado nunca
se auto-corrige.** La reserva muestra "Anulada" (todo saldado) cuando en realidad el
operador aún debe un reembolso: es el descuadre silencioso R-T1-1 que el propio diseño
declaró, reintroducido en el camino de cancelación por servicio — el mismo camino de donde
salió F-2026-1046.

**Por qué los tests no lo atrapan.** E2E-2 siembra las BC directamente en BD (líneas ya
existentes) y ejercita solo el CIERRE, no la derivación-al-cancelar. E2E-3 cancela "a mano"
un servicio sin operador (sin `RefundCap`), donde `Cancelled` es lo correcto. Ningún test
ejercita "cancelar por servicio el último servicio con reembolso de operador pendiente →
esperar `PendingOperatorRefund`". Ese es justo el hueco.

**Fix sugerido (elegir uno):**
- Mover la derivación del terminal para que corra DESPUÉS de crear la línea de cancelación
  (paso 5), no en el paso 3 — p. ej. una segunda llamada explícita a
  `ReservaTerminalTransitionApplier.ApplyIfNeededAsync` (o un `RecalculateMoney`) al final de
  `RunCancellationUnitAsync`, dentro de la misma transacción. Como el applier es idempotente,
  correrlo de nuevo no rompe nada y ahora sí ve la línea recién creada. (El paso 3 puede
  quedar: en la mayoría de los caminos de plata sin creación de línea posterior sigue siendo
  el enganche correcto; lo que falta es re-derivar cuando SÍ hubo creación de línea después.)
- O hacer que la derivación lea las líneas incluyendo lo pendiente en el `ChangeTracker`
  (como hace `CloseReservaIfOperatorRefundComplete`, `:3831-3835`) Y garantizar que la línea
  ya esté al menos `Added` antes de derivar — hoy ni siquiera está `Added` en el paso 3, así
  que esto por sí solo no alcanza sin reordenar.
- **Agregar** una caminata E2E que cubra exactamente: `CancelServiceAsync` del último
  servicio con `RefundCap>0` → assert `Status == PendingOperatorRefund` (no `Cancelled`).

---

## Non-blocking improvements / riesgos

### N-1 (riesgo de negocio a confirmar con Gaston) — Reemplazo del ÚLTIMO servicio queda atrapado

El propio implementador declaró el riesgo. Lo verifiqué contra el código y **es real**:

- Cancelar el último servicio vivo auto-anula la reserva (a `Cancelled` o
  `PendingOperatorRefund`).
- A partir de ahí, el candado de solo-lectura ADR-035 bloquea agregar/editar servicios en
  estados terminales (`BookingService.cs:160`,
  `ReservaCapacityRules.EnsureServicesEditableByStateAsync`) → 409.
- Salir del terminal: `PendingOperatorRefund` **no tiene revert** en la matriz
  (`ReservaStatusTransitions.cs:83-103`, ausente); `Cancelled` solo revierte a
  `InManagement` **si la cancelación no dejó huella fiscal ni de plata** (gate duro en
  `RevertStatusAsync`, ADR-033). Si hubo NC o `RefundCap`, no se puede reabrir.

O sea: el flujo "anulo el viejo para cargar el nuevo" **sobre el último servicio** deja la
reserva trabada. El flujo seguro es "agrego el nuevo y después anulo el viejo" (siempre
queda un vivo). No encontré en el código un flujo documentado de reemplazo que fuerce el
orden inseguro, así que **no lo marco bloqueante**, pero es un cambio de comportamiento que
conviene que Gaston confirme (o mitigar: no auto-anular si la cancelación viene inmediatamente
seguida de un alta, o dejar claro en UX que el reemplazo del último servicio es add-first).

### N-2 (menor) — `HadServicesAndAllCancelled` depende de que el caller incluya las 6 colecciones

Ya está documentado en el XML-doc del applier (`ReservaTerminalTransitionApplier.cs:43-52`):
si a un caller nuevo le falta un `Include`, un servicio vivo de ese tipo pasa desapercibido y
podría disparar una anulación incorrecta. Los 2 callers actuales traen las 6. No es bloqueante
hoy, pero es una trampa latente para el próximo caller. Un guard defensivo (o cargar dentro del
método puro) lo cerraría de raíz; el diseño ya lo anotó como "detalle de implementación a
cuidar" (§2.3).

### N-3 (menor) — Migración: barrido de `Traveling` mueve estado por SQL fuera de la matriz

Es decisión M5 aprobada (saneo de dato legacy, auditado). Solo dejo constancia de que
`Traveling → {Cancelled, PendingOperatorRefund}` no existe en la matriz forward y se aplica por
UPDATE crudo; está justificado y acotado a la reparación única. Recomiendo el conteo pre/post
contra PROD que el propio diseño pide (§3, lección 2026-07-09) antes de dar por buena la corrida.

---

## Security / data risks

- Sin exposición de datos nuevos en respuestas API en esta tanda (T1 es backend/estado; la
  presentación es T4). Los `Reason` del log y de la migración están en criollo, sin IDs/enums
  técnicos ni PII. OK.
- La migración deja tablas `_repair_*` con `from_status` como red forense; no contienen PII
  sensible (solo IDs de reserva y estados). Aceptable, consistente con el precedente.
- Auditoría (regla 10 / INV-048-04): toda transición automática y la reparación quedan en
  `ReservaStatusChangeLogs` con actor `system:auto-state`. Cubierto.

## Missing tests

1. **(bloqueante, ligado a B-1)** E2E: `CancelServiceAsync` del último servicio con
   `RefundCap>0` → `Status == PendingOperatorRefund`. Hoy no existe y es el caso que falla.
2. E2E-1 (caso base) y el negativo "reserva nueva sin servicios no se anula" del diseño §5 no
   están en el file de integración; el negativo sí está cubierto a nivel unit
   (`ReservaDerivedStateTests`), pero E2E-1 (cancelar 2 servicios sin refund → `Cancelled` +
   assert "no se emitió NC nueva") no lo veo en ningún lado. Recomendado agregarlo.
3. Cobertura de `OnAllCreditConsumedAsync` a nivel reserva con OTRA BC pendiente (la rama nueva
   `:4720-4733`): E2E-2 cubre `CloseReservaIfOperatorRefundComplete`, pero no vi una que pase por
   el path de crédito-del-cliente-consumido con refund de operador pendiente en otra BC.

## Domain concerns

- El par terminal y su cierre a nivel reserva están correctos conceptualmente. El único
  problema es de ORDEN de ejecución (B-1), no de reglas de dominio.
- Coherencia con OQ-1 (Gaston): la intención es que mientras el operador deba, la reserva
  diga "Esperando reembolso"; B-1 rompe justo eso en el camino más común.

## Commands that should be run (los corre el reviewer/QA, no yo)

- `dotnet test` unit (InMemory) — debería seguir verde; las suites tocadas no ejercitan el
  path de B-1.
- `dotnet test` integración Postgres (CI) — E2E-2/E2E-3 nuevos. Ojo: E2E-2 depende del índice
  único "una cancelación activa por reserva" y de `PostgresIntegrationFixture`; no corre local
  (DB en VPS). Verificar en CI.
- Tras el fix de B-1: agregar y correr la E2E de "último servicio con refund de operador".

## No verificado

- No corrí la app real ni el CI. Verdicto sobre código estático.
- No verifiqué el interno completo de `AssignRefundCapsAsync` respecto de si SIEMPRE setea
  `RefundStatus=PendingOperatorRefund` cuando `cap>0` en el path parcial — leí `:12441`
  (`cap>0 ? PendingOperatorRefund : None`) y el comentario `:1120`, que lo confirman, pero no
  ejercité el cálculo del cap en sí.
- No verifiqué el flujo de anulación TOTAL (`:822`/`:2558`) en profundidad; el diseño afirma
  que setea `PendingOperatorRefund` explícito y no depende de la derivación del motor. Asumido,
  no re-verificado end-to-end.
- No verifiqué que `gen_random_uuid()` esté disponible en la instancia PROD (Postgres 13+ lo
  trae built-in; el precedente lo usa, así que asumo que sí).

---

# Re-review (2026-07-17, 2da pasada) — sobre el fix de B-1 y los demás gates

**Veredicto: RECHAZADO.** El "paso 6" agregado NO corrige B-1: es un no-op en el escenario
exacto del bloqueante. El resto de los puntos (atomicidad del paso 6, idempotencia de la
doble corrida, otros call-sites, E2E-1, Reason de la migración) están OK. Pero el corazón
del fix no funciona, así que el diff no puede aprobarse.

## BLOQUEANTE B-1 SIGUE ABIERTO — el "paso 6" re-invoca el persister, pero el applier lo ignora porque la reserva ya está en un estado terminal

**El fix aplicado** (`BookingCancellationService.cs:549`, dentro de `RunCancellationUnitAsync`):
una segunda llamada a `ReservaMoneyPersister.PersistAsync` al final, después de que el paso 5
crea la `BookingCancellationLine` con `RefundCap`.

**Por qué NO funciona.** El applier (`ReservaTerminalTransitionApplier.ApplyIfNeededAsync`)
sigue teniendo, como PRIMERA guarda, `IsLiveEngineStatus(reserva.Status)` (`:56-57`), y ese
helper solo devuelve `true` para `{InManagement, Confirmed}`
(`ReservaTerminalDerivation.cs:31-33`, sin cambios; el unit test lo fija así). Traza del
escenario del bloqueante (último/único servicio con `RefundCap>0`):

1. **Paso 3** (`:483`): reserva en `Confirmed`, servicio ya `Cancelado`, la BC/línea todavía
   NO existe → `HadServicesAndAllCancelled`=true, query de líneas vacía →
   `DetermineTerminalStatus` = **`Cancelled`** → el applier **transiciona `Confirmed → Cancelled`**
   y hace `SaveChanges`.
2. **Paso 5** (`:502`): recién ahora se crea la línea con `RefundCap=500 / PendingOperatorRefund`.
3. **Paso 6** (`:549`): re-invoca el persister. Pero ahora `reserva.Status == "Cancelled"` →
   `IsLiveEngineStatus("Cancelled")` = **false** → el applier **retorna en la línea 57 sin
   hacer nada**. Nunca llega a la re-derivación (`:69`) ni a la transición.

Resultado final: la reserva queda **`Cancelled`** con el operador debiendo $500 — exactamente
la mentira B-1/R-T1-1 que el fix decía cerrar. Y no se auto-corrige nunca:
`CloseReservaIfOperatorRefundComplete` tiene guarda `Status != PendingOperatorRefund` (`:3842`),
que con la reserva en `Cancelled` retorna sin actuar.

**Por qué el diseño del par lo hace evidente:** el camino de anulación TOTAL sí anda porque
setea `PendingOperatorRefund` **a mano ANTES** de recalcular
(`ConfirmAsync`/`PersistConfirmationCoreAsync`, `:2579-2587`: transición explícita → luego
`CancelAllReservaServicesAsync` → luego los persisters), así el applier ve un estado terminal
y hace no-op. El camino POR SERVICIO no setea nada a mano: delega en el applier, que deriva
**prematuro** en el paso 3 y después se auto-bloquea. El "paso 6" no puede deshacer eso porque
el applier, por diseño, **jamás toca un estado terminal**.

**El test nuevo lo confirma (y debería estar ROJO).**
`CancelarUltimoServicioConReembolsoDeOperadorPendiente_QuedaEsperandoReembolso_NoAnuladaDirecto`
(`Adr048...E2ETests.cs:277-342`) monta el escenario correcto por el camino real
(`CancelServiceAsync` + `SeedSupplierPaymentAsync` → `RefundCap`) y asserta
`Status == PendingOperatorRefund` (`:323`). Según la traza de arriba, el código produce
`Cancelled` → **el assert falla**. Es un buen test (prueba lo que dice), pero prueba que el
bug SIGUE. Como los tests de integración Postgres NO corren local (DB en VPS), es casi seguro
que este test **no se ejecutó**: hay que correrlo en CI y verlo verde ANTES de aprobar — hoy,
por análisis estático, va a dar rojo.

**Fixes posibles (elegir uno; requieren tocar código, no lo hago yo):**
- **(A, recomendado) Permitir que el applier corrija ENTRE los dos estados del par cuando la
  reserva ya es terminal y sigue "toda anulada".** Ampliar la guarda para que la re-derivación
  también acepte `{Cancelled, PendingOperatorRefund}` como estados de entrada (NO Traveling /
  Closed / Lost / Budget). La guarda de idempotencia existente (`:72`) evita churn: si ya está
  en el terminal correcto, no hace nada. Así el paso 6 hace `Cancelled → PendingOperatorRefund`
  de verdad. Ojo: separar "estados que el motor EN VIVO puede auto-anular desde vivo"
  (`IsLiveEngineStatus`, hoy usado también por la migración/tests) de "estados desde los que se
  puede re-derivar el par" para no cambiar la semántica del barrido `Traveling` de la migración.
- **(B) No derivar el terminal en el paso 3 del camino por-servicio.** Pasar un flag a
  `PersistAsync` (o al applier) para saltear la derivación en la 1ra llamada y correrla SOLO en
  el paso 6, cuando la línea ya existe. Evita el `Cancelled` prematuro de raíz. Costo: plomería
  de un parámetro por el persister.
- **(C) Crear la línea de cancelación ANTES del recálculo de plata** (reordenar 5 antes de 3),
  para que la única derivación en el paso 3 ya vea la línea. Más riesgoso: mueve un flujo fiscal/
  de crédito delicado; no lo recomiendo sin más análisis.

Después del fix real: correr el test nuevo en CI y confirmar verde. Sin eso, no hay evidencia
de que ande (lección "verificado de verdad vs no verificado").

## Lo que SÍ quedó bien (verificado en esta pasada)

- **1(a) Atomicidad del paso 6.** Correcto: el paso 6 corre DENTRO de `RunCancellationUnitAsync`,
  que a su vez corre dentro de la transacción `tx` (`:547` begin, `:564` commit). No es un commit
  separado; respeta la regla 9. OK.
- **1(b) Doble corrida del persister idempotente en la plata.** Correcto: `ReservaMoneyCalculator`
  recalcula desde el estado actual (puro), `SyncMoneyByCurrencyRowsAsync` es upsert+delete, y
  `CommissionAccrualPersister.RecalculateAsync` recalcula (no acumula). Correr dos veces da el
  mismo saldo. El problema del paso 6 no es la plata (esa queda bien): es que la parte de ESTADO
  no se re-deriva. OK en plata.
- **1(c) Otros call-sites del persister.** Verificado para `ConfirmAsync`: setea
  `PendingOperatorRefund` explícito (`:2579`) ANTES de `RecalculateMoneyAfterTotalCancellationAsync`
  (`:2587`+), así el applier ve un estado terminal y hace no-op → no alcanzable por B-1. Correcto.
  `FinalizeForceCloseAsync` (`:3673`): no lo tracé línea por línea, pero sigue el mismo patrón de
  anulación total; asumido, no re-verificado end-to-end.
- **3. E2E-1 (no emite NC/ND).** El assert es REAL: cuenta `Invoices` de la reserva justo antes
  y justo después de la transición del último servicio y exige igualdad (`:245`, `:260-261`).
  Atraparía una emisión accidental de NC/ND en la transición. No es trivialmente verde. Nota: E2E-1
  usa `RefundCap=0` (sin `SupplierPayment`), donde `Cancelled` ES lo correcto, así que este test
  pasa aunque B-1 siga roto — NO cubre el fix de B-1 (para eso está el test de arriba, que sí lo
  cubre y hoy daría rojo).
- **4. Reason de la migración sin "(ADR-048)".** Corregido: los `Reason` insertados en
  `ReservaStatusChangeLogs` ahora dicen "Reparacion de datos historicos: ..." sin el código
  interno (`20260717182416_...cs:136-137`). El "ADR-048" queda solo en el comentario XML del
  archivo (`:8`), que no es visible para el usuario. OK.

## No verificado (2da pasada)

- No corrí el test nuevo (integración Postgres, no corre local). Mi afirmación de que da rojo es
  por análisis estático de la traza; **hay que confirmarlo en CI**.
- `FinalizeForceCloseAsync` no re-verificado end-to-end (asumido igual que `ConfirmAsync`).

---

# Re-review 2 (2026-07-17, 3ra pasada) — fix opt-in `allowCorrectionWithinPar`

**Veredicto: APROBADO** (con 1 mejora no bloqueante + un gate de verificación en CI).

El 3er intento (Opción A ACOTADA a opt-in) **sí corrige B-1** sin romper los flujos que
deliberadamente sostienen un estado del par. Verifiqué los 5 puntos línea por línea contra el
código nuevo, sin confiar en el reporte.

## 1. La traza del bloqueante contra el código nuevo → AHORA CORRIGE

Escenario: reserva `Confirmed`, único hotel, la agencia le pagó al operador (`RefundCap=500`).
`CancelServiceAsync` → `RunCancellationUnitAsync`:

- **Paso 3** (`BookingCancellationService.cs:486`): `PersistAsync(_db, reserva.Id, ct)` — flag
  **default false**. Adentro, `ApplyIfNeededAsync` con `allowCorrectionWithinPar=false`:
  `canReDerive = IsLiveEngineStatus("Confirmed")=true` (`ReservaTerminalTransitionApplier.cs:97-100`).
  La línea aún NO existe → query vacía → `DetermineTerminalStatus` = `Cancelled`. `isCorrectionWithinPar`
  = `IsInTerminalPar("Confirmed")` = false → `direction="Forward"` → transiciona **`Confirmed → Cancelled`**.
- **Paso 5** (`:506`): crea la BC + línea con `RefundCap=500 / RefundStatus=PendingOperatorRefund`.
  `SaveChanges` (`:531`) — la línea queda persistida dentro de la tx.
- **Paso 6** (`:555-556`): `PersistAsync(..., allowTerminalCorrectionWithinPar: true)`. El persister
  la propaga a `ApplyIfNeededAsync(..., allowCorrectionWithinPar: true)` (`ReservaMoneyPersister.cs:94-95`).
  Ahora `reserva.Status="Cancelled"` → `canReDerive = false || (true && IsInTerminalPar("Cancelled")=true) = true`
  → NO retorna. `HadServicesAndAllCancelled`=true. Query de líneas: **ahora SÍ ve la línea** creada en el
  paso 5 (`AsNoTracking`, round-trip dentro de la misma tx) → `IsOperatorRefundPending`=true →
  `DetermineTerminalStatus` = `PendingOperatorRefund`. `Status("Cancelled") != target` → no es no-op.
  `isCorrectionWithinPar=true` → `direction="Correction"`, motivo en criollo (`:131-133`) → transiciona
  **`Cancelled → PendingOperatorRefund`**.

**Resultado final: `PendingOperatorRefund`.** El bloqueante queda cerrado; el test nuevo
(`CancelarUltimoServicioConReembolsoDeOperadorPendiente...:323`) ahora **debería pasar** (y el cierre
posterior a `Cancelled` tras el reembolso, vía `CloseReservaIfOperatorRefundComplete`, también). No lo
corrí: es integración Postgres (no corre local) → **confirmar verde en CI antes del deploy**.

## 2. Byte-idéntico para los demás callers → VERIFICADO estructuralmente

Grepé TODOS los call-sites: el ÚNICO que pasa el flag en `true` es `BookingCancellationService.cs:556`
(paso 6). Todos los demás usan el default `false`: `PaymentService:1394`, `AfipService:2268`,
`ReservaService:1374/5130`, `ClientCreditService:*`, `DebitNoteAnnulmentReconciliation:255`,
`CoherenceChecks:276`, `CoherenceMoneyRecalculator:159`, `OverpaymentCreditConverter:137`,
`MultiCurrencyBackfillService:96`, `BookingCancellationService:486/851` (este último = el persister de la
anulación total `RecalculateMoneyAfterTotalCancellationAsync`). Y `ReservaAutoStateService.cs:116` llama
`ApplyIfNeededAsync` SIN el flag. Como el único comportamiento nuevo del 3er pase está detrás del flag
(`:98`), y default=false reproduce EXACTAMENTE la rama vieja `IsLiveEngineStatus`-only, el resto de los
callers es byte-idéntico a la versión donde los 3 tests reales pasaban. En particular:
- `ConfirmAsync`: setea `PendingOperatorRefund` a mano ANTES del recalc (`:2579-2587`) → el persister
  (flag false) ve un estado del par → `canReDerive=false` → no-op. Su decisión post-CAE
  (`ShouldAutoCloseWithoutOperatorRefundAsync`) queda intacta.
- `AnnulWithPaymentsToCreditAsync` / `ForceArcaConfirmationAsync`: mismo patrón, flag false → el applier no
  les pisa el estado del par que sostienen a propósito.

## 3. La corrección lateral no puede salirse del par ni tocar Traveling/Closed/Lost, ni emite NC/ND

- `DetermineTerminalStatus` SOLO devuelve `Cancelled` o `PendingOperatorRefund`
  (`ReservaTerminalDerivation.cs:82-85`) → el destino siempre es dentro del par; nunca vuelve a un estado
  vivo.
- Gate `canReDerive` (`ReservaTerminalTransitionApplier.cs:97-98`): para `Traveling/Closed/Lost/Budget/
  Quotation`, `IsLiveEngineStatus=false` Y `IsInTerminalPar=false` → `false` **incluso con el flag en true**
  → el applier no los toca. Verificado con la tabla de verdad del unit test.
- La transición pasa por `ReservaStatusTransitioner.ApplyAsync` = log + `Status` + cleanup, **sin
  comprobantes ni plata** (INV-048-02, regla 3). Además `HadServicesAndAllCancelled` sigue de guarda: solo
  corrige reservas efectivamente "todas anuladas".

## 4. Carrera/pisada applier-con-flag vs callbacks de cierre → SIN conflicto

Los callbacks (`CloseReservaIfOperatorRefundComplete`, `OnAllCreditConsumedAsync`) corren dentro de
`OperatorRefundService.AllocateAsync` / `ClientCreditService`, que **NO pasan por `ReservaMoneyPersister`**
(verificado en la 1ra pasada: `AllocateAsync` muta `line.RefundStatus` en memoria, no llama al persister).
El applier-con-flag corre en el paso 6 de `CancelServiceAsync` — **otra operación de usuario, otra
transacción** (cancelar un servicio ≠ registrar un reembolso). No hay ejecución concurrente en la misma tx.
Entre operaciones distintas, ambos caminos usan el MISMO criterio de dominio
(`IsOperatorRefundPending`) y el MISMO punto único de transición, que es idempotente (`:116`: si ya está en
el target, no-op y no re-loguea) → el que corra segundo encuentra el Status ya correcto. Sin doble
escritura ni doble log entre caminos. (La concurrencia multi-usuario con xmin es la misma propiedad
last-write-wins pre-existente del motor; no es regresión de este fix.)

## 5. ¿Opt-in correcto, o par corregible por default con exclusiones? → OPT-IN ES LO CORRECTO (no solo preferencia)

Me convence, y no es solo gusto: es una decisión de **fail-closed vs fail-open** en un camino de
plata/cancelación.
- `DetermineTerminalStatus` decide el par mirando SOLO `BookingCancellationLine.RefundCap/RefundStatus`.
  Varios flujos sostienen a propósito un estado del par con reglas MÁS RICAS que ese criterio no conoce
  (ConfirmAsync: cierre post-CAE + NDs a medio emitir + sync de `BookingCancellation.Status`;
  AnnulWith: receivable a nivel PROVEEDOR). Corregible-por-default forzaría el criterio simple sobre esos
  flujos → los 3 tests rotos son la evidencia concreta.
- Default-corrigible con exclusiones = hay que enumerar y marcar "no me corrijas" en CADA flujo que
  sostiene el par; olvidarse de uno **falla ABIERTO** (pisa en silencio una decisión deliberada → posible
  corrupción del estado de plata). Opt-in **falla CERRADO**: un flujo nuevo que necesite la corrección y se
  olvide del flag simplemente conserva el bug viejo (visible, atrapable por test), no corrompe nada.
- Para un ERP de plata, fail-closed es el default correcto. **Sin objeción de fondo.**

## Mejora NO bloqueante — doble entrada de auditoría (Cancelled "fantasma")

En el escenario del fix quedan DOS filas en `ReservaStatusChangeLog` para una sola cancelación:
`Confirmed → Cancelled` (Forward, paso 3) y `Cancelled → PendingOperatorRefund` (Correction, paso 6),
ambas en la misma tx / instante. El `Cancelled` intermedio **nunca fue un estado visible** (se corrigió
dentro del mismo commit), pero el historial lo muestra. No es corrupción (el campo Status sí pasó por ahí)
y la fila Correction lo explica en criollo, pero es ruido de auditoría y podría confundir en la ficha de
historial. La forma de evitarlo sería la Opción B de mi 2da pasada (no derivar el terminal en el paso 3 del
camino por-servicio y derivar UNA sola vez en el paso 6 → log limpio `Confirmed → PendingOperatorRefund`).
La Opción A elegida es correcta y más simple de razonar; dejo esto como follow-up opcional, **no bloquea**.

## No verificado (3ra pasada)

- No corrí el test de integración nuevo (Postgres en VPS, no corre local). La traza dice que ahora pasa;
  **confirmar verde en CI** (lección "verificado de verdad vs no verificado").
- `FinalizeForceCloseAsync` sigue asumido igual que `ConfirmAsync` (patrón de anulación total, flag false),
  no tracé línea por línea.
- No re-verifiqué que la migración/otros gates de las pasadas anteriores no se hayan tocado en este 3er
  pase (el alcance del pedido fue el fix de B-1); asumo que no cambiaron.
