# 2026-05-26 - FC1.3 Fase 2: plan tactico FIRMADO + sub-fase F2.0 implementada

> Nivel trainee. Ejemplos pelotudos. Doc de cierre de la sesion donde el plan paso 4 rondas de revision + arrancamos a programar Fase 2.

## Lo grande del dia

Dos cosas:

1. **El plan tactico de la Fase 2 quedo firmado** (v5, despues de 4 rondas con un revisor critico).
2. **La primera sub-fase de la Fase 2 (F2.0) quedo programada + revisada + arreglada + re-revisada = Aprobada**.

En criollo: **arrancamos a construir el segundo piso**.

## Ejemplo pelotudo de la Fase 2 entera

Acordate del [doc de cierre Fase 1](./2026-05-22-fc1-3-fase-1-implementacion-completa.md): la fiambreria que ahora calcula bien cuando devolver plata por una milanesa cancelada. Pero todavia **no le pega el ticket nuevo a la maquina fiscal del gobierno**.

La Fase 2 es eso: hacer que cuando se confirma una NC parcial, el sistema **realmente le mande al ARCA** la nota de credito por el monto exacto, en la moneda correcta, sin emitir el mismo comprobante dos veces aunque se caiga el servidor a la mitad del proceso.

## El plan tactico — 4 rondas de revisor

### Por que tantas rondas

El plan tactico es un "manual de obra". Antes de comprar materiales (= antes de programar), un revisor critico lo lee buscando errores. Si encuentra cosas mal, hay que arreglarlas. Despues lo lee de nuevo. Asi 4 veces.

| Ronda | Que encontro el revisor | Status final |
|---|---|---|
| Round 1 (sesion previa) | 6 bloqueantes + 4 majors + 5 minors. | Changes Required |
| Round 2 | 2 bloqueantes nuevos + 2 majors + 3 minors. | Changes Required |
| Round 3 | 1 bloqueante nuevo + 2 minors. | Changes Required |
| Round 4 | 1 major + 1 minor (no bloqueantes). | **Approved with Minors** |
| Post-round-4 (esta sesion) | Aplicamos los 3 minors directos en el doc. | **v5 FIRMADO** |

**Total**: 10 bloqueantes cerrados a lo largo de 4 rondas. Ninguno llego a codigo. **Eso ahorra dias de programar y tirar a la basura**.

### Las 2 cosas mas importantes que se arreglaron en el camino

**1. La consulta SQL para medir volumen contaba mal**
- El plan tiene una consulta para medir "que porcentaje de cancelaciones son del tipo raro (casos 4 y 7)". Si es mas del 5%, hay que invertir en programar una sub-fase opcional.
- La consulta original sumaba dos filtros sueltos, pero el sistema en realidad clasifica con prioridad: si hay Factura A, gana sobre cualquier otra cosa. **Sobrecontaba**.
- Fix: cambiar a usar el campo persistido `CreditNoteKind` directo.

**2. La idempotencia tenia un agujero entre "guardar la llave" y "mandar al ARCA"**
- Para no emitir el mismo comprobante dos veces, antes de enviar guardamos una "llave" en la base. Si despues el servidor se cae y reintenta, la llave evita doble envio.
- Pero hay un agujero: que pasa si se cae **entre** guardar la llave y mandar al ARCA? La llave dice "ya se mando" pero **no se mando nada**.
- Fix elegido (vos elegiste Camino A): hacer una funcion nueva que le pregunta al ARCA "che, viste algo de esto?". Si dice si -> derivar el codigo. Si dice no -> borrar llave + reintentar limpio. Necesito agregar una columna a la tabla para guardar el numero ARCA "antes de" mandar, asi al volver podemos comparar.

## F2.0 — la primera sub-fase programada

### Que hizo F2.0 (en simple)

**Ejemplo pelotudo**: imaginate que vas a abrir un negocio que vende cervezas, vino y fernet. Antes de **abrir** (= antes de empezar a vender), pones en el cuaderno:
- "Vendo cerveza: SI / NO"
- "Vendo vino: SI / NO"
- "Vendo fernet: SI / NO"

Y tambien una regla: **no podes vender fernet si no vendes cerveza** (porque van juntos).

Eso es lo que hace F2.0: **agregar los interruptores** de la Fase 2 al panel de control, **dejarlos apagados por default**, y poner las reglas que impiden encender el de "emitir NC parcial real" si la Fase 1 esta apagada.

### Los 5 interruptores nuevos

| Interruptor | Default | Que hace |
|---|---|---|
| `EnablePartialCreditNoteRealEmission` | OFF | Master de la Fase 2. Si apagado, sistema sigue como FC1.2 (NC total). |
| `EnableTotalPlusNewInvoiceAutoProcessing` | OFF | Procesar casos raros (4 y 7) automatico. Bloqueado por criterio cuantitativo. |
| `IvaProrrateoMode` | ProportionalToNet | Como prorratear IVA en NC parcial. Configurable post-respuesta contador. |
| `PartialCreditNoteRoundingTolerance` | 0.01 | Cuanto puede diferir la suma de componentes vs el total. |
| `IdempotencyKeyStaleThresholdMinutes` | 10 | Cuantos minutos esperar antes de declarar una llave "huerfana". |

