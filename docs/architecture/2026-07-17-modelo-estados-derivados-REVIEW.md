# REVIEW de arquitectura — Modelo de estados derivados (implementación)

> Revisor: software-architect-reviewer. Fecha: 2026-07-17.
> Documento revisado: `docs/architecture/2026-07-17-modelo-estados-derivados-DISENO-implementacion.md`.
> Contexto/reglas aprobadas: `docs/architecture/2026-07-17-modelo-estados-derivados-BORRADOR-para-aprobar.md`.
> Alcance: se cuestiona el **CÓMO** del diseño. Las 10 reglas y las 3 respuestas del dueño se toman como DECISIONES CERRADAS.
> Todo lo afirmado sobre el código se verificó con Read/Grep en esta sesión (archivo:línea abajo). Lo no verificado se marca.

---

## VEREDICTO: **APROBADO CON CAMBIOS**

La dirección es correcta y está bien fundada: reusar el motor existente como único escritor, materializar el terminal en `Status`, y enganchar en el chokepoint de plata es la decisión de menor riesgo. El diseño es honesto sobre sus huecos (R-TX, OQ-1). Pero hay **tres bloqueantes reales** que NO son "preguntas de negocio para Gaston" sino problemas de correctitud/acople que deben resolverse **en el diseño** antes de codear T1/T4. Ninguno tumba la estrategia; todos son acotables.

---

## Hechos verificados (lo que inspeccioné y confirmé)

1. **El motor hoy corre solo desde 2 lugares productivos.** VERIFICADO: `ReservaService.cs:5005` (`UpdateBalanceAsync`) y `ReservaLifecycleAutomationService.cs:195` (job nocturno). No hay otra invocación de `EvaluateAndApplyAsync` fuera de tests. La afirmación central del diseño (§2.2) es correcta.
2. **La cancelación por servicio llama al persister directo, NO al motor.** VERIFICADO: `BookingCancellationService.cs:482` (`ReservaMoneyPersister.PersistAsync`) sin `EvaluateAndApplyAsync` al lado. Ésta es efectivamente la causa raíz de la mentira #1 (header quedó `Confirmed`).
3. **El persister es el chokepoint universal de plata y hace su propio `SaveChanges`.** VERIFICADO: `ReservaMoneyPersister.cs:39-79`, `SaveChanges` en `:70`, y ya dispara un side-effect posterior (`CommissionAccrualPersister.RecalculateAsync`, `:78`). Es `internal static`, sin DI. El diseño lo describe bien.
4. **El transicionador NO hace `SaveChanges`; corre en la unidad de trabajo del caller.** VERIFICADO: `ReservaStatusTransitioner.cs:21, 52-93`. El motor sí hace su propio `SaveChanges` (`ReservaAutoStateService.cs:140`). La base para "plegar en una sola `SaveChanges`" existe y es viable.
5. **`Status=Cancelled` ya se alcanza desde `{InManagement, Confirmed}` por la matriz manual** (`ReservaStatusTransitions.cs:53-54`) y el flujo de anulación total lo escribe vía el transicionador. Reusar `Status=Cancelled` como terminal derivado NO inventa un acople nuevo (§1.2 correcto).
6. **La transición a terminal no toca comprobantes ni plata** (INV-048-02). VERIFICADO: `ReservaStatusTransitioner.ApplyAsync` solo escribe log + set `Status` + limpieza de marcas (`:73-92`). Cumplido por construcción.
7. **La limpieza al entrar a `Cancelled`/`PendingOperatorRefund` apaga la marca "confirmada con cambios" + borra el detalle** (`ReservaStateCleanupRules.cs:63-72`). O sea: la transición automática a terminal LIMPIA `HasUnacknowledgedChanges`. Consistente con el objetivo.
8. **El circuito de plata de anulada lee TODAS las BC de la reserva, tolera múltiples** (`ReservaService.cs:5257-5312`, `DeriveCancelledMoneyContextAsync`). No asume una única BC "total". El path servicio-por-servicio (que genera N BC por servicio) alimentará el circuito correctamente.
9. **El gate de reapertura de una anulada** (`ReservaService.cs:1698-1714`) bloquea reabrir si hubo NC/saldo a favor/refund. Aplica igual a una reserva auto-anulada por el motor: benigno y consistente.
10. **Existe precedente del patrón de reparación**: `20260705015708_RepairLegacyAnnulledReservaServices.cs` (SQL puro idempotente + backup `_repair_*` + recálculo de plata delegado a `CoherenceMoneyRecalculator`). El diseño elige Program.cs+marcador en vez de migración porque la derivación necesita lógica de dominio; razonamiento válido, pero ver B2/M2.

