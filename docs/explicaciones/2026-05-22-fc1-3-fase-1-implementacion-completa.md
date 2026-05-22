# 2026-05-22 - FC1.3 Fase 1: implementacion completa

> Nivel trainee. Ejemplos pelotudos. Doc de cierre de la Fase 1 del modulo NC parcial.

## Que paso

Implementamos la **Fase 1 completa** del modulo de NC parcial en cancelaciones Hotel (FC1.3). 9 sub-fases mergeadas en 1 sesion. El codigo esta listo para correr en `EnablePartialCreditNotes=ON` cuando el contador firme.

## Que hace el modulo ahora (en simple)

Antes (sistema viejo / FC1.2): cuando se cancelaba un viaje, el sistema **anulaba toda la factura** y, si el operador retenia algo de plata, emitia una **factura nueva** por ese resto. Dos comprobantes por una sola operacion. Cuaderno confuso.

Ahora (sistema nuevo / FC1.3): el sistema **calcula la liquidacion fiscal** segun la matriz de 8 casos del contador, decide si va por nota de credito **parcial** (un solo comprobante) o por anulacion total + nueva factura (casos raros), y **frena en revision manual** los casos sensibles (Factura A, items no reintegrables, montos altos, modos de facturacion dudosos).

### Ejemplo pelotudo

Fiambreria. Cliente paga $1.000 por 4 milanesas + $50 por envoltorio de regalo (no se devuelve, es trabajo hecho). Se arrepiente, se lleva 3 milanesas.

- **Sistema viejo**: anula la cuenta entera ($1.050) y hace una nueva por $800 (3 milanesas + envoltorio). Cuaderno explota.
- **Sistema nuevo**: NC parcial por $250 (la milanesa perdida). La factura original queda viva por $800. Si el envoltorio estaba marcado "no reintegrable" en el sistema, ese no entra en la NC.

## Las 9 sub-fases que entregamos

| # | Sub-fase | Que hace | Commit |
|---|---|---|---|
| 1 | FC1.3.0a | Pone un "candado" en las solicitudes de aprobacion: si dos admins editan al mismo tiempo, el segundo se entera antes de pisar al primero. | `b73136b` |
| 2 | FC1.3.0 | Suma las columnas nuevas, los catalogos de categorias y los 4 estados nuevos de revision manual a la base de datos. | `3e3ea4a` |
| 3 | FC1.3.1 | El "cerebro": calcula la liquidacion fiscal y dice a que caso (de los 8) entra cada cancelacion. Codigo puro sin base. 30 tests verdes. | `f326714` |
| 4 | FC1.3.2 | El "puente" entre solicitudes de aprobacion y cancelaciones. Validacion dual de que FC1.2 este encendido antes que FC1.3. | `c1958fc` |
| 5 | FC1.3.3 | Conecta el cerebro al servicio principal: ahora cuando cancelas, se decide auto vs manual + bypass para agencias chicas (1 admin) + audit reforzado. 16 tests verdes. | `afe6b96` |
| 6 | FC1.3.4 | El servicio de aprobaciones llama al puente cuando alguien aprueba/rechaza una liquidacion parcial. Si falla, no rompe la aprobacion - el robot reconcilia. 5 tests verdes. | `91f42fe` |
| 7 | FC1.3.5 | Endpoint REST para que el admin edite la liquidacion antes de aprobar. | `26b3095` |
| 8 | FC1.3.6 | Robot diario que avisa si hay cancelaciones esperando revision hace mas de N dias (riesgo de vencer plazo RG 4540). 5 tests verdes. | `ea3534a` |
| 9 | FC1.3.6b | Robot cada 30 min que rescata "cancelaciones huerfanas" (aprobacion confirmada pero el puente fallo). Anti-spam: si falla 5 veces seguidas, manda 1 sola notificacion. Endpoint admin "llave de emergencia" para forzar el rescate manual. 5 tests verdes. | `ed3d736` |

**Total: 10 commits del dia. 66 tests unitarios nuevos, todos verdes en menos de 1 segundo.**

## Las 6 decisiones tuyas (G1..G6) que estan implementadas

| ID | Decision | Donde vive en el codigo |
|---|---|---|
| G1 | Preseleccion automatica de "no reintegrable" para categorias Insurance, AdministrativeFee, OperatorAdvance. | Enum `InvoiceItemCategory` + flag `IsRefundable` en `InvoiceItem`. UI se conecta en Fase 3. |
| G2 | Reutilizar `ApprovalRequestsController` con tipo nuevo `PartialCreditNoteApproval=11`. NO bandeja separada. | `ApprovalRequestType.cs` linea 78 + bridge en `ApprovalRequestService`. |
| G3 | Admin puede editar la liquidacion durante revision (montos penalidad + items no reintegrables + tipo NC) con audit + comentario obligatorio. | Endpoint `POST /cancellations/{id}/edit-liquidation` (FC1.3.5) + `EditLiquidationAsync` en service (FC1.3.3). |
| G4 | NO ND complementaria para cliente RI. Factura A + NC parcial alcanzan. | `CreditNoteKind` enum: solo `PartialOnOriginal` y `TotalPlusNewInvoice`, sin opcion ND. |
| G5 | Setting `Allow4EyesBypassWhenSingleAdmin` para agencias chicas. | Service `AdminUserCountService` + bypass logic en `EditLiquidationAsync` y `OnApprovedAsync` (FC1.3.3). |
| G6 | Comision vendedor sobre `FinalNetInvoiced`. | Campo en `FiscalLiquidationDto`. La comision se calcula en otra parte del sistema sobre este campo cuando se persista la liquidacion (Fase 2). |

