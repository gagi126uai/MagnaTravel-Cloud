# T1 modelo de estados derivados — Review de seguridad y riesgo de datos (2026-07-17)

Revisor: security-data-risk-reviewer. Alcance: diff sin commitear (Tanda 1 ADR-048):
transición automática al terminal del par, cierre de reembolsos de operador a
nivel reserva, y migración de reparación de datos históricos.

**No se corrió la app real ni el CI.** Este review es sobre código estático + verificación
de nombres de schema contra el snapshot EF. Distingo hecho verificado / riesgo / no verificado.

---

## VEREDICTO: APROBADO CON COMENTARIOS (0 bloqueantes)

La transición automática NO mueve plata ni emite comprobantes (INV-048-02 verificado en
código). La migración es data-only, idempotente, conservadora (no puede anular una reserva
viva por falso positivo), con rastro auditable y backup forense. Los nombres de schema del
SQL crudo están todos verificados contra el snapshot EF. El fix del cierre a nivel reserva
(B1) es correcto en el camino secuencial y está testeado E2E.

Queda **un riesgo residual de concurrencia** (stranding en "Esperando reembolso") que NO
es bloqueante hoy (mono-usuario, sin pérdida de plata, mentira en dirección conservadora,
sin healing automático) pero que debe anotarse para el frente de concurrencia multi-usuario.

---

## Hechos verificados (archivo:línea)

1. **La transición automática NO toca plata ni comprobantes.**
   `ReservaTerminalTransitionApplier.ApplyIfNeededAsync` (`ReservaTerminalTransitionApplier.cs:53-90`)
   sólo llama a `ReservaStatusTransitioner.ApplyAsync`, que escribe el log, setea `Status`
   y limpia marcas (`ReservaStatusTransitioner.cs:73-92`). No hay emisión de NC/ND, no hay
   recálculo de saldo, no hay notificación al cliente. **INV-048-02 confirmado por código.**
2. **Vía atómica (B2).** El applier corre ANTES del `SaveChanges` del persister
   (`ReservaMoneyPersister.cs:82`, `SaveChanges` en `:86`), sobre el mismo `AppDbContext` y
   la misma instancia trackeada → estado y saldo en el MISMO commit. Verificado.
3. **El persister carga las 6 colecciones** (`ReservaMoneyPersister.cs:51-57`: Servicios +
   5 tipadas), que es exactamente lo que `HadServicesAndAllCancelled` recorre
   (`ReservaDerivedState.cs:39-44`). No se le escapa un servicio vivo de un tipo no incluido.
4. **No hay doble corrida del motor.** `ReservaService.cs:5012` pasa
   `skipTerminalDerivation: true` porque `RecalculateMoneyAsync` ya pasó por el persister
   (M1). En ese camino, si la reserva quedó Cancelled/PendingOperatorRefund, `isEngineState`
   es false (`ReservaAutoStateService.cs:103-105`) → el bloque entero se saltea, sin
   auto-confirmar ni marcar "confirmada con cambios" ni notificar. Verificado.
5. **Sin notificación al cliente en la rama terminal.** Cuando `terminalTransitioned==true`,
   `ReservaAutoStateService.cs:120-123` sólo setea `anyChange`; NO entra al `else if` de
   `NotifyNeedsReviewAsync` (`:136-156`). La campana no suena en la anulación automática.