---

## BLOQUEANTES

### B1 — OQ-1 no es un toggle de negocio: `PendingOperatorRefund` como estado de cabecera está acoplado al ciclo de la BC total, y sus callbacks de cierre asumen UNA sola BC que maneja la cabecera

**Evidencia.** El cierre de la reserva `PendingOperatorRefund → Cancelled` lo disparan callbacks por-BC guardados SOLO por `Reserva.Status == PendingOperatorRefund`, que inspeccionan **las líneas de ESA BC** (no todas las de la reserva):
- `CloseReservaIfOperatorRefundComplete` (`BookingCancellationService.cs:3779` guard; `:3790-3811` cuenta solo `line.BookingCancellationId == bc.Id`; `:3830` cierra a `Cancelled`).
- `OnAllCreditConsumedAsync` (`:4656` guard `ClientCreditApplied`; `:4669` cierra a `Cancelled`).

Hoy esto es sano porque **la cabecera entra a `PendingOperatorRefund` solo en el flujo de anulación TOTAL** (`:2558`), donde hay UNA BC total que gobierna. Pero el escenario que el motor viene a cubrir es el **servicio por servicio**: cada `CancelServiceAsync` crea su **propia BC** (`:482` recalcula, `:493+` deja línea/ancla). Cuando el motor auto-anula la cabecera al caer el último servicio, hay **N BC por servicio**, ninguna "total".

**Por qué rompe.**
- Si el motor lleva la cabecera a **`PendingOperatorRefund`** (recomendación del diseño en OQ-1): la **primera** BC por-servicio cuyo refund del operador se salda dispara `CloseReservaIfOperatorRefundComplete`, ve **sus** líneas Settled, y cierra la cabecera a `Cancelled` **aunque otras BC por-servicio sigan con refund del operador pendiente**. Cierre prematuro + los refunds pendientes de las demás quedan invisibles a nivel cabecera.
- Si el motor va **directo a `Cancelled`**: los callbacks quedan no-op (guard `!= PendingOperatorRefund`), pero la cabecera muestra terminal mientras hay refunds del operador vivos, y se pierde la semántica/visibilidad de "esperando reembolso".

El diseño enmarca esto como una decisión binaria para Gaston (`Cancelled` vs `PendingOperatorRefund`, §1.2/OQ-1). **No lo es**: ninguna de las dos opciones es correcta tal cual, porque el estado de cabecera `PendingOperatorRefund` está cableado a un mecanismo de cierre que presupone una única BC conductora. Además, la afirmación "reusar el mismo criterio que ya usa `BookingCancellationService`" no es un extract limpio: ese criterio está **embebido** en el flujo de anulación total (`:2558`), no existe como helper de dominio compartido que el motor pueda invocar con N BC.

