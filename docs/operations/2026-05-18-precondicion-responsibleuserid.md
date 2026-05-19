# Precondicion operativa pre-prod: backfill `TravelFiles.ResponsibleUserId`

- **Audiencia**: Ops + Product Owner.
- **Aplica a**: cualquier deploy a produccion que habilite el feature flag
  `OperationalFinanceSettings.EnableNewCancellationFlow = true`.
- **Origen**: BR-V2-02 del plan tactico FC1.2 v3 (§10.2.1). Cierra
  bloqueante del review previo a aceptar opcion (a) del bypass de
  approval ([ADR-004](../architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md)).

## Que es

El nuevo modulo de cancelacion de reservas usa el sistema de **ownership**
para autorizar las acciones del Vendedor sobre **sus** BCs (Booking
Cancellations). El ownership del BC **hereda** del
`TravelFile.ResponsibleUserId` (campo en la entidad `TravelFile`, que es
el nombre real de la tabla `Reservas` en BD).

Si una `TravelFile` activa NO tiene `ResponsibleUserId` (es NULL) y un
Vendedor intenta cancelarla, el decorator de ownership devuelve **403** —
porque no hay como decidir si es "suya". El Vendedor queda bloqueado.

Esta query bloqueante verifica que ninguna reserva activa quede en ese
estado antes de habilitar el flag.

## Como verificar

Correr esta query en la BD de **produccion** (read-only, sin escritura):

```sql
SELECT COUNT(*) FROM "TravelFiles"
WHERE "Status" NOT IN ('Closed', 'Cancelled', 'Archived')
  AND "ResponsibleUserId" IS NULL;
```

**Resultado esperado**: `0`.

### Lectura del resultado

#### Caso A — resultado = 0

**Accion**: ninguna. El feature flag puede habilitarse sin riesgo. El
ownership BC → TravelFile.ResponsibleUserId resuelve correctamente para
todas las reservas activas.

#### Caso B — resultado > 0

**El flag NO debe habilitarse hasta resolver una de las siguientes
opciones**.

##### Opcion B.1 — Backfill (RECOMENDADA)

Ejecutar el comando administrativo `users.set-responsible` (provisto por
B1.15 Fase J) sobre cada reserva afectada. Eso asigna un responsable
deterministico (Admin o vendedor que creo la reserva).

Pasos:

1. Listar las reservas afectadas:
   ```sql
   SELECT "Id", "PublicId", "Status", "CustomerId", "CreatedAt"
   FROM "TravelFiles"
   WHERE "Status" NOT IN ('Closed', 'Cancelled', 'Archived')
     AND "ResponsibleUserId" IS NULL;
   ```
2. Decidir el responsable (Admin default, o un vendedor especifico).
3. Ejecutar el comando administrativo (endpoint `POST /admin/users/set-responsible`
   o equivalente — verificar contra B1.15 docs).
4. Re-correr la query bloqueante para confirmar que devuelve 0.
5. Habilitar el flag.

##### Opcion B.2 — Aceptar restriccion soft

Documentar que las reservas afectadas solo podran cancelarse via
usuarios con permiso `ReservasViewAll` (Admin / Colaborador con
bypass), NO via Vendedor con solo `ReservasView`. El ownership decorator
devolveria 403 al Vendedor; un Admin / Colaborador con ViewAll lo
podria hacer.

**Riesgo aceptado**: el Vendedor no ve sus reservas viejas en la lista
de BCs. Cuando un cliente lo llame para cancelar, el Vendedor tendra que
escalar a un Admin.

Documentar esta decision en el ticket de deploy + avisar al equipo
operativo.

## Cuando se chequea

**Antes de cada deploy a produccion** que ponga
`EnableNewCancellationFlow = true` en el `OperationalFinanceSettings` de
una agencia. La query es bloqueante: si no se corre, el deploy va a la
ceremonia de ops, no se hace flip flag.

Si el flag ya esta en `true` desde un deploy anterior, no es necesario
re-correr la query (las reservas nuevas creadas post-flag tienen
`ResponsibleUserId` set obligatorio por el flujo de creacion). Solo
relevante en el momento del primer flip.

## Quien lo aprueba

- **Ops**: ejecuta la query y lee el resultado. Si es Caso B, propone
  Opcion B.1 o B.2.
- **Product Owner**: aprueba la Opcion B.1 (recomendada) o autoriza la
  Opcion B.2 con riesgo aceptado.
- **Backend reviewer** (`backend-dotnet-reviewer`): puede solicitar
  cualquiera de las dos sin objecion — la decision es de negocio.

## Trazabilidad

Adjuntar al ticket de deploy:

1. Captura / output de la query.
2. Si Caso B: detalle de las reservas afectadas + opcion aplicada.
3. Si Opcion B.1: lista de comandos `users.set-responsible` ejecutados.
4. Re-corrida de la query mostrando 0 (post-backfill).

Si Caso B sin backfill (Opcion B.2): documentar en el changelog del
deploy "Cancelacion: N reservas activas sin responsable — Vendedor no
las vera en su lista de BCs hasta backfill". Aviso por email al equipo
operativo + product owner.

## Referencias

- Plan tactico FC1.2 v3 §10.2.1 (precondicion operativa flag prod).
- BR-V2-02 cierre en commit del plan (`bd500e8`).
- ADR-004 ([Bypass approval NC](../architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md)) — el bypass depende del override, que a su vez depende de
  un BC con ownership resoluble.
- Documento explicativo de la sesion 2026-05-18 ([fc1-2-implementacion.md](../explicaciones/2026-05-18-fc1-2-implementacion.md)).
- B1.15 Fase J — implementacion del comando `users.set-responsible`.