6. **Nombres de schema del SQL crudo — TODOS verificados contra `AppDbContextModelSnapshot.cs`:**
   - Reserva → tabla `TravelFiles`, cols `Status`, `HasUnacknowledgedChanges`,
     `ChangesPendingSince`, PK `Id` (`:5029` ToTable).
   - Las 5 tipadas + genérico (`Reservations`) referencian por columna `TravelFileId`
     (FlightSegments/Hotel/Transfer/Package/Assistance/ServicioReserva — verificado
     `HasColumnName("TravelFileId")` en cada una; ServicioReserva `ToTable("Reservations")`).
   - `BookingCancellations` usa columna `ReservaId` (la asimetría real, no un typo).
   - `BookingCancellationLines`: cols `BookingCancellationId`, `RefundCap`, `RefundStatus`(int).
   - `ReservaStatusChangeLogs`: required = `Direction/FromStatus/ToStatus`; el INSERT provee
     todos los required + `PublicId/ReservaId/ByUserId/ByUserName/Reason/OccurredAt`; no falta
     ninguna columna NOT NULL (los `AuthorizedBySuperior*` son nullable).
   - `ReservaPendingChanges.ReservaId` existe.
7. **Los literales de "cancelado" del SQL coinciden (o son un SUBCONJUNTO conservador) del C#:**
   - Vuelo: SQL `UPPER("Status") IN ('UN','UC','HX','NO')` vs C# `MapFlightStatus`
     (`WorkflowStatusHelper.cs:27`, mismos 4 códigos).
   - Genérico: SQL `LOWER("Status") LIKE 'cancel%'` vs C# `MapGenericStatus`
     (`WorkflowStatusHelper.cs:46`, `StartsWith("cancel")`).
   - `RefundStatus <> 2` == `!= Settled` (`BookingCancellationLineRefundStatus.cs:36` Settled=2).
8. **La reserva SIN servicios NO se anula.** El SQL usa `JOIN reserva_service_summary`
   (INNER) + `total_count >= 1` (`migración :112,:115`); una reserva sin filas en ninguna de
   las 6 tablas no aparece. Igual que `HadServicesAndAllCancelled` (`ReservaDerivedState.cs:46`,
   `totalServiceCount >= 1`). Cubierto por unit test `ReservaVacia_SinNingunServicio_NoEstaAnulada`.
9. **Rastro auditable del sistema.** Migración: INSERT en `ReservaStatusChangeLogs` con
   `ByUserId='system:auto-state'`, `ByUserName='Sistema (motor de estados)'`, `Reason` en
   criollo, `OccurredAt=now()`, ANTES del UPDATE, guardado `WHERE tf."Status"=c.from_status`
   (`migración :124-143`). Vía en vivo: mismo actor sistema (`ReservaTerminalTransitionApplier.cs:33-34`).
   **INV-048-04 confirmado.**
10. **Idempotencia de la migración.** `CREATE TABLE IF NOT EXISTS ... AS` (foto de candidatos
    no se recalcula en una 2da corrida) + los 3 pasos guardan por `Status` VIVO = `from_status`
    (`:142,:157`), así una corrida doble no re-audita ni re-repara. `Down` no-op forense.

---

## BLOQUEANTES

Ninguno.

---

## RIESGOS ACEPTABLES (no bloqueantes)

### R1 — Stranding en "Esperando reembolso" con 2+ cancelaciones saldadas EN CONCURRENCIA (el más importante)
El cierre `PendingOperatorRefund → Cancelled` de una reserva con N cancelaciones se decide
consultando las líneas de las OTRAS BC desde BD (`BookingCancellationService.cs:3792-3799`
y `:3781-3789`, AsNoTracking, read-committed). Si dos reembolsos de operador de DOS BC
distintas de la MISMA reserva se imputan en transacciones que se solapan:
- Tx A (salda BC1) no ve el settle no-commiteado de BC2 → cree que BC2 sigue pendiente → no cierra.
- Tx B (salda BC2) no ve el settle no-commiteado de BC1 → cree que BC1 sigue pendiente → no cierra.
- Ambas commitean: ambas BC saldadas, **ninguna cerró la reserva → queda colgada en
  `PendingOperatorRefund` para siempre.**

Por qué NO es bloqueante hoy:
- **No mueve plata ni genera doble crédito** (la transición sólo cambia el cartel, INV-048-02).
- La mentira es en dirección **conservadora**: muestra "esperando reembolso" cuando en
  realidad ya se saldó (nunca al revés → nunca oculta plata pendiente).