**Remediación (antes de T1).** Diseñar explícitamente la derivación del terminal con **N BC**:
- Definir "operador pendiente" a nivel reserva agregando **todas** las BC/líneas con `RefundCap > 0` y `RefundStatus != Settled` (no una).
- Si se elige `PendingOperatorRefund`: cambiar los callbacks de cierre para que evalúen **la completitud a nivel RESERVA** (todas las líneas con refund de todas las BC), no por-BC; o introducir un único punto de cierre que el motor consulte.
- Si se elige `Cancelled` directo: documentar que la visibilidad de "refund operador pendiente" pasa a vivir en el circuito de plata de anulada (`DeriveCancelledMoneyContextAsync`) y no en el `Status`, y verificar que ese circuito la expone.
- En ambos casos: extraer el criterio a un helper de dominio puro y testearlo con 2+ BC.

---

### B2 — La mitigación de R-TX ("plegar el estado en el mismo `SaveChanges` del persister") está sin especificar, y el fallback propuesto ("la próxima mutación corrige") contradice la razón de ser del ADR

**Evidencia.** El persister hace su `SaveChanges` en `ReservaMoneyPersister.cs:70`, **antes** de cualquier lugar donde podría invocar al motor (hoy el único side-effect posterior, `:78`, ya corre después del commit de plata y con su propia `SaveChanges`). El motor, tal como está, también hace su propia `SaveChanges` (`ReservaAutoStateService.cs:140`). El diseño (§2.4) deja la atomicidad como "**decisión de diseño a confirmar en review**" y ofrece como respaldo (a) idempotencia: "la próxima mutación corrige".

**Por qué rompe / por qué importa.** El ADR nace porque Gaston está furioso con estados que mienten y con "después lo arregla un proceso" (regla 9 corregida: SIN pasada nocturna, correcto ahí mismo). "La próxima mutación corrige" ES corrección diferida: si el proceso crashea entre el commit de plata y el commit de estado en un caller **sin transacción explícita**, el estado queda stale hasta que **un humano vuelva a tocar** la reserva — exactamente la mentira que el ADR viene a matar, y ahora sin vigía nocturno que la cure. No verifiqué cuántos call-sites del persister abren transacción explícita; si la mayoría no lo hace, el riesgo es real, no teórico.

**Remediación (antes de T1).** Comprometer el diseño con la vía atómica concreta, que ES viable dado el hecho verificado #4:
- Convertir la **derivación del terminal** en un método **puro estático** (estilo `ReservaStatusTransitioner`, que NO hace `SaveChanges`) e invocarlo **dentro de `PersistAsync`, ANTES de la línea 70**, de modo que el cambio de `Status` se flushee en la MISMA `SaveChanges` que la plata → atómico incluso sin transacción del caller.
- La campana/notificación (`NotifyNeedsReviewAsync`) queda fuera del persister (Opción C, correcto).
- Especificar que bajo esta vía **`UpdateBalanceAsync` NO debe llamar al motor dos veces** (ver M1).
- Solo si se descarta plegar en el persister, entonces sí OQ requiere transacción envolvente obligatoria en cada call-site (Opción A + test de arquitectura). No dejar la atomicidad "a confirmar": es el corazón de la regla 9.

---

### B3 — `IsVoided` derivado vs. `Status` materializado: `PendingOperatorRefund` rompe la equivalencia, y T4 elimina el candado que hoy lo cubre

**Evidencia.** El diseño define `IsVoided = (OperationalAxis == SinEfecto)` (§1.1), y materializa el terminal **solo** como `Status=Cancelled` (§1.2, "El eje operativo NO agrega columna: es `Status`"). T4 (`§5`) **elimina** el candado hardcodeado del front `ESTADOS_ANULADOS = {Cancelled, PendingOperatorRefund}` (`moneyStatus.js:32`) y `isReservaAnulada` (`:35-37`) para leer `isVoided`.

Hoy `moneyStatus.js` trata a **ambos** estados como anulada (`:32, :70-72`): una reserva en `PendingOperatorRefund` cae al circuito de plata de anulada (`getMoneyStatusAnulada`). El backend también: `IsCancelledLikeStatus` (usado por `DeriveCancelledMoneyContextAsync`, `ReservaService.cs:5254`) reconoce ambos.

