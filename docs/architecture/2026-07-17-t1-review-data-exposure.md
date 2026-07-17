# Data-exposure review — T1 (ADR-048, transición automática a Anulada/Esperando reembolso + migración de reparación)

**Fecha**: 2026-07-17
**Alcance revisado**: diff sin commitear (`git status`/`git diff`) — backend puro:

- `src/TravelApi.Infrastructure/Reservations/ReservaMoneyPersister.cs` (modificado)
- `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` (modificado)
- `src/TravelApi.Infrastructure/Services/ReservaService.cs` (modificado)
- `src/TravelApi.Infrastructure/Services/Reservations/ReservaAutoStateService.cs` (modificado)
- `src/TravelApi.Domain/Reservations/ReservaDerivedState.cs` (nuevo)
- `src/TravelApi.Domain/Reservations/ReservaTerminalDerivation.cs` (nuevo)
- `src/TravelApi.Infrastructure/Reservations/ReservaTerminalTransitionApplier.cs` (nuevo)
- `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260717182416_Adr048_M1_RepairLegacyAnnulledReservaState.cs` (+ `.Designer.cs`, nuevo)
- Tests (`Adr020LifecycleTests.cs`, `Adr020StatusUnlockTests.cs`, `Adr025PartialAndMultiOperatorCancellationTests.cs`, `Adr048ReservaTerminalDerivationE2ETests.cs`, `ReservaDerivedStateTests.cs`, `ReservaTerminalDerivationTests.cs`) — no user-facing, no revisados línea por línea salvo para confirmar que no filtran nada.

No hay archivos de frontend ni de controladores (`TravelApi/Controllers/**`) en este diff. No se agregó ni modificó ningún endpoint, DTO de respuesta ni contrato de API.

## Veredicto

**OK — sin bloqueantes.**

Un hallazgo no bloqueante (recurrencia de un patrón ya existente en el repo) detallado abajo.

## Qué se verificó

### 1. Motivos (`reason`) de la transición automática nueva — `ReservaTerminalTransitionApplier.cs:75-79`

Los dos textos que el motor en vivo escribe en `ReservaStatusChangeLog.Reason` cuando auto-transiciona una reserva:

```
"Todos los servicios de la reserva quedaron anulados. Como el operador todavía no devolvió
el dinero de alguna cancelación, la reserva queda esperando ese reembolso (sistema)."

"Todos los servicios de la reserva quedaron anulados y no hay ningún reembolso del operador
pendiente: la reserva queda anulada (sistema)."
```

Español de negocio limpio. Sin GUIDs, sin nombres de clase/tabla, sin enums crudos, sin "ADR-048" ni códigos internos. **OK.**

### 2. ¿Este `Reason` llega a la pantalla? — verificado que NO, hoy

El prompt de la tarea advertía que "el texto del rastro de auditoría se muestra en pantalla en la ficha". Lo verifiqué explícitamente y **la premisa no se sostiene en el código actual**:

- `grep "ReservaStatusChangeLog"` sobre `src/TravelApi/Controllers/**` → 0 resultados. Ningún controller expone `ReservaStatusChangeLog` ni su campo `Reason`.
- `grep "FromStatus|ToStatus|fromStatus|toStatus"` sobre `src/TravelWeb/src/**` → 0 resultados. Ningún componente de React consume filas de ese log.
- El campo que SÍ se renderiza en la ficha (`ReservaLockBanner.jsx:54`, `ReservaDetailPage.jsx`) es `reserva.lastRegressionReason` (`Reserva.LastRegressionReason`), que es una entidad **distinta** de `ReservaStatusChangeLog.Reason` y que este diff no toca en la vía nueva (`ReservaTerminalTransitionApplier` no escribe `LastRegressionReason`; `ReservaStatusTransitioner.ApplyCleanupAsync` solo lo **limpia/pone en null** al entrar a un terminal — nunca lo llena con el `reason` de esta transición).