- Mono-usuario hoy (Gaston es el único operador): dos imputaciones solapadas a la misma
  reserva son prácticamente inalcanzables. El xmin/retry de `AllocateAsync`
  (`OperatorRefundService.cs:296-316`) protege el refund/BC, no la coherencia cross-BC.
- El caso secuencial (que es el real hoy) está testeado y pasa: E2E-2
  (`Adr048...E2ETests.cs:46-126`).

Por qué igual hay que anotarlo fuerte (es exactamente el "dejar colgada una reserva" que se
pidió mirar): **no existe healing automático.** El applier ignora `PendingOperatorRefund`
(`ReservaTerminalTransitionApplier.cs:56`, sólo InManagement/Confirmed) y la reconciliación
nocturna sólo toma `{InManagement, Confirmed}` (`ReservaLifecycleAutomationService.cs:183`).
Una vez saldadas ambas BC, ningún evento futuro vuelve a disparar un callback de cierre → la
reserva no se auto-sana. Recomendación (frente concurrencia multi-usuario): tomar lock de fila
sobre `TravelFiles` (o serializable) al evaluar el cierre, y/o que un reconciliador de reserva
pueda cerrar `PendingOperatorRefund → Cancelled` cuando todas las líneas con `RefundCap>0`
están Settled.

### R2 — Riesgo simétrico en `OnAllCreditConsumedAsync`
Mismo patrón: consulta todas las líneas de la reserva desde BD
(`IsReservaOperatorRefundPendingAsync(reservaId, ct)`, `BookingCancellationService.cs:4712`).
Si un reembolso de operador de OTRA BC está imputado pero no commiteado, podría no verlo y
cerrar prematuro (cierre-de-más). Misma probabilidad (concurrencia real) y mismo contexto
mono-usuario. Bundle con R1.

### R3 — La clasificación "cancelado" del SQL es SUBCONJUNTO del C# (whitespace)
El SQL no hace `TRIM`; el C# sí (`status.Trim()...`). Un estado legacy con espacios
(`" cancelado"`, `" UN"`) el C# lo lee cancelado, el SQL no. Consecuencia: el SQL puede
**dejar SIN reparar** una reserva legacy con estados con espacios (falso negativo, dirección
segura) pero **nunca puede marcar cancelado un servicio que el C# considera vivo** (SQL-cancelado
⊆ C#-cancelado) → **no puede anular una reserva viva por esta divergencia.** El doc dice
"EXACTAMENTE el mismo criterio"; en rigor es un subconjunto conservador. No bloqueante.

### R4 — La migración barre `Traveling` (M5)
Decisión de diseño explícita y aprobada (§6 del diseño): el motor en vivo respeta ADR-036
(Traveling inmutable) pero la reparación única sí toca Traveling para sanear datos legacy.
Como la clasificación es conservadora (R3), no anula un Traveling con algún servicio vivo.
Aceptable, documentado.

### R5 — "ADR-048" en el `Reason` de la migración
Los `Reason` de la migración dicen "Reparacion de datos historicos (ADR-048)..."
(`migración :136-137`). Si el historial de estados se muestra al usuario, "ADR-048" es jerga
interna. Los `Reason` de la vía en vivo (`ReservaTerminalTransitionApplier.cs:75-79`) están
limpios. Menor; queda para el gate `data-exposure-reviewer`.

### R6 — Tabla `_repair_20260717_...` queda para siempre
Backup forense deliberado (igual que el precedente). Sin PII (sólo reserva_id/status). Se
acumula como clutter de schema. Aceptable, coincide con precedente.

### R7 — Invariante latente: un caller nuevo del applier que no cargue las 6 colecciones
Si un caller futuro invoca `ApplyIfNeededAsync` sin Include de las 6 colecciones, un servicio
vivo pasaría desapercibido → anulación incorrecta. Los 2 callers actuales cargan las 6. Está
documentado en el XML doc del applier (`:44-52`). Latente, no activo.