**Por qué rompe.** El diseño no dice si `OperationalAxis==SinEfecto` (y por ende `isVoided`) es **true para `PendingOperatorRefund`**. Hay ambigüedad peligrosa según la fuente:
- Si `isVoided` se deriva del **estado de los servicios** ("tuvo servicios y todos anulados"): una `PendingOperatorRefund` del flujo total (que ya corrió `CancelAllReservaServicesAsync`, `:2564`) tiene todos los servicios cancelados → `isVoided=true`. OK. **Pero entonces `IsVoided` y `Status=Cancelled` NO son equivalentes**: hay reservas con `isVoided=true` y `Status=PendingOperatorRefund`. La afirmación de §1.2 "el terminal se materializa en `Status=Cancelled`" es **incompleta** (deja fuera el otro estado SinEfecto).
- Si `isVoided` se proyecta de `Status==Cancelled` (materializado, la lectura barata que promete T5): entonces al eliminar el candado del front, **toda reserva en `PendingOperatorRefund` pierde el circuito de anulada** → cae a `getMoneyStatusEstadoVivo` → muestra "Debe"/"Sin movimientos". **Nueva mentira introducida por el fix.**

`PendingOperatorRefund` no es un caso raro: es el estado intermedio normal de toda anulación con reembolso de operador pendiente (`:2558, 3644, 5980, 6108, 11825, 12945`).

**Remediación (antes de T4, y define la semántica que necesita T1).** Definir con precisión, en el doc, qué es `SinEfecto`/`IsVoided`:
- O bien `OperationalAxis==SinEfecto` (y `isVoided`) incluye explícitamente `{Cancelled, PendingOperatorRefund}` (más el derivado por servicios), y T4 lo respeta.
- O T4 mantiene `PendingOperatorRefund` en el conjunto anulado.
- Y reconciliar §1.2: el terminal derivado NO es un único `Status`; son dos estados que hoy comparten el circuito de plata anulada. La materialización "es `Status`" debe reconocer el par, no solo `Cancelled`.

---

## MEJORAS (fuertes, no bloqueantes)

- **M1 — Doble corrida del motor en `UpdateBalanceAsync`.** Si Opción C hace que `PersistAsync` invoque la derivación, el path `UpdateBalanceAsync` correría el motor dos veces: dentro del persister y otra vez en `ReservaService.cs:5005`. Es idempotente (no incorrecto) pero redundante y confuso. Especificar: bajo Opción C, `:5005` mantiene SOLO la campana/notificación (o se elimina si el persister ya la cubre), no la derivación.

- **M2 — El marcador de la reparación debe setearse DESPUÉS de completar, y hay que decidir concurrencia de deploy.** Si el marcador se persiste antes de terminar y el arranque se corta a la mitad, la reparación queda "hecha" y a medias → reservas que mienten para siempre (sin nocturno que las cure). Setear el marcador AL FINAL, y apoyarse en la idempotencia de la derivación para re-correr todo el set en el próximo arranque si se cortó. Además: la reparación corre en el startup (`Program.cs`, bloque `using scope` antes de `app.Run()`), pero **no verifiqué si el deploy del VPS es stop-then-start o rolling**. Si es rolling, la instancia vieja podría estar sirviendo mientras la nueva repara → una edición concurrente de Gaston sobre la misma reserva puede chocar con xmin (`DbUpdateConcurrencyException`) o last-write-wins. Confirmar modo de deploy; si hay solape, envolver cada reserva reparada en manejo de conflicto (saltear y re-derivar en la próxima mutación) en vez de abortar el arranque.

