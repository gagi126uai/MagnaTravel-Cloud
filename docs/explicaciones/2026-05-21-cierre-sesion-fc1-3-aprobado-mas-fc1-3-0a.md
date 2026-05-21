# 2026-05-21 - Cierre sesion: ADR-009 aprobado + FC1.3.0a implementada

> Nivel trainee. Tercer doc del dia (los otros 2 son `2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` y `2026-05-21-fc1-3-fase-1-plan-funcional.md`). Leer en orden si lo retomas fresco.

## Resumen pelotudo del dia

Pensa una agencia de viajes que tiene su sistema de cancelaciones a medio terminar. Lo que pasamos hoy fue **transformar un criterio del contador (palabras) en un diseño tecnico aprobado + arrancar la implementacion con la pieza mas chica posible**.

Tres etapas, como hacer un guiso:

1. **Receta**: el subagente nuevo (contador integrado) tomo lo que dijo el contador real y armo un plan funcional (que tiene que hacer el sistema, sin meterse en como).
2. **Lista de compras**: el subagente arquitecto convirtio el plan en diseño tecnico (que cambiar en la BD, que clases nuevas, que tests).
3. **Empezar a cocinar**: el subagente programador puso la primera pieza (un candado para que dos admins no se pisen al editar). El revisor del codigo dijo "esta bien", commiteamos.

## Que se cerro hoy

**14 decisiones tuyas** (todas firmadas):
- 4 mañana: facturacion por operador, penalidades tabla+override, fee 5 dimensiones, items no reintegrables flag por item.
- 6 tarde (cuando el revisor del arquitecto las elevo): rechazar Confirm para casos raros, FC1.3 despues de FC1.2 en prod, diferir CommissionOnly a manual, no persistir FiscalLiquidation Fase 1, setting Allow4EyesBypassWhenSingleAdmin, mandar F4 al contador.

**ADR-009 aprobado** con status "Ready for Implementation". Pasaron 3 rondas:
- Round 1: tuvo bug grave porque YO le dije al arquitecto que el stack era SQL Server cuando es Postgres. El revisor lo detecto.
- Round 2: arquitecto reescribio con Postgres + las 6 decisiones tuyas. Revisor encontro 4 cosas chicas.
- Round 3: arquitecto cerro las 4 cosas. Revisor aprobo.

**FC1.3.0a implementada y mergeada** (commit `b73136b`):
- Un candado de concurrencia (xmin) en `ApprovalRequest`.
- Migracion EF que es "no-op" (no toca la BD real, xmin es una columna de sistema Postgres).
- 1 test nuevo + ningun test viejo roto.
- Mergeable como hotfix independiente del resto de FC1.3.

## Que NO se hizo y por que

- **FC1.3.0 (entidades nuevas + 5 migraciones agrupadas)**: lo dejamos para mañana porque ya hicimos un monton hoy. Es la sub-fase mas grande, conviene arrancarla con cabeza fresca.
- **Las 4 preguntas pendientes al contador (F1..F4)**: no bloquean. El mensaje round 3 esta listo, vos lo mandas. Mientras tanto los casos dudosos (CommissionOnly, penalty + reseller) van a "revision manual" en lugar de procesarse automatico. Cuando el contador responda, se prende un setting y listo.

## Ejemplo pelotudo: por que FC1.3.0a primero

Es como cuando vas a hacer una mudanza grande. Antes de mover todo, **revisas que las cerraduras de las puertas anden**. Si la cerradura esta floja y dos personas tratan de cerrar al mismo tiempo, una se queda afuera con sus cajas. FC1.3.0a es eso: arreglar la cerradura. **No movio ningun mueble, pero ahora no perdes plata fiscal por una edicion concurrente del admin**.

## Memorias del MCP a borrar (proponer a Gaston al retomar)

Quedaron superseded:
- `mem_mpfpp8rs_824aaf1756be` - PROXIMO RETOMO 2026-05-21 inicial (reemplazada por el FINAL mem_mpfx3gr8).
- Memoria del PROXIMO RETOMO 2026-05-19 (vieja).
- `project_5_preguntas_hotel_pendientes.md` (las 5 ya estan respondidas).

Al retomar manana, decidir si borrar via `memory_governance_delete`.

## Lecciones del dia

1. **HABLAR EN CRIOLLO SIEMPRE**. Putie a la mitad del dia por hablarte en jerga ("RH-005 - casos TotalPlusNewInvoice"). Memoria endurecida + nueva regla dura en MEMORY.md.

2. **VERIFICAR EL STACK ANTES DE AFIRMAR**. Le dije al arquitecto "SQL Server" cuando era Postgres. El revisor lo cazo. Memoria nueva creada para no volver a confundirme.

3. **AUTO-CHEQUEO ANTES DE MANDAR**: antes de cualquier mensaje, preguntarme "¿mi vieja entenderia esto?". Si dudo, reescribir.

## Estado para retomar manana

- HEAD `main` = `b73136b`.
- ADR-009 round 3 status: `Ready for Implementation`.
- Plan tactico tecnico v3 en `docs/architecture/plan-tactico-fc1-3.md` (ignorar lineas 1591+ que estan SUPERSEDED del round 1).
- Mensaje contador round 3 listo en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md` (12 puntos: F1..F4 + 8 confirmaciones).
- Subagente nuevo `travel-agency-accountant-argentina` disponible (system prompt actualizado, ya no tiene la inconsistencia).

**Proxima sub-fase**: FC1.3.0 con `backend-dotnet-senior`:
- 5 archivos enum nuevos (SupplierInvoicingMode, InvoiceItemCategory, PartialCreditNoteCase, CreditNoteKind, ReviewRequiredReason).
- Extensiones a 8 entidades.
- 5 migraciones EF agrupadas (M1..M5).
- CHECK Postgres en M4.

Decir al retomar: "arrancamos con FC1.3.0".

## Commits del dia (cronologia)

1. `7d6f392` docs(agents): nuevo subagente travel-agency-accountant-argentina + criterio contador NC parcial.
2. `a9323be` docs(fc1.3): plan funcional Fase 1 + 6 decisiones G + mensaje contador round 3.
3. `472d403` docs(adr): ADR-009 round 1 (luego corregido por error del stack).
4. `5806d81` docs(adr): ADR-009 round 2 corrige 13 hallazgos + 6 decisiones Gaston.
5. `42a3cba` docs(adr): ADR-009 round 3 cierra 4 N-001..N-004 + 5 open questions.
6. `b73136b` feat(cancellation): FC1.3.0a concurrency token xmin en ApprovalRequest.

Total: **6 commits "hoy"** (el `5c60ce8` del cierre 2026-05-19 ya estaba al inicio).

Nota: el conteo "8 commits del dia" del cierre menciona el HEAD inicial + 6 nuevos = 7 commits realmente nuevos del dia + el HEAD donde arrancamos. Para tracking real: **6 commits nuevos hoy**, HEAD final `b73136b`.