Confirma la memoria de este agente: `ReservaStatusChangeLog.Reason` sigue siendo **write-only** hoy — no hay ningún endpoint GET ni componente que lo lea. Esto es un hecho verificado por lectura estática, no una suposición de runtime.

### 3. Migración de reparación — `20260717182416_Adr048_M1_RepairLegacyAnnulledReservaState.cs:124-143`

Mismo campo (`ReservaStatusChangeLogs.Reason`), pero con SQL crudo. Los dos textos que inserta:

```sql
'Reparacion de datos historicos (ADR-048): todos los servicios de la reserva ya estaban anulados
y el operador todavia debe algun reembolso. La reserva queda esperando ese reembolso (sistema).'

'Reparacion de datos historicos (ADR-048): todos los servicios de la reserva ya estaban anulados
y no hay reembolso del operador pendiente. La reserva queda anulada (sistema).'
```

Español de negocio, pero **incluye el literal `ADR-048`** — un código interno de decisión de arquitectura. No es un bloqueante hoy porque (ver punto 2) el campo no se lee en ningún lado. Ver hallazgo no bloqueante abajo.

### 4. Logs nuevos (`_logger.LogDebug` / `_logger.LogInformation`) — `BookingCancellationService.cs`

- Línea ~4048 (nueva, reemplaza el log anterior): `"CloseReservaIfOperatorRefundComplete: la reserva {ReservaPublicId} todavia tiene reembolso del operador pendiente en alguna de sus cancelaciones..."` — usa `ILogger`, va al log de servidor (Serilog/consola/archivo), no a ninguna respuesta HTTP ni a la UI. **OK**, no es una superficie de usuario. Nota: usa `PublicId` (GUID), que es aceptable en logs de servidor (no expuestos al usuario) — no es el mismo caso que un GUID mostrado en pantalla.
- Línea ~4758 (nueva): mismo patrón, log de servidor únicamente. **OK.**

Ninguno de estos logs se materializa en una respuesta de API: los métodos que los contienen (`CloseReservaIfOperatorRefundComplete`, `OnAllCreditConsumedAsync`) son privados y no retornan estos strings al caller HTTP.

### 5. Excepciones nuevas

No se agregó ningún `try/catch` nuevo, ningún `throw` nuevo con mensaje técnico, ni ningún cambio en el mapeo de excepciones a respuesta HTTP. Los métodos nuevos (`ReservaTerminalDerivation.*`, `ReservaDerivedState.HadServicesAndAllCancelled`, `ReservaTerminalTransitionApplier.ApplyIfNeededAsync`, `BookingCancellationService.IsReservaOperatorRefundPendingAsync` ×2) son funciones puras / de infraestructura sin manejo de excepciones propio; cualquier excepción no capturada burbujea al `catch` genérico ya existente en el controller que los invoca indirectamente (`ReservasController`/`CancellationsController`), que no fue tocado por este diff.

### 6. Respuestas de API alteradas