- **M3 — Reusar migración vs. Program.cs+marcador.** Existe precedente de reparación por **migración** (`RepairLegacyAnnulledReservaServices`, idempotente, con backup `_repair_*` y `Down` no-op forense). EF ya garantiza "corre una sola vez" vía `__EFMigrationsHistory` — es un marcador más robusto que una `AppSetting` casera. El diseño elige Program.cs porque necesita lógica de dominio (la derivación) que no se puede escribir en SQL; razonable. Pero considerar el patrón híbrido del precedente: la parte de datos que sí es SQL-able por migración, y la derivación de estado por un servicio invocado una vez con marcador — o directamente reusar el endpoint admin `CoherenceMoneyRecalculator` como disparador manual controlado, evitando lógica de arranque nueva. Como mínimo, dejar backup del `Status` previo (el `ReservaStatusChangeLog` ya lo cubre — verificado — así que esto está OK, solo consolidarlo en el doc).

- **M4 — Orden de deploy de tandas.** T4 (front lee `isVoided`) DEPENDE de que T1 exponga `isVoided`/`cancelledMoneyContext` correctamente en el DTO y con la semántica de B3 resuelta. T2 (deuda operador por servicio vivo) y T3 (facturación "y devuelta") son independientes entre sí y de T1. Declarar en el doc: T1 antes que T4 (dependencia dura); T2/T3 en cualquier orden; T5 opcional. Ver también "T5 puede quedar afuera" abajo.

- **M5 — El motor no cubre `Traveling` con todos los servicios anulados.** `isEngineState` solo abarca `{InManagement, Confirmed}` (`ReservaAutoStateService.cs:85-88`). Una reserva en `Traveling` con todos los servicios cancelados no se auto-anularía. Probablemente correcto ("En viaje es inmutable", ADR-036), pero la mentira "todo anulado pero cartel En viaje" podría existir igual. Confirmar con Gaston que es intencional y anotarlo en "Qué NO cambia".

---

## MEJORAS MENORES

- El comentario de clase del motor (`ReservaAutoStateService.cs:35`) todavía dice "La reconciliación nocturna del job cura cualquier reserva que haya esquivado el chokepoint" — con la regla 9 corregida (sin nocturno) y el enganche universal, ese comentario quedará obsoleto. Actualizarlo al implementar para no dejar documentación que contradiga la decisión del dueño.
- T2 depende de que `BuildReservaServiceRowsAsync` traiga el flag de cancelación por servicio; el diseño lo marca como no verificado (§ auto-revisión). Correcto que sea un TODO de implementación, pero es un pre-requisito de T2, no un detalle.

---

## Acoples ocultos / preocupaciones de frontera

1. **`PendingOperatorRefund` es un estado de cabecera gobernado por el ciclo de la BC total** (B1). El motor, al escribir cabecera desde el chokepoint de plata, se mete en un territorio que hoy solo tocaba el flujo de anulación total con una BC conductora. Éste es el acople más peligroso y el menos declarado en el doc.
2. **El persister invocando al motor invierte la dirección** (persistencia → derivación de estado). El diseño lo reconoce (Opción C, `internal static`). Mitigable con un derivador puro estático (como el transicionador), pero hay que asegurarse de que NO haya reentrada: la derivación via transicionador solo escribe `Status`+cleanup, no recalcula plata → no re-llama al persister. Verificado que no hay recursión hoy; mantener esa invariante.
3. **Identidad de EF**: persister y motor cargan la reserva con Includes distintos (`ReservaMoneyPersister.cs:44-52` económico; `ReservaAutoStateService.cs:67-74` servicios). En el mismo `AppDbContext` scoped, la raíz es la misma instancia trackeada, pero las colecciones nav se pueblan con queries extra. Si la derivación se pliega en el persister, cuidar qué colecciones ya están cargadas para no depender de un Include ausente.

## Riesgos de integridad de datos

- Cierre prematuro de cabecera con refunds de operador pendientes (B1) → una reserva figura cerrada mientras el ERP todavía espera plata del operador: descuadre operativo silencioso.
- Estado stale por commits separados sin transacción (B2) → header miente hasta próxima mutación humana, sin nocturno que lo cure.
- `PendingOperatorRefund` mostrando circuito de cobro vivo tras T4 (B3) → "Debe"/"Sin movimientos" sobre una reserva en pleno circuito de anulación.

