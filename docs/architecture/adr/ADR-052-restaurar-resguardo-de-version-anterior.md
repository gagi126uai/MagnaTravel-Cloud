# ADR-052 — "Restaurar todo" acepta resguardos de versiones anteriores (restaurar en una base nueva, intercambiar nombres y actualizar el esquema solo)

- **Status**: negocio **ACEPTADO** (firmado por el dueño el 2026-07-29). Parte técnica: **REVISIÓN 2**,
  cierra los 4 bloqueantes (B1–B4) y los 3 menores (M1–M3) del `software-architect-reviewer`.
- **Date**: 2026-07-29 (rev. 2 el mismo día)
- **Autor**: software-architect, sobre código real inspeccionado (§1)
- **Related**: ADR-051 ("Empezar de cero" / "Volver atrás", principio fail-closed),
  `docs/explicaciones/2026-07-29-backup-restore-arreglado-y-datos-recuperados.md` (deuda nueva #1),
  `docs/db-operations.md` (runbook `ops-restore.yml` + comandos de rescate manual).
- **Reglas de la constitución**: **F-16** (motivo obligatorio + auditoría), **T-5** (ningún nombre
  interno en respuestas ni textos de usuario), **P-9** (botón que el motor no permite: apagado con
  motivo visible **o** escondido).
- **Numeración**: los archivos llegan a 044, pero 045/048/050/051 ya están citados en `src/`
  (`grep ADR-05`); el siguiente libre es **052**.

> **Qué cambió en la rev. 2** (para quien leyó la rev. 1): la restauración ya **no** se hace con
> `pg_restore --clean` sobre la base viva. Se restaura en una base **nueva al costado** y se
> **intercambian los nombres**; la vuelta atrás pasa a ser otro intercambio de nombres (segundos, sin
> segundo `pg_restore`). El veredicto de versión se calcula contra el **ensamblado** (no contra la base
> viva), el ajuste de AFIP corre **después** de migrar, los **backfills** ADR-021/022/025 entran en el
> paso de actualización, y un resguardo "más nuevo" **no apaga el botón** (avisa y deja que el motor
> rechace sin tocar nada).

## 1. Contexto (hechos verificados en el repo, 2026-07-29)

- El candado de compatibilidad exige **igualdad exacta** de migraciones:
  `PgDatabaseRestorePort.CheckSchemaCompatibilityAsync` → `if (!dumpMigrations.SetEquals(liveMigrations))`
  (`src/TravelApi.Infrastructure/Services/PgDatabaseRestorePort.cs:493`), y el caller rechaza antes de
  tocar nada (`SystemDataRestoreService.cs:456-465`).
- **Consecuencia real en PROD**: cada deploy con una migración deja inservibles TODOS los resguardos
  anteriores. El 2026-07-29 hubo que recuperar los datos por `ops-restore.yml` porque la app rechazó
  (bien, según la regla de hoy) un resguardo de 2 días.
- El restore total de hoy es `pg_restore --no-owner --clean --if-exists --single-transaction` **sobre la
  base viva** (`PgDatabaseRestorePort.cs:270-272`). **`--clean` solo dropea lo que está en el índice del
  dump**: cualquier objeto que exista en la base viva y NO en el dump sobrevive → esquema híbrido, o
  aborto de la transacción entera por lo que quedó de más. Es la causa del bloqueante B1.
- El resguardo previo obligatorio (`pre-restore-<ts>.dump` + copia de MinIO) ya existe y se crea antes de
  tocar la base viva (`SystemDataRestoreService.cs:481-498`).
- La API **no puede** disparar el contenedor `migrate`: el servicio `api` de `docker-compose.yml` monta
  solo `./uploads`, `./logs` y `./backups/postgres/wipe`; no monta el socket de Docker. El contenedor
  `migrate` corre `dotnet TravelApi.dll --migrate-only` (`docker-compose.yml:166-176`), que hace EN
  ORDEN: 3 bootstrappers de SQL crudo (OperationalFinance, RefreshToken, BNA) → `MigrateAsync()` → 3
  backfills idempotentes (ADR-021, ADR-022, ADR-025), cada uno con `NeedsBackfillAsync` barato y hoy con
  `try/catch` que **loguea y sigue** (`src/TravelApi/Program.cs:742-915`).
- Los dos ids que los bootstrappers marcan a mano **sí existen como migración en el ensamblado**
  (`20260322010000_AddOperationalFinanceAndTreasury.cs`, `20260325003000_AddRefreshTokens.cs`) → comparar
  contra el ensamblado no genera falsos "resguardo más nuevo".
- El `AppDbContext` se registra con `EnableRetryOnFailure(5, 10s)` y **sin** `CommandTimeout` explícito
  (`Program.cs:173-182`) → rige el default de Npgsql (30 s) por sentencia.
- **El usuario de Postgres es el `POSTGRES_USER` de la imagen oficial `postgres:16`**
  (`docker-compose.yml:14-16`), que la imagen crea como superusuario: alcanza para `CREATE DATABASE`,
  `ALTER DATABASE ... RENAME`, `ALTER DATABASE ... ALLOW_CONNECTIONS` y `CREATE EXTENSION`. **No lo
  verifiqué con una consulta contra PROD** → el diseño lo **assertea en runtime** (§D1.5).
- **Hay extensiones**: `pgcrypto` (2 migraciones) y `pg_trgm` (`20260530120000_AddRateFuzzyMatching`).
  Viajan en el dump como `CREATE EXTENSION`.
- **Hangfire vive en la MISMA base**: `JobStorageConnection` no está definido en `docker-compose.yml`, así
  que cae a `DefaultConnection` (`Program.cs:691-698`) → el esquema y las colas de Hangfire viajan en el
  dump. El filtro `MaintenanceModeHangfireFilter` frena la **ejecución** de jobs en mantenimiento, pero el
  servidor de Hangfire del contenedor `worker` (`Hangfire__ServerEnabled: "true"`, `docker-compose.yml:232`)
  **sigue conectado y reconectando** a la base.
- Helpers ya existentes y reusables (mismo puerto): `RecreateEmptyDatabaseAsync`,
  `BuildMaintenanceConnectionString` (se conecta a `postgres`), `TerminateConnectionsToAsync`,
  `DropDatabaseIfExistsAsync` (`PgDatabaseRestorePort.cs:554-593`).
- La pantalla de mantenimiento del front ya repregunta sola cada 5 s a `GET /system/status`
  (`src/TravelWeb/src/components/MaintenanceScreen.jsx:21,41-45`).
- Presupuesto de mantenimiento: `Maintenance:MaxDurationMinutes` = 30 (`FileMaintenanceModeService.cs:64`,
  explícito en compose), con test guardián `RestoreTotalTimeoutConfigurationTests` (2 caminos hoy).

## 2. Decisiones del dueño ya firmadas (NO reabrir)

1. **Se aceptan resguardos de versiones anteriores**: si las migraciones del dump son un **subconjunto
   estricto** de las del sistema, se restaura y después se aplican solas las que faltan, dentro del modo
   mantenimiento que ya existe. Si el dump trae migraciones que el sistema NO conoce (resguardo de una
   versión más nueva), se sigue rechazando.
2. **Si la actualización falla a mitad: volver solo atrás** y dejar TODO exacto como antes, avisando en
   criollo. Fail-closed: nunca un sistema a medias.
3. **UX: aviso claro sin paso extra.** La lista marca los resguardos de versión anterior; el modal avisa
   "este resguardo es más viejo; al restaurarlo el sistema se actualiza solo"; y el cartel de "¿Seguro?"
   lleva **una línea extra** con eso mismo (firmada hoy). Confirmación igual que hoy: frase + contraseña +
   motivo ≥10 (F-16).

## 3. Decisiones técnicas

### D1 (cierra B1) — Restaurar en una base NUEVA y después intercambiar nombres

**Recomendación única**: nunca más `pg_restore` sobre la base viva.

1. `CREATE DATABASE "travel_restore_<ts>"` (mismo helper que ya crea la base sombra).
2. `pg_restore --no-owner --no-acl --single-transaction -d travel_restore_<ts> <archivo>` — el dump
   **completo**, en una base vacía: no hace falta `--clean` ni `--if-exists`, y **no puede quedar esquema
   híbrido** porque no hay nada previo que sobreviva.
3. **Si eso falla, la base viva NUNCA se tocó.** Un resguardo corrupto o incompleto pasa de "riesgo de
   dejar la base a medias" a **cero daño** — es mejor que el comportamiento actual, no solo distinto.
4. Si salió bien, **intercambio de nombres** (todo desde la base `postgres`, fuera de EF):
   1. `ALTER DATABASE "travel" WITH ALLOW_CONNECTIONS false` — sin esto, el `worker` (Hangfire, §1)
      reconecta entre el `pg_terminate_backend` y el `RENAME`, y el rename falla por "database is being
      accessed by other users".
   2. `NpgsqlConnection.ClearAllPools()` + `pg_terminate_backend` sobre la base viva.
   3. `ALTER DATABASE "travel" RENAME TO "travel_old_<ts>"`.
   4. `ALTER DATABASE "travel_restore_<ts>" RENAME TO "travel"`.
   5. `ALTER DATABASE "travel" WITH ALLOW_CONNECTIONS true` + `ClearAllPools()`.
   - Los pasos 2-4 se reintentan de forma acotada (propuesto: 5 intentos, 2 s de espera): el único motivo
     esperable de fallo es una conexión que entró en la ventana.
   - **`finally` OBLIGATORIO**: dejar `ALLOW_CONNECTIONS true` sobre la base que tenga el nombre vivo,
     pase lo que pase. Si esto se olvidara, el sistema queda **muerto para todos** aunque los datos estén
     perfectos. Es el riesgo nuevo más serio de esta obra y va con línea propia en el runbook:
     `ALTER DATABASE "travel" WITH ALLOW_CONNECTIONS true;`.
   - Si falla entre 3 y 4 (existe `travel_old_<ts>` y no existe `travel`): se renombra de vuelta
     `travel_old_<ts>` → `travel`. Si ni eso sale, es doble fallo (§D4.5).
5. **Assert previo, fail-closed**: antes de crear nada se verifica que el usuario pueda hacerlo
   (`SELECT current_setting('is_superuser')` o `rolcreatedb`). Si no puede → rechazo criollo ("este
   servidor no permite hacerlo desde acá; avisá al equipo técnico") y camino `ops-restore.yml`. Verificado
   en el compose que hoy es el usuario de la imagen oficial; **no** verificado contra PROD.
6. **Limpieza y espacio en disco**: al ARRANCAR cada restore total se dropean, de forma idempotente, las
   sobras de intentos anteriores (`travel_restore_*`, `travel_old_*`, `travel_fallido_*`). En el camino
   feliz, `travel_old_<ts>` se dropea **al final**, recién después de que migraciones + AFIP + auditoría
   salieron bien; si ese drop falla, se loguea y **no** convierte un restore exitoso en error (es basura,
   no pérdida de datos). **Decisión documentada**: no se conserva `travel_old_<ts>` "por si acaso" — el
   rescate posterior ("me equivoqué de resguardo") sigue siendo el `pre-restore-*.dump`, que ahora se
   restaura por este mismo camino seguro. Así el disco queda acotado a ~2 copias durante la operación y 1
   al terminar. El chequeo de espacio libre va al runbook (la app no ve el volumen de la base): si el
   disco se llena, el que sufre es Postgres entero.
7. **Ruido conocido de Hangfire**: como su esquema viaja en el dump, un resguardo viejo trae las colas y
   los jobs de ese momento → jobs viejos que se re-encolan y filas de "servidores" fantasma que Hangfire
   limpia solo. **No se toca** el esquema de Hangfire en esta obra (borrar sus tablas a mano sería inventar
   una limpieza no firmada); queda anotado como ruido operativo a mirar después del primer restore real.
8. **Extensiones**: `pgcrypto` y `pg_trgm` viajan en el dump y se recrean en la base nueva. Con el usuario
   actual alcanza (ambas son "trusted" desde PG13); si el assert del punto 5 falla, se rechaza antes.
9. **Orden respecto del resguardo previo (cambio deliberado vs. hoy)**: el `pre-restore-*.dump` se toma
   **después** de que el `pg_restore` a la base nueva salió bien y **antes** del intercambio. Motivo: si
   el resguardo elegido está corrupto, hoy se pagan 10-15 minutos de mantenimiento para nada. Sigue siendo
   obligatorio y sigue siendo el cinturón extra (es lo único que sobrevive a perder el volumen).

### D2 (cierra B2) — El veredicto se calcula contra el ENSAMBLADO, y la base viva tiene que estar al día

El candado de hoy compara el dump contra `__EFMigrationsHistory` **de la base viva**, pero el que aplica
las migraciones es EF con la lista del **ensamblado**. Si la base viva quedó atrás (deploy a medias), el
veredicto se calcula contra la referencia equivocada. Nuevo gate, en este orden:

1. `GetPendingMigrations()` sobre la base viva **tiene que estar vacío**. Si no: *"El sistema quedó a
   mitad de una actualización; no se puede restaurar desde acá. Avisá al equipo técnico."* →
   `ops-restore.yml`.
2. Los ids del dump tienen que estar **todos** en `Database.GetMigrations()` (lista del ensamblado). Si
   aparece uno desconocido → rechazo **"resguardo de una versión más nueva"**.
3. Lo que falta (`ensamblado − dump`) tiene que ser el **final de la fila** en el **orden de EF** (el que
   devuelve `GetMigrations()`, nunca `string.CompareOrdinal`). Si falta una del medio → rechazo
   **"historial con agujero"**, texto distinto del anterior (M1: hoy hay uno solo, que en este caso
   mentiría).
4. Historial del dump vacío → rechazo, igual que hoy.
5. Igualdad exacta → camino de hoy, sin paso de actualización.

### D3 (cierra B3 y B4) — Secuencia exacta, AFIP después de migrar y los backfills adentro

`ExecuteTotalRestoreAsync` (`SystemDataRestoreService.cs:424`) queda así:

| # | Paso | Si falla |
|---|---|---|
| 0 | Motivo ≥10 (F-16) → `TryActivate` (candado atómico) | rechazo, nada tocado |
| 1 | Gate de esquema (D2, 5 veredictos) | rechazo con el texto que corresponda |
| 2 | Candado fiscal (misma regla que "Empezar de cero") | rechazo |
| 3 | Assert de permisos (D1.5) + limpieza de sobras (D1.6) | rechazo |
| 4 | `Touch()` → base nueva + `pg_restore` del dump completo (D1.1-3) | **nada tocado**, mantenimiento OFF |
| 5 | Resguardo previo obligatorio (`pre-restore-*.dump` + MinIO) | rechazo, nada tocado |
| 6 | `Touch()` → **intercambio de nombres** (D1.4) | vuelta atrás (§D4) |
| 7 | `Touch()` → **actualizar esquema**: bootstrappers → `MigrateAsync()` → backfills ADR-021/022/025 | vuelta atrás (§D4) |
| 8 | **AFIP a homologación** (`UPDATE "AfipSettings" SET "IsProduction" = false`) | vuelta atrás (§D4) |
| 9 | Reponer archivos de MinIO | **best-effort**, ver abajo |
| 10 | Auditoría (§D7) → drop `travel_old_<ts>` → mantenimiento OFF | log, sin convertir el éxito en error |

- **B3 cerrado**: el ajuste de AFIP se movió del paso "justo después del restore" (donde corría contra el
  esquema viejo y **sin** vuelta atrás posible) al paso 8, contra el esquema ya actualizado y **dentro** del
  sobre de vuelta atrás. Si no se puede confirmar, ahora hay a dónde volver: ya no es "desenlace incierto",
  es vuelta atrás.
- **B4 cerrado**: la secuencia de actualización se extrae de `Program.cs` a una clase compartida
  **`DatabaseSchemaUpdater`** (Infrastructure) que usan los DOS caminos (arranque y restore), con
  **política de reintentos parametrizada**: arranque = 5 intentos con espera (como hoy), restore = **1
  intento**. En el camino de restore **no se traga ningún fallo**: los backfills que hoy hacen `LogError`
  y siguen, acá **hacen fallar el paso** → vuelta atrás. Motivo: en el arranque un backfill que falla se
  recupera en el próximo deploy; en un restore, dejar la plata derivada (saldos por moneda, libro de caja,
  líneas de cancelación) en cero es exactamente el dato silencioso falso que este ERP no puede mostrar.
  Los 3 backfills son idempotentes y arrancan con un `NeedsBackfillAsync` barato: en el caso normal (dump
  reciente) son tres consultas y listo.
- **Regla que se mantiene, como regla citable y NO como sustituto** (B4): *toda migración que cree una
  tabla/columna derivada lleva su propio backfill adentro* — ya es la práctica (`Adr048_M2`, `Adr022_M3`).
  Un reviewer puede bloquear citándola.
- **Desvío declarado en el paso 9**: reponer MinIO queda **fuera** del sobre de vuelta atrás. El reviewer
  pidió "todo lo que falle entre el swap y el final → rename de vuelta"; propongo excluir MinIO porque la
  reposición es aditiva, no tiene rollback propio, y tirar abajo una base entera porque no volvieron unos
  vouchers es peor que el aviso honesto que ya existe ("los archivos subidos no se pudieron recuperar").
  **Queda a criterio del reviewer aceptar o rechazar este desvío.**

### D4 (cierra M2) — Vuelta atrás por intercambio de nombres, con reintento acotado

La vuelta atrás firmada deja de ser un segundo `pg_restore` y pasa a ser un intercambio de nombres
(segundos):

1. `LogCritical` con el detalle interno (archivo, base vieja, resguardo previo, excepción).
2. `Touch()` → `ALLOW_CONNECTIONS false` en `travel` → terminate → `RENAME travel` →
   `travel_fallido_<ts>` → `RENAME travel_old_<ts>` → `travel` → `ALLOW_CONNECTIONS true`.
   `travel_fallido_<ts>` se **conserva** para diagnóstico (es la evidencia de por qué falló la
   actualización) y se dropea en la limpieza del próximo intento (D1.6).
3. **Reintento acotado antes de declarar doble fallo** (M2): hasta 3 vueltas del paso 2, con espera corta.
   El motivo esperable de fallo es, otra vez, una conexión que entró en la ventana.
4. Si sale bien: el sistema queda **exactamente** como antes del intento (mismos datos, mismo esquema,
   mismo `__EFMigrationsHistory`), se apaga el mantenimiento y se rechaza el pedido en criollo: *"No se
   pudo actualizar el sistema con ese resguardo. Quedó todo como estaba antes de intentarlo."*
5. **Doble fallo** (los 3 reintentos fallaron): `SuppressAutoExpiry`, `LogCritical`, el sistema **queda**
   en mantenimiento y el mensaje pide avisar urgente. Es el ÚNICO caso frenado, y sale por el runbook:
   `ALLOW_CONNECTIONS true` + los dos `RENAME` a mano (`docs/db-operations.md`).
6. El `pre-restore-*.dump` sigue siendo el cinturón para el caso catastrófico (se perdió el volumen, o
   alguien dropeó `travel_old_<ts>`).

### D5 (cierra M1) — Marca de versión en la lista: barata, cacheada y **nunca** apaga el botón

- **Camino autoritativo** (el único que habilita o rechaza un restore real): el gate de D2, fail-closed,
  con base sombra descartable para leer el historial del dump. No cambia el mecanismo, cambian los
  veredictos.
- **Camino informativo** (la lista): lectura barata **sin base de datos** —
  `pg_restore --data-only --table=__EFMigrationsHistory -f -` y parseo del bloque `COPY`.
  - Caché en `IMemoryCache` (ya registrado, `Program.cs:124`) con clave
    `(nombreArchivo, tamañoBytes, fechaModificaciónUtc)`: los dumps son inmutables una vez escritos, así
    que la clave se auto-invalida y no necesita TTL.
  - **Se cachea el conjunto de ids del dump, nunca el veredicto**: el veredicto se recalcula contra la
    lista del ensamblado en cada listado. Si se cacheara el veredicto, el primer deploy posterior dejaría
    toda la lista mintiendo.
  - Si el parseo falla → estado **"no se pudo determinar"**, nunca "compatible".
  - Riesgo asumido: parsear texto de `pg_restore` ya nos falló una vez (el índice lista los nombres SIN
    comillas, commit `380175d7`). Acá solo degrada un cartel; jamás habilita un restore.
- **M1 cerrado**: **ningún estado apaga el botón.** Ni siquiera "más nuevo": la lectura barata puede
  equivocarse, y apagar un botón por un dato informativo es la peor mezcla posible. P-9 se cumple igual —
  no hay botón que el motor rechace en silencio: el motor **avisa y no toca nada**. Para "posterior" el
  cartel rosa dice que **muy probablemente** se rechace y que el sistema **verifica antes de tocar nada**;
  el rechazo autoritativo es el único que frena.
- **Contrato de API** (`GET /admin/danger/backups`, `BackupFileSummaryDto`): campo nuevo
  `versionResguardo` con valores `"actual" | "anterior" | "posterior" | "desconocida"` — strings de
  contrato en castellano, misma convención que `RestoreModes` (`"prueba"/"real"/"total"`). **Sin** ids ni
  conteos de migraciones (T-5).

### D6 — Textos y pantalla (T-5, P-9) — con gate UX obligatorio

- Nada técnico al usuario: ni ids de migración, ni "esquema", ni "EF", ni nombres de base
  (`travel_old_*` / `travel_restore_*` **jamás** aparecen en una respuesta), ni conteos internos.
- Borradores para el gate UX (**no** son texto final):
  - `actual`: como hoy, sin cartel.
  - `anterior`: marca en la lista + aviso en el modal + **línea extra en el "¿Seguro?"** (firmada):
    *"Este resguardo es más viejo que el sistema de hoy: al restaurarlo, el sistema se actualiza solo. Si
    algo sale mal, vuelve solo a como está ahora."*
  - `posterior`: botón **habilitado**, cartel rosa: *"Este resguardo parece ser de una versión más nueva
    del sistema. Es muy probable que no se pueda usar: el sistema lo verifica antes de tocar nada y, si no
    sirve, te avisa sin haber cambiado nada."*
  - `desconocida`: botón habilitado + *"No pudimos determinar de qué versión es este resguardo. El sistema
    lo verifica antes de tocar nada."*
- **Dos mensajes de rechazo distintos** (M1): "versión más nueva" y "historial con agujero".
- **NOTA PARA `ux-ui-disenador`**: esto **ajusta el punto 3 de la spec UX** acordada — el hint de "botón
  apagado con motivo" para el caso `posterior` se **reemplaza** por el cartel con botón habilitado. Los
  textos finales y la marca visual los define ese agente con `docs/ux/guia-ux-gaston.md`, y lo que no esté
  cubierto se le pregunta a Gaston **antes** de tocar el front.

### D7 — Auditoría (F-16)

- Éxito (`SystemDataTotallyRestored`): se agregan `esquemaActualizado: true|false` y
  `migracionesAplicadas: <n>` (número, sin ids). Se mantienen `archivo`, `backupPrevio`, `motivo`,
  `afipForzadoAHomologacion`.
- Fallo con vuelta atrás exitosa: `SystemDataRestoreRejected` con el mismo texto criollo que ve el usuario
  + `volvioAtras: true` (hoy el motivo auditado es el `ex.Message`, `SystemDataRestoreService.cs:165-197`).
- **Doble fallo**: `LogCritical` con todo el detalle interno + intento best-effort de
  `SystemDataRestoreRejected`. Si la base quedó inalcanzable, **el log es la constancia** — misma decisión
  que ADR-051.

### D8 (cierra M3) — Timeouts derivados de la política de reintentos

- Config nueva: `Wipe:MigrateTimeoutMinutes` (propuesto 10), `Wipe:MigrateCommandTimeoutMinutes`
  (propuesto 5 — contra el default de 30 s de Npgsql; hay migraciones con SQL crudo largo),
  `Wipe:SwapRetries` (5) y `Wipe:RollbackSwapRetries` (3) con su espera.
- `Touch()` antes de **cada** paso largo (4, 6, 7 y la vuelta atrás) → la invariante sigue siendo **por
  paso**: `peorCasoDelPaso + margen ≤ Maintenance:MaxDurationMinutes`.
- **El peor caso de cada paso ahora incluye los reintentos**: `pg_restore` 15 + 5 ≤ 30 ✓; actualización de
  esquema 10 × 1 intento + 5 ≤ 30 ✓; intercambio 5 × 2 s + 5 ≤ 30 ✓; vuelta atrás 3 × (2 s +
  intercambio) + 5 ≤ 30 ✓.
- **Extender `RestoreTotalTimeoutConfigurationTests`** con los caminos "actualización de esquema"
  (timeout × intentos), "intercambio" y "vuelta atrás", derivando los números de constantes `internal`
  (nunca duplicados a mano).
- Pared total posible ≈ 15 + 15 + 10 + 1 ≈ 41 min → el pedido HTTP muere en el proxy. **No** se agrega
  cola ni job asíncrono (complejidad distribuida sin necesidad): desde que el `pg_restore` a la base nueva
  salió bien, todo usa `CancellationToken.None`, y el front ya repregunta cada 5 s a `/system/status`. El
  trabajo abierto de `ops-nginx` (timeouts del nginx del host) sigue siendo la pieza de afuera.

### D9 — Piso de antigüedad soportado: **no hace falta** (pregunta 2 del reviewer)

Con B4 cerrado, el paso de actualización corre la MISMA secuencia que un deploy limpio (bootstrappers →
migraciones → backfills), así que cualquier resguardo cuyo historial sea subconjunto-final es soportable
sin importar la antigüedad. Si alguno muy viejo igual falla, el gate lo rechaza o la vuelta atrás lo cubre
y el camino sigue siendo `ops-restore.yml`: no se gana nada declarando un piso arbitrario que después
habría que mantener a mano.

## 4. Consecuencias

**Positivas**
- Los resguardos dejan de vencer con cada deploy: la restauración desde la app vuelve a servir en una
  emergencia, que es el único momento en que se usa.
- **Un resguardo corrupto ya no puede dañar la base viva** (antes, `--clean` la tocaba antes de fallar).
- La vuelta atrás pasa de un `pg_restore` completo (minutos, con su propio riesgo) a un intercambio de
  nombres (segundos).
- Desaparece el esquema híbrido de B1 y el "restauré una base vieja y el AFIP quedó a medias" de B3.
- La plata derivada (saldos por moneda, libro de caja, líneas de cancelación) queda garantizada por B4.

**Negativas**
- Más piezas: base nueva, intercambio, `travel_old_*`, limpieza de sobras. Es más infraestructura que el
  `--clean` de una línea de hoy.
- Uso de disco: hasta ~2 copias de la base durante la operación (más el dump). Con la base actual es
  trivial; cuando crezca hay que mirar el disco antes (runbook).
- Un segundo lugar que sabe actualizar el esquema; se mitiga con `DatabaseSchemaUpdater` compartido, pero
  esa extracción toca el arranque de la app.
- La lista de resguardos gana un cálculo por archivo (barato y cacheado, pero deja de ser un
  `Directory.GetFiles` puro).

**Riesgos (declarados)**
- **`ALLOW_CONNECTIONS false` mal manejado deja el sistema muerto para todos**, con los datos intactos. Se
  mitiga con `finally` obligatorio + línea propia en el runbook + test de integración dedicado.
- Ruido de Hangfire al restaurar un dump viejo (jobs viejos re-encolados). Anotado, no resuelto.
- El assert de permisos se apoya en cómo crea el usuario la imagen oficial: si en PROD alguien lo cambió,
  el rechazo criollo es lo que salva (fail-closed).
- El parseo barato del historial es frágil por naturaleza; solo degrada un cartel.

## 5. Alternativas descartadas (2 líneas cada una)

| Alternativa | Por qué no |
|---|---|
| `pg_restore --clean` sobre la base viva (propuesta de la rev. 1) | `--clean` no dropea lo que no está en el dump: deja esquema híbrido o aborta la transacción entera, y **toca la base viva antes de saber si el dump sirve** (B1). |
| Vuelta atrás con un segundo `pg_restore` del `pre-restore-*.dump` (rev. 1) | Minutos de riesgo en el peor momento; con el intercambio de nombres la vuelta atrás son segundos y no depende de que un dump recién hecho esté sano. |
| Vuelta atrás "quirúrgica" con los `Down()` de las migraciones | Los `Down()` de este repo no están probados y varias migraciones traen backfill de datos: revertirlas puede perder datos. |
| Disparar el contenedor `migrate` desde la API | Requiere el socket de Docker en el contenedor de la app (hoy no lo tiene, verificado); darle poder de orquestar contenedores es peor riesgo que el problema. |
| `_context.Database.MigrateAsync()` directo, sin puerto | Usa el contexto del request (30 s de `CommandTimeout`) y hace **intesteable** el único camino nuevo peligroso: "la actualización falla → vuelve atrás". |
| Veredicto contra `__EFMigrationsHistory` de la base viva (rev. 1) | El que aplica las migraciones es EF con la lista del ensamblado: si la base viva quedó atrás, se compara contra la referencia equivocada (B2). |
| Backfills afuera del paso de actualización (rev. 1) | Deja los saldos por moneda / libro de caja / líneas de cancelación en cero hasta el próximo deploy: dato silencioso falso en plata (B4). |
| Apagar el botón cuando la lectura barata dice "más nuevo" (rev. 1) | La lectura barata puede equivocarse; avisar y dejar que el motor rechace sin tocar nada es más honesto y cumple P-9 igual (M1). |
| Calcular la versión con base sombra en cada listado | N `pg_restore` + N `CREATE/DROP DATABASE` por listado, y bases sombra huérfanas si se corta; el listado es informativo. |
| Cachear el **veredicto** por archivo | Después del primer deploy el veredicto cacheado miente; se cachea el historial del dump y se compara siempre contra el ensamblado. |
| Conservar `travel_old_<ts>` indefinidamente | Duplica el disco de forma permanente; el rescate posterior ya lo cubre el `pre-restore-*.dump`, que ahora se restaura por el mismo camino seguro. |
| Cola / job asíncrono para el restore total | Complejidad distribuida sin necesidad: el front ya sobrevive a que se corte el pedido y el motor ya ignora la cancelación. |
| Un paso extra de confirmación cuando el resguardo es viejo | El dueño lo decidió: aviso claro (+ línea extra en el "¿Seguro?"), sin paso extra. |

## 6. Migración de datos, rollback de la obra y runbook

- **Sin migración de base**: no hay entidades ni columnas nuevas. Cambian un DTO de respuesta (campo
  agregado, compatible), puertos nuevos y configuración.
- **Rollback de la obra**: revertir el commit deja el candado en "igualdad exacta" (comportamiento de hoy).
  No queda dato nuevo persistido huérfano. Si se revierte con una base `travel_old_*` colgada, se dropea a
  mano (no molesta a nadie).
- **`docs/db-operations.md` se actualiza con**: (a) `ALTER DATABASE "travel" WITH ALLOW_CONNECTIONS true;`
  como primer comando de rescate; (b) los dos `RENAME` para deshacer un intercambio a mano; (c) el chequeo
  de espacio libre antes de una restauración total; (d) que sigue siendo obligatorio parar
  `postgres-backup` antes (nota ya existente en el compose).
- **Camino de escape que se mantiene**: `ops-restore.yml` para resguardos más nuevos, historial con
  agujero, base viva a medio actualizar, o después de un doble fallo.

## 7. Estrategia de tests

**Unit** (fakes de puertos, extendiendo `src/TravelApi.Tests/Unit/SystemDataRestoreServiceTotalModeTests.cs`):
1. Dump subconjunto-final → restaura en base nueva, intercambia, actualiza esquema una vez, ajusta AFIP,
   audita `esquemaActualizado: true`, dropea la vieja y apaga el mantenimiento.
2. Dump con un id desconocido → rechazo **"versión más nueva"**, antes de crear la base nueva y antes del
   resguardo previo.
3. Dump con agujero en el historial → rechazo **"historial con agujero"** (texto distinto del anterior).
4. Base viva con migraciones pendientes → rechazo, sin tocar nada (B2).
5. Historial idéntico → **no** llama al paso de actualización (camino de hoy intacto).
6. Falla el `pg_restore` a la base nueva → **nunca** se llama al intercambio ni al resguardo previo;
   mantenimiento OFF; mensaje criollo sin internals (B1: cero daño).
7. Falla la actualización de esquema → vuelta atrás por intercambio inverso, mantenimiento OFF, rechazo
   con `volvioAtras: true`.
8. Falla un **backfill** (no la migración) → también vuelve atrás (B4: en restore no se traga).
9. Falla el ajuste de AFIP → vuelve atrás (B3: ya no es "desenlace incierto").
10. La vuelta atrás falla una vez y anda a la segunda → termina en rechazo limpio, **no** en doble fallo (M2).
11. La vuelta atrás falla las 3 veces → **nunca** apaga el mantenimiento, llama `SuppressAutoExpiry`.
12. Pedido HTTP cancelado después del intercambio → no aborta (`CancellationToken.None`).
13. Sin permisos para crear bases → rechazo criollo, nada tocado (D1.5).
14. T-5: ningún mensaje devuelto contiene ids de migración, nombres de tabla ni nombres de base
    (`travel_old_*`, `travel_restore_*`, `travel_fallido_*`).
15. Guardián de timeouts extendido con actualización / intercambio / vuelta atrás, reintentos incluidos (M3).

**Integración** (`PostgresIntegrationFixture`, extendiendo `SystemDataRestoreServiceIntegrationTests.cs`):
16. Ciclo feliz con Postgres real: base nueva + intercambio + la app sigue leyendo con la MISMA connection
    string (el nombre vivo no cambió) + `ALLOW_CONNECTIONS` quedó en `true`.
17. **Invariante crítica**: ante fallo en cualquier punto del intercambio, `ALLOW_CONNECTIONS` de la base
    con el nombre vivo termina SIEMPRE en `true` (el riesgo "sistema muerto" de §4).
18. Intercambio con una conexión abierta que reconecta (simula el `worker`): los reintentos lo resuelven.
19. Los veredictos del gate autoritativo con dumps armados a mano (subconjunto / id desconocido / agujero /
    vacío) + base viva con pendientes.
20. Lectura barata (D5) contra un dump real: mismo conjunto de ids que el camino autoritativo; archivo
    ilegible → "desconocida", nunca "actual".
21. Caché: dos listados seguidos no releen el archivo; cambiar tamaño/fecha sí.
22. `DatabaseSchemaUpdater` con política "arranque" (5 intentos, tolera) vs "restore" (1 intento, no
    tolera): mismo código, dos comportamientos verificados.

**Verificación manual obligatoria antes de dar la obra por hecha** (no la reemplaza ningún test):
restaurar en PROD, desde la pantalla, el resguardo real `wipe-20260727-223313.dump` — hoy exactamente el
caso "subconjunto estricto" que provocó este ADR — y comprobar que el sistema queda usable, que la lista
lo marcaba como de versión anterior, y que los datos derivados de plata NO quedaron en cero. **El doble
fallo no se puede verificar en PROD**: queda cubierto solo por tests (11) y así hay que reportarlo.

## 7.bis Condiciones aceptadas de la re-review (anexo, 2026-07-29)

- **C1**: la vuelta atrás del intercambio es **reconciliación por estado** contra `pg_database` e **idempotente**
  (si la base original ya tiene el nombre vivo, no toca nada), nunca una secuencia ciega de pasos.
- **C1 (bis)**: el assert temprano incluye **propiedad de la base** (`pg_get_userbyid(datdba) = current_user`),
  no solo `rolcreatedb`, y corre **antes** de pagar el `pg_restore` y el resguardo previo.
- **C2**: si la reposición de MinIO falla o es parcial, la auditoría lleva `archivosRepuestos: false` como **dato**
  (no solo dentro del mensaje), y `docs/db-operations.md` explica cómo re-correrla sin restaurar de nuevo.
- **Extra del reviewer**: hay test de integración que demuestra que el `finally` de `ALLOW_CONNECTIONS true` cubre
  el **camino de fallo** — el único que dejaría el sistema muerto con los datos sanos.

### Deuda anotada (obra futura, NO parte de esta)

- **Purgar las colas de Hangfire después de una restauración total.** El esquema y las colas de Hangfire viajan en el
  dump (§1), así que un resguardo viejo trae los jobs pendientes de ese momento y el `worker` los ejecuta al salir de
  mantenimiento. Mitigación de HOY (operativa, en el runbook): reiniciar el `worker` y mirar sus logs después de una
  restauración total. La obra futura es decidir con el dueño QUÉ colas se pueden purgar (borrarlas a mano sería
  inventar una limpieza no firmada) y hacerlo dentro del mismo modo mantenimiento.

## 8. Auto-crítica

- **Verificado en el repo** (archivo y línea en §1): el `SetEquals`, los flags del `pg_restore` de hoy, la
  ausencia de socket de Docker, la secuencia de `--migrate-only` con sus 3 bootstrappers y 3 backfills que
  hoy **se tragan** los fallos, que los ids de los bootstrappers existen como migración en el ensamblado,
  `CommandTimeout` no seteado, Hangfire en la misma base + worker con servidor activo, los helpers de
  crear/dropear/terminar del puerto, el polling de 5 s del front y el presupuesto de 30 min.
- **NO verificado (asumido)**: (a) que el usuario de Postgres sea superusuario en PROD — mitigado con el
  assert fail-closed de D1.5; (b) que `pg_restore --data-only --table=__EFMigrationsHistory -f -` imprima
  un `COPY` parseable en los dumps reales — **hay que probarlo con un dump de PROD antes de construir D5**;
  si no sirve, el plan B es calcular la marca al elegir el archivo en vez de en la lista, y eso vuelve al
  gate UX; (c) el comportamiento exacto de Hangfire al recibir colas viejas (se declara ruido, no se
  modela).
- **Lo que un reviewer podría rechazar con razón**: (1) el desvío del paso 9 (MinIO fuera del sobre de
  vuelta atrás), marcado como decisión a aceptar o rechazar; (2) extraer `DatabaseSchemaUpdater` de
  `Program.cs` toca el arranque, que hoy funciona — se puede pedir que vaya en commit propio y verificado
  aparte, antes de tocar el restore; (3) `ALLOW_CONNECTIONS false` es un mecanismo nuevo en este repo y
  agrega un modo de falla ("sistema muerto con datos sanos") que antes no existía: la alternativa (solo
  `pg_terminate_backend` + reintentos) es más suave pero puede no cerrar nunca la ventana con el `worker`
  reconectando; si el reviewer la prefiere, se cambia D1.4.1 por reintentos más agresivos y se documenta
  que el intercambio va a fallar más seguido, terminando en rechazo limpio (nada tocado).
- **Sin cubrir a propósito**: el ruido de Hangfire, el chequeo de espacio en disco (runbook: la app no ve
  el volumen) y el nginx del host (fuera de este repo).