Ningún DTO (`ReservaDto.cs`, etc.) fue tocado. `ReservaAutoStateService.EvaluateAndApplyAsync` ganó un parámetro `skipTerminalDerivation` (booleano interno de C#, nunca serializado) y sigue devolviendo `bool` (`anyChange`), igual que antes. `ReservaMoneyPersister` no expone nada nuevo a un controller. **Sin cambios de contrato de API.**

### 7. Enum crudo `PendingOperatorRefund` / `Cancelled`

Estos dos estados ya existían antes de este diff (no son nuevos). Verificado que el frontend ya tiene mapeo a etiqueta en español (`ReservaStatusBadge.jsx:80` para `PendingOperatorRefund`), sin tocar por este cambio. El diff solo hace que el motor **llegue** más seguido a esos estados ya existentes y ya traducidos — no introduce un estado nuevo sin traducir.

## Exposiciones bloqueantes

Ninguna encontrada.

## Exposiciones no bloqueantes

1. **`ADR-048` embebido en el texto de `Reason` de la migración de reparación** — `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260717182416_Adr048_M1_RepairLegacyAnnulledReservaState.cs:135-138`.
   - Hoy no llega a ningún usuario (campo write-only, verificado punto 2). No bloqueante.
   - Es la **misma práctica ya establecida** en el resto del código (`BookingCancellationService.cs:3874,4007,4732,6044,6172` ya escriben `"Cancelacion (ADR-002): ..."` en el mismo campo `Reason`) — no es una regresión nueva de este diff, es consistencia con un patrón preexistente que ya venía "sucio".
   - Nótese la inconsistencia dentro del propio diff: el código en vivo nuevo (`ReservaTerminalTransitionApplier.cs`) **no** incluye "ADR-048" en su texto — es más limpio que la migración. Recomiendo alinear la migración al mismo criterio (sacar el `(ADR-048)` del string SQL) para no seguir engordando un campo de auditoría con jerga interna, el día que alguien decida exponer este historial en una pantalla (hay una `AuditPage.jsx` para admins que ya renderiza otras tablas de auditoría con códigos crudos — ver memoria `audit-page-renders-action-and-details-raw` — así que el riesgo de que a alguien se le ocurra sumar este log ahí no es hipotético).
   - Fix sugerido: `'Reparacion de datos historicos: todos los servicios de la reserva ya estaban anulados...'` (sin el paréntesis `(ADR-048)`), igual de informativo para quien audite la base de datos.

## Backend: respuestas y errores revisados

No aplica cambio de contrato — no hay controllers en el diff. Los métodos de servicio tocados (`ReservaMoneyPersister.PersistAsync` (interno), `ReservaService.UpdateBalanceAsync` (llamada a `EvaluateAndApplyAsync` con el nuevo flag), `ReservaAutoStateService.EvaluateAndApplyAsync`, `BookingCancellationService.CloseReservaIfOperatorRefundComplete`, `BookingCancellationService.OnAllCreditConsumedAsync`) no devuelven strings nuevos a ningún caller HTTP; todo lo nuevo queda en: (a) el estado persistido de la reserva (ya traducido en el frontend), (b) el campo `Reason` write-only, o (c) logs de servidor.

## Frontend: superficies revisadas

Ninguna tocada por este diff. Se revisó, para descartar la premisa del brief, si `ReservaStatusChangeLog.Reason`/`FromStatus`/`ToStatus` se renderiza en algún componente (`ReservaHeader.jsx`, `ReservaDetailPage.jsx`, `ReservaLockBanner.jsx`, `RevertStatusModal.jsx`, `ReservaStatusBadge.jsx`) — ninguno lo lee; el único campo de "motivo" que se pinta en pantalla es `lastRegressionReason`, que este diff no llena con el nuevo texto.

## Otras superficies

No hay PDF/voucher/recibo, WhatsApp/email, ni exports tocados por este diff. Los únicos side-channels nuevos son (a) el campo `Reason` en `ReservaStatusChangeLogs` (DB, no expuesto) y (b) logs de `ILogger` (servidor, no expuestos).

## Fallback amistoso presente?

No aplica — no se agregó ningún camino de error nuevo hacia el usuario en este diff.

## Missing tests

No es estrictamente necesario para este diff (no hay superficie de usuario nueva), pero como recomendación de dureza a futuro: si algún día se agrega un endpoint que exponga `ReservaStatusChangeLog`, agregar un test que falle si `Reason` contiene un patrón `ADR-\d+` o cualquier identificador con guion tipo código — así un futuro "vamos a mostrar el historial en la ficha" no arrastra el string crudo de la migración sin que alguien lo note.

## No verificado

- Comportamiento en runtime (no se corrió la app ni se ejecutaron los tests de este diff como parte de esta revisión; el análisis es 100% estático, por lectura de código).
- No se revisó si `_repair_20260717_reserva_terminal_candidates` (tabla de respaldo temporal creada por la migración) es accesible desde algún panel de administración de datos fuera del código de este repo (fuera de alcance de esta revisión de exposición al usuario final).