## Riesgos de migración / rollback

- La reparación única toca `Status` de datos productivos. Idempotente por derivación (bien), pero exige: marcador AL FINAL (M2), conteo pre/post contra PROD (lección 2026-07-09, el diseño ya lo cita), y confirmar modo de deploy (M2).
- Columnas T5 (`DerivedCollectionStatus`/`DerivedInvoicingStatus`) nacen null: compatibles hacia atrás con el binario viejo (lecturas recalculan). Correcto. El bug 42701 está bien tratado (§1.4): solo migración EF, cero SQL de arranque para esas columnas. VERIFICADO que los bootstraps de `Program.cs:732/744/756` son de datos, no de schema de `Reserva`.

## Riesgos de seguridad / auditoría

- La transición automática deja rastro con actor "sistema" (`ReservaAutoStateService.cs:40-41` + `ReservaStatusChangeLog`, verificado). Cubre regla 10 para el eje operativo. OK.
- OQ-2 (no auditar cada recomputo de ejes secundarios) es razonable: esos ejes ya tienen rastro en sus flujos de plata/comprobantes. Sin objeción.
- Sin exposición de datos internos en las transiciones (razones en criollo). El texto nuevo de T3/T4 pasa por gate data-exposure, correcto que se marque.

## Riesgos operativos

- Arranque más lento: la reparación itera todas las reservas `{InManagement, Confirmed}` y evalúa cada una. En una base grande, sumar latencia de startup dentro de la ventana de retry de migraciones (`Program.cs:764-770`). No es prod con miles de reservas hoy, pero acotar (batch, log de progreso).
- Sin nocturno, la única red de seguridad ante un estado stale es la próxima mutación (B2). Operativamente eso significa que un descuadre puede quedar invisible indefinidamente. El vigía de coherencia existente (`CoherenceChecks`/`CoherenceWatchdog`) podría detectarlo — evaluar si esos chequeos ya cubren "todo anulado pero Status no-terminal"; si no, es la observabilidad natural de reemplazo del nocturno.

## Gaps de testing

- **T1 rompe seeds de integración Postgres del CI** (el propio diseño lo anticipa; confirmo el riesgo). Con el enganche en el chokepoint, cualquier suite que cancele servicios y luego opere/asserte sobre una reserva `Confirmed` ahora la verá `Cancelled`. Candidatos concretos a revisar (verificados que ejercen estos flujos): `Adr027ConfirmedWithChangesTests` (marca "con cambios" — hay que separar el subcaso "todos anulados" que ahora va a terminal), `ReservaServiceCancellationTests`, `ReservaServiceCancellationWithPaymentTests`, `LifecycleFixes20260624Tests`, `Adr020LifecycleTests`, y las suites de cancelación en `Tests/Cancellation/Integration/*`. Barrer CARGANDO datos reales, jamás relajando asserts (regla operativa del dueño).
- Falta una caminata E2E que ejercite **múltiples BC por servicio + refund de operador parcial** para blindar B1 (no solo el caso feliz de 2 servicios sin multa que propone §5/T1).
- Falta test de la vía atómica de B2: crash simulado entre plata y estado en un caller sin transacción → assert de que el estado no queda stale (o assert de que comparten `SaveChanges`).
- Falta test de B3: reserva en `PendingOperatorRefund` → `isVoided`/circuito de plata correcto tras T4.

## Realismo de esfuerzo / alcance

