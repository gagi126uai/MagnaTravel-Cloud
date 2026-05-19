# ADR-007 — `AuditService.LogBusinessEventAsync` hace `SaveChanges` interno (deuda tecnica)

- **Status**: ACCEPTED (con condicion: refactor diferido a sesion futura, ver §5)
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2, observacion del
  senior commit `81b8332` y commit `27506d9`)
- **Related**: ADR-002 cancelacion/refund, principio HC1 ("audit commit deberia
  ir con el flujo principal").
- **Trazabilidad**: observacion arrastrada de FC1.2.2 → FC1.2.3.

## 1. Contexto

`AuditService.LogBusinessEventAsync` (en
`src/TravelApi.Infrastructure/Services/AuditService.cs`) persiste un
`AuditLog` en BD para cada operacion auditable del modulo. Hoy hace su
propio `SaveChanges` **dentro** del metodo.

**Principio HC1**: el audit commit deberia ir junto con el flujo principal
en la misma transaccion EF. Si el flujo principal falla post-audit, el
audit queda huerfano (registra una operacion que no ocurrio). Si el flujo
principal commitea pero el audit falla, hay una operacion sin trazabilidad.

**Decision durante FC1.2** (commit `27506d9` Obs 4): para garantizar
consistencia audit-fiscal **temporal** (mientras no se refactoree
AuditService), el `ClientCreditService.WithdrawAsync` hace `SaveChanges`
del withdrawal/movement/entry **ANTES** de auditar.

```
1. Validar amount, kind, balance.
2. Construir withdrawal + movement.
3. Decrementar entry.RemainingBalance.
4. SaveChangesAsync (commit del flujo principal).
5. AuditService.LogBusinessEventAsync (hace SU PROPIO SaveChanges).
6. OnAllCreditConsumedAsync si aplica (commit propio del BC).
```

Esto evita el caso "auditamos algo que despues no se commitea". Pero
expone el otro caso: si el SaveChanges principal commitea y el audit
falla, no hay rollback (la operacion queda sin audit).

## 2. Decision

**Provisional**: aceptar el patron actual (audit con `SaveChanges`
interno) como **deuda tecnica conocida**.

**Mitigacion**:
- `AuditService.LogBusinessEventAsync` (fix commit `81b8332` Round 3):
  el `catch` interno NO traga estas excepciones criticas (deja que
  propaguen):
  - `BusinessInvariantViolationException`.
  - `DbUpdateConcurrencyException`.
  - `DbUpdateException`.
  Solo traga excepciones de logging / serializacion.
- El caller (service de negocio) hace `SaveChanges` del flujo principal
  ANTES de auditar, asi si el flujo falla el audit ni se llama.
- Tests dedicados (commit `9e63b06` — `AuditLogPresenceTests`) verifican
  que cada operacion realmente persiste su audit log usando el
  `AuditService` real (NO mock). Un mock pasaria sin commitear, el bug
  que estos tests vienen a detectar.

## 3. Consecuencias

### Positivas

- **Funciona para FC1.2** sin refactor invasivo.
- **Tests de audit presence** (10 tests, `9e63b06`) detectan
  inmediatamente si alguien rompe el contrato de "auditar persiste".
- **Excepciones criticas** ya no quedan tragadas (fix `81b8332`).
- **Coherencia**: todos los services del modulo siguen el mismo orden
  (commit principal → audit), facil de revisar.

### Negativas

- **Audit huerfano potencial** si el SaveChanges principal commitea y
  el audit falla por un bug nuevo. Mitigacion debil: el catch del
  AuditService NO traga criticas, asi que la mayoria de fallos del
  audit propagan al caller (queda log + audit log presence test
  fallaria).
- **Doble round-trip a BD** por operacion (uno principal + uno audit).
  Performance trivial para volumenes esperados (decenas de
  cancelaciones por dia).
- **Mas dificil de razonar** el orden temporal cuando hay multiples
  audits + multiples SaveChanges en un mismo flujo (ej.
  `ConfirmAsync` audita 2 veces, hay 3 SaveChanges en juego).

### Riesgos

- **Si el patron HC1 se aplica algun dia en otra parte del codigo**, el
  modulo de cancelacion queda fuera de patron. Cuando se decida
  refactor, sera un cambio grande (toca AuditService + todos los
  services consumers + tests).

## 4. Alternativas consideradas

| Alternativa | Por que NO hoy |
|---|---|
| **Refactor `AuditService.LogBusinessEventAsync` a NO commitear** | Requiere cambiar la firma o convenir que el caller hace `SaveChanges` despues. Toca multiples services consumers + tests. Diferido a sesion dedicada. |
| **Wrap todo en `IDbContextTransaction` explicito** | El service principal abriria una tx, llamaria al audit (sin SaveChanges interno), y commitearia al final. Complejo cuando el `OnAllCreditConsumedAsync` callback tiene su propio scope. Diferido. |
| **Audit asincrono via outbox** | Overengineering para auditoria fiscal (que debe ser sincronica al evento). Rechazado por ADR-002 §2.6. |
| **Tragar todas las excepciones del audit** | Ya estaba asi pre-fix. Resultado: bugs criticos del flujo principal quedaban escondidos. Causa real del over-allocation concurrente. Rechazado por fix `81b8332`. |

## 5. Refactor diferido — condicion para cerrar este ADR

El ADR queda **ACCEPTED con condicion**: refactor de
`AuditService.LogBusinessEventAsync` a NO commitear, programado para una
sesion dedicada futura. Cuando se haga:

1. Cambiar la firma a `Task LogBusinessEventAsync(..., bool saveChanges = false)`.
2. Cada caller decide si commitea ahi mismo o lo hace su propio
   SaveChanges envolvente.
3. Actualizar todos los services consumers del modulo cancelacion.
4. Actualizar tests `AuditLogPresenceTests` para usar el modo "no
   commit" + verificar que el commit envolvente del caller propaga el
   audit.
5. Tests integration que verifiquen "si flujo principal falla, audit
   NO queda persistido" (rollback transaccional verificado).

Effort estimado: medio-alto, riesgo medio (toca un servicio
transversal). Por eso quedo diferido fuera de FC1.2.

## 6. Migration plan / rollback

**Sin migration**. El ADR documenta una decision **provisional** sobre
codigo existente.

**Rollback**: no aplica. Si el refactor futuro rompe algo, se reverte
ese commit, no este ADR.

## 7. Testing strategy

Tests existentes:

- `AuditLogPresenceTests` (10 tests, commit `9e63b06`) — usan
  `AuditService` real, verifican que cada operacion persiste su audit
  log esperado en BD.
- Fix `81b8332` Round 3 — el `catch` de AuditService NO traga
  excepciones criticas (verificado leyendo `AuditService.cs:31`).

Tests pendientes para cuando se haga el refactor:

- Test "flujo principal falla post-audit interno → audit huerfano
  observado" (estado actual, sirve de baseline antes del refactor).
- Test "flujo principal falla post-audit modo nuevo → audit
  rollback" (verifica el refactor).

## 8. Auto-critica

- **Verificado en repo**: si — `AuditService.cs` linea 31 catch que
  NO traga criticas. Services usan el patron `SaveChanges` principal →
  audit (verificado en `WithdrawAsync`, `ConfirmAsync`,
  `AllocateAsync`).
- **Coherencia con principio HC1**: NO. La decision es explicita —
  aceptamos la deuda hoy, planificamos el refactor.
- **Mantenible por otros**: si — el patron es uniforme en todos los
  services del modulo cancelacion. El ADR documenta el por que y el
  refactor diferido.
- **Cuando programar el refactor**: idealmente antes de extender el
  modulo a otros productos (vuelos, paquetes, traslados). Si se
  extiende sin refactor, la deuda se duplica.