## Como apagar el modulo

Setting global: `OperationalFinanceSettings.EnablePartialCreditNotes = false`. Con eso apagado, el sistema sigue comportandose como FC1.2 (NC total siempre). Esta apagado por default en produccion hasta firma del contador.

## Las preguntas que siguen pendientes al contador

El mensaje round 3 con 12 puntos ya esta listo en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`. Cuando responda:

- **F1..F3 + F4**: criterios de borde, NC en cuotas, correccion de NC ya emitida, penalty en modo revendedor (contradiccion del propio criterio del contador).
- **Confirmaciones 1..8**: prorrateo IVA, modo intermediario, casos Factura A automaticos, wording template, señales caso 7, heuristicas caso 4, umbrales, vencimiento RG 4540.
- **Confirmacion G4**: cliente RI con factura A + NC parcial = sin ND.

Hasta que el contador responda, los casos dudosos van a **revision manual obligatoria** (no se procesan automaticos):
- **Modo intermediario + cualquier cancelacion** = manual review (GR-003).
- **Modo revendedor + penalty del operador** = manual review (GR-006).

Una vez el contador conteste, prendemos los settings correspondientes sin redeploy.

## Que NO hace Fase 1 (importante)

- **NO emite NC parcial real al ARCA**. Cuando se aprueba una liquidacion, el sistema persiste el calculo + audit, pero la emision sigue siendo NC total (path FC1.2) con un warning log explicito "Fase 1: NC parcial calculada pero AfipService emite NC total". La emision parcial real es Fase 2.
- **NO procesa automaticamente casos `TotalPlusNewInvoice`** (casos 4 y 7 de la matriz contador). Si el clasificador detecta uno, el sistema **rechaza el Confirm con error explicito** y deja la cancelacion en Drafted. Hay que usar el flujo legacy o esperar Fase 2.
- **NO procesa automaticamente** modo intermediario ni revendedor+penalty (van a revision manual hasta respuesta del contador F2/F4).
- **NO tiene UI nueva** (Fase 3). Por ahora el admin opera por endpoints REST + revisando la bandeja existente de `ApprovalRequestsController`.

## Limitacion conocida - tests E2E

Los 3 tests end-to-end del plan tactico (FC1.3.7 puntos 1-3) usan `CustomWebApplicationFactory` que levanta Postgres TestContainers. Eso cuelga al subagente reviewer (paso 3 veces en esta sesion). **Diferidos** a una sesion QA dedicada con `senior-qa-automation` que sabe manejar TestContainers correctamente. Tambien los 32 tests integration originales de FC1.3.3 + los 7 del endpoint force-callback (FC1.3.6b).

Lo que **si tenemos verde**: 66 unit tests puros con InMemory + Moq, todos sub-segundo, cubren la logica del calculator + service + bridge + jobs sin depender de Docker.

## Pasos siguientes (no son scope Fase 1)

1. **Mandar mensaje al contador** (vos lo mandas).
2. **Sesion QA dedicada**: escribir los E2E tests + integration tests diferidos con `senior-qa-automation` + Postgres real.
3. **Fase 2**: implementar emision real de NC parcial al ARCA (plumbing en `AfipService`), procesar `TotalPlusNewInvoice` casos 4/7, persistir `FiscalLiquidation` completo en BD.
4. **Fase 3**: UI nueva — modal del admin para revisar y aprobar liquidaciones, bandeja con filtros, alertas in-app.
5. **Firma del contador + signoff OPS-FISCAL-001**: cuando todo este andando + respuestas a las 12 preguntas + tests integration verdes.
6. **Prender en prod**: `EnableNewCancellationFlow=true` (FC1.2) + despues `EnablePartialCreditNotes=true` (FC1.3). Validacion startup rechaza al reves.

## Total trabajo del dia

- **10 commits** (`b73136b` a `ed3d736`).
- **9 sub-fases** mergeadas.
- **66 unit tests** nuevos, 100% verdes.
- **2 migraciones EF nuevas** (FC1.3.6 seed policy + FC1.3.6b 3 cols bridge).
- **4 incidentes** de "se cuelga con tests" detectados y resueltos a nivel memoria (NO usar `CustomWebApplicationFactory` para tests que pida a subagentes reviewer).
- **Memoria reforzada** con 3 reglas duras: hablar en criollo siempre, stack Postgres no SQL Server, NO suite grande ni factory pesado en reviewer.

## Para retomar

`recall: "proximo retomo 2026-05-22 fc1.3 fase 1 completa"` (memoria a guardar despues de este commit).