---

## Seguridad y privacidad

- **Sin datos sensibles nuevos en logs.** Los nuevos logs
  (`BookingCancellationService.cs:3855-3860`, `:4718-4723`, `:4757-4759`) usan `PublicId`
  (Guid público), no IDs internos, sin montos, sin PII. OK.
- **Autorización: sin cambios.** La transición es un side-effect del sistema dentro de
  chokepoints ya autorizados (cancelación/cobro/persister). No hay endpoint nuevo ni cambio
  de permisos. Sin ampliación de superficie de authz.
- **Scoping.** Todas las queries del applier/callbacks filtran por `reserva.Id` /
  `ReservaId`; no hay fuga cross-reserva.

---

## Integridad y concurrencia

- Doble crédito: **imposible** por esta transición (INV-048-02, no toca plata).
- Atomicidad estado+saldo: **garantizada** (misma SaveChanges, verificado).
- Cierre-de-más (premature close, el bug B1 viejo): **corregido** y testeado (E2E-2 secuencial).
- Cierre-de-menos (stranding): **posible sólo bajo concurrencia real** (R1/R2), sin healing.
- xmin: el applier lee BC lines AsNoTracking (no pelea con el grafo trackeado); el retry xmin
  existente protege refund/BC, no la coherencia cross-BC (raíz de R1).

---

## Migración / rollback

- Data-only, sin cambios de schema (ModelSnapshot intacto). Sin NOT NULL sobre tabla poblada.
- Idempotente (guardas por Status vivo). Reversible: `Down` no-op deliberado; el estado
  previo queda en `from_status` de la tabla `_repair_*` y en `ReservaStatusChangeLogs`.
- No toca comprobantes con CAE (INV-048-02) → no hay irreversibilidad fiscal en juego.
- **Pendiente operativo (lección 2026-07-09):** conteo pre/post contra PROD antes y después
  del deploy. El diseño (§3) lo exige; NO verificado que se haya corrido.

---

## Auditoría

Cubierta: quién (sistema), cuándo (OccurredAt/now), qué (From/To Status), por qué (Reason).
Tanto en vivo como en la migración. INV-048-04 OK.

---

## Tests faltantes

1. **INV-048-02 sin assert E2E directo.** El diseño planeaba E2E-1 ("assert que NO se emitió
   NC por la transición"); el archivo sólo tiene E2E-2 y E2E-3. Verificado por inspección de
   código, no por test. Recomendado: un test que anule el último servicio y afirme que el
   conteo de comprobantes de la reserva no cambió.
2. **La migración SQL no tiene test.** Ningún test siembra una "reserva-mentira" legacy y
   afirma que la migración (a) la repara al terminal correcto y (b) NO toca una reserva viva.
   Mitigado por: unit tests del helper de dominio (13 casos), stop-then-start, clasificación
   conservadora, conteo pre/post PROD. Aun así el SQL crudo queda sin cobertura ejecutada.
3. **Concurrencia R1/R2 sin test** (dos imputaciones solapadas a 2 BC de la misma reserva).
   Coherente con "InMemory no ejercita locks/aislamiento" (limitación conocida del stack).

---

## Necesita confirmación humana / profesional

- Nada fiscal nuevo (la transición no emite comprobantes). El circuito NC/ND ya deployado no
  se toca. No requiere firma de contador para ESTA tanda.

---

## No verificado

- CI verde (unit + integración Postgres). No se corrió.
- App real (caminata E2E manual de la anulación por servicio → cartel correcto).
- Conteo pre/post de la migración contra PROD (cuántas reservas repara, y que ninguna viva
  entre en el conjunto).
- Que no exista un trigger de BD sobre `TravelFiles` que reaccione al UPDATE de `Status`
  (asumido inexistente; no verificado).