### El semaforo nuevo G-F2-C

Tambien sumamos una bandera nueva: si la factura origen tiene **tributos provinciales** (IIBB Capital, percepciones de provincia), **se va a revision manual obligatoria**. Razon: el prorrateo de tributos provinciales NO esta modelado, hay que decidir caso a caso.

### Un bug que casi se nos escapa (importante para futuro)

**El revisor backend lo agarro a tiempo + el contador fiscal lo confirmo en paralelo**.

**Ejemplo pelotudo**: armaste una alarma que suena cuando entra alguien al jardin. La probaste con un robot que vos pusiste en el jardin: anda. Pero **nunca le dijiste a la alarma que mire el jardin** — solo mira la sala. Entran al jardin y la alarma no suena, pero los tests pasan porque vos siempre la probas con el robot **adentro de la sala**.

Eso es exactamente lo que pasaba con la bandera `HasProvincialTributes`. El test la probaba pasandole una lista de tributos a mano (= robot adentro de la sala). Pero el **caller real** del calculador (`BookingCancellationService.ConfirmAsync`) cargaba la factura **sin cargar la coleccion de tributos**, asi que `Tributes.Any()` daba false aunque la base tuviera 5 IIBB.

**Fix**: agregar `.ThenInclude(i => i.Tributes)` en los 2 sites del caller. 2 lineas. + comentario didactico que explica el bug fantasma para que nadie lo rompa de nuevo cuando agregue caller nuevo.

### Otras 3 cosas que arreglamos en el camino

- **DTO nullable + update condicional**: si un cliente legacy del API mandaba un PUT sin los 3 settings nuevos, los pisaba con defaults (silencioso). Fix: los 3 ahora son nullable, solo se actualizan si vienen con valor.
- **Log structured**: cuando el flag G-F2-C dispara, ahora deja log "tributos provinciales detectados, InvoiceId=X, TributesCount=Y" para debugging en prod.
- **Comentarios DTO**: clarifica que los 3 nuevos son "patch-like via PUT" (omitir = no modificar).

## Que entregamos al final

**Archivos creados (3)**:
- `IvaProrrateoMode.cs` enum.
- Migracion EF `Fase2_M0_AddFc13Phase2Settings` (+ designer).

**Archivos modificados (7)**:
- `OperationalFinanceSettings.cs`: 5 settings nuevos.
- `ReviewRequiredReason.cs`: flag `HasProvincialTributes = 1 << 11`.
- `OperationalFinanceSettingsDto.cs`: 3 nuevos campos nullable.
- `OperationalFinanceSettingsService.cs`: UpdateAsync + Map + 2 cross-field rules.
- `BookingCancellationService.cs`: `.ThenInclude(Tributes)` en 2 sites.
- `FiscalLiquidationCalculator.cs`: deteccion + log info.
- `Program.cs`: 2 startup checks adicionales.
- `AppDbContextModelSnapshot.cs`: auto-update EF.
- `FiscalLiquidationCalculatorTests.cs`: 2 tests unit nuevos.

**Numeros**:
- 32/32 tests del calculator verdes (30 de Fase 1 + 2 nuevos F2.0).
- 3/3 tests del Settings verdes.
- Build limpio sin warnings nuevos.
- 1 migracion aditiva sin DROP.

## Lo que NO hicimos hoy (queda pendiente)

- **F2.1**: la siguiente sub-fase (persistir `FiscalLiquidation` completo + backfill).
- **F2.2..F2.7**: las otras 6 sub-fases del plan.
- **Tests integration del VPS** (los del script `run-tests-fc13.sh`).
- **Mandar mensaje al contador round 3** (tarea de Gaston).
- **Plugin Superpowers**: explorado pero descartado.

## Lo importante para la proxima sesion

1. **F2.1 hereda B-007** (nota documental en `MapToDtoAsync` sobre Include Tributes para futura UI).
2. **F2.1 hereda B-005** (recordatorio sobre default `'ARS'` en la migracion M1).
3. **Antes de prender `EnablePartialCreditNoteRealEmission = true` en prod**:
   - Respuestas F1+F5 contador del round 3.
   - QA contra ARCA homologacion.
   - 5 confirmaciones adicionales que listo el revisor fiscal.

## Conteo de la sesion

- 4 rondas de revisor sobre el plan tactico Fase 2.
- 1 round de fixes finales directos (v5).
- 1 sub-fase F2.0 implementada.
- 2 reviews paralelos (backend + fiscal) + 1 re-review post-fixes.
- 4 bugs detectados pre-commit + resueltos.
- 14 tasks completadas.
- 0 commits hasta que Gaston de el OK.

## Para retomar

`recall: "proximo retomo 2026-05-26 fc1.3 fase 2 plan v5 firmado f2.0 listo"` (memoria a guardar despues de este commit).