- No hay estimaciones de tiempo (correcto, regla del dueño).
- El corte en 5 tandas es sensato y en su mayoría independiente, con dos salvedades: **T1↔T4 tienen dependencia dura** (isVoided/semántica B3), y **B1 puede obligar a tocar los callbacks de cierre de `BookingCancellationService`** dentro de T1 — es más código del que sugiere "el motor deriva Anulada". El alcance de T1 está subestimado por B1.
- **T5 puede quedar afuera sin dejar mentiras vivas: CONFIRMADO.** Las mentiras se cierran en T1-T4 (derivación en vivo). T5 solo materializa columnas para filtrar/ordenar. Riesgo de T5: doble fuente transitoria (columna vs recomputo) — el diseño ya manda migrar lecturas "de una". OK diferirlo. **Salvedad:** si T5 introduce `DerivedCollectionStatus`/`DerivedInvoicingStatus` como fuente de lectura, debe respetar B3 (que `isVoided`/anulada cubra `PendingOperatorRefund`), o materializará la misma mentira de B3 de forma persistente.

## Conflictos con ADRs / decisiones previas

- **Sin conflicto** con ADR-020 (ciclo de vida): el motor sigue siendo el único que toca `InManagement↔Confirmed`; agregar el terminal derivado usa el transicionador (punto único), coherente con INV-020-02.
- **Sin conflicto** con ADR-036 (En viaje inmutable): el motor no toca `Traveling` (M5).
- **Tensión con la regla 9** (corregida): el fallback "la próxima mutación corrige" de §2.4 es corrección diferida encubierta (B2). Debe resolverse por la vía atómica, no por idempotencia diferida.
- **Coherente con ADR-033** (reapertura de anulada con gate fiscal): el gate D2 (`ReservaService.cs:1698`) aplica a la auto-anulada sin cambios.

## Preguntas abiertas para el arquitecto / negocio

1. (Deriva de B1) Cuando hay N BC por servicio con refunds de operador en distintos estados y el motor auto-anula, ¿el cierre a `Cancelled` se decide a nivel RESERVA (todas las líneas Settled) y no por-BC? Esto reescribe los guards `:3779/:4656`.
2. (Deriva de B3) ¿`OperationalAxis==SinEfecto` incluye `PendingOperatorRefund`? La respuesta define qué expone el DTO y qué elimina T4.
3. (M2) ¿El deploy del VPS es stop-then-start o rolling? Define si la reparación necesita manejo de concurrencia.
4. (OQ-2) Confirmado con el diseño: solo auditar la transición operativa. Sin objeción del revisor.

## No verificado (lo que NO pude confirmar en esta revisión)

- Cuántos call-sites de `ReservaMoneyPersister.PersistAsync` abren una transacción explícita envolvente (impacta la severidad real de B2). Requiere auditar `PaymentService.cs:1394`, `BookingCancellationService.cs:482/822`, `AfipService.cs:2268` y sus callers.
- El detalle interno de `BuildReservaServiceRowsAsync` (si ya trae el flag de cancelación para T2) — el propio diseño lo marca como TODO.
- El modo exacto de deploy del VPS (M2).
- Si el vigía de coherencia existente (`CoherenceChecks`/`CoherenceWatchdog`) ya detectaría "todo anulado pero Status no-terminal" como red de seguridad de reemplazo del nocturno.
- Nada se corrió contra la app real ni contra el CI: esta es una revisión de diseño sobre código estático. Ninguna caminata E2E de §5 existe todavía.

---

## Resumen accionable

Para pasar de **APROBADO CON CAMBIOS** a **APROBADO**, antes de codear:
1. **B1**: rediseñar la derivación del terminal con N BC y ajustar los callbacks de cierre por-servicio (nivel reserva, no por-BC). Extraer criterio a helper de dominio puro + test con 2+ BC.
2. **B2**: comprometer la vía atómica concreta (derivador puro estático invocado dentro de `PersistAsync` antes de su `SaveChanges`); eliminar el fallback "próxima mutación corrige" como estrategia primaria.
3. **B3**: definir `IsVoided`/`SinEfecto` de forma precisa incluyendo `PendingOperatorRefund`, y reconciliar la narrativa de §1.2 (el terminal no es un único `Status`).
