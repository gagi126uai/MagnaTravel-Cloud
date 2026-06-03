# ADR-015 — Cancelación multi-operador y cancelación parcial (rediseño del flujo de cancelación)

- **Status**: ⏸️ **EN PAUSA (2026-06-02)** — Solo la **Fase 1 (inferencia de operador + bloqueo multi-operador, commit `464339c`) fue construida y se conserva** (es motor fiscal necesario). Las **Fases 2 (multi-operador) y 3 (cancelación parcial) quedan EN PAUSA**: el dueño redefinió el rumbo del flujo de cancelación hacia "experiencia simple + IA" (ver `docs/producto/vision-copiloto-ia-2026-06-02.md` y `vision-cancelacion-2026-06-02.md`). El MOTOR fiscal de este ADR sigue siendo válido como referencia, pero la CAPA DE EXPERIENCIA (modales/bandejas/decisiones fiscales al vendedor) NO se construye con este enfoque. Multi-operador y cancelación parcial se resolverán DENTRO del copiloto IA (cancelación = caso de uso del copiloto), cuando se necesiten. NO construir Fases 2/3 como están escritas acá sin revalidar contra la visión nueva.
- **Status original**: Proposed (Round 1, plan por fases — NO implementado).
- **Date**: 2026-06-02.
- **Author(s)**: software-architect agent.
- **Driver de negocio**: Gaston (dueño) — "arreglar todos estos bugs". Respuestas de negocio: multi-operador es **frecuente** (no marginal); necesita **cancelación parcial** (a veces solo un servicio); tracking del refund del operador "mezcla / no seguro" (sin definir).
- **Supersedes (parcial)**: el supuesto de [ADR-002](ADR-002-cancellation-refund.md) de "1 BC = 1 operador = 1 reserva" deja de ser universal a partir de Fase 2 de este ADR. ADR-002 sigue vigente para el camino mono-operador / cancelación total.
- **Related**:
  - [ADR-002](ADR-002-cancellation-refund.md) — modelo BC, máquina de estados, FiscalSnapshot, INV-081/126.
  - [ADR-009](ADR-009-partial-credit-note.md) — NC parcial fiscal (`EnablePartialCreditNotes`, `IFiscalLiquidationCalculator`). **Reusable, ver §5.**
  - [ADR-010](ADR-010-partial-credit-note-receipt-reconciliation-inbox.md) — bandeja reconciliación NC parcial.
  - [ADR-013](ADR-013-debit-note-on-cancellation-penalty.md) / [ADR-014](ADR-014-deferred-penalty-confirmation-debit-note.md) — ND por penalidad propia (1 penalidad por BC hoy).

> **Aviso de alcance.** Este ADR es un **plan de diseño por fases**. La Fase 1 está especificada a nivel implementable. Las Fases 2+ están diseñadas a nivel de decisión de modelado y trade-offs, con **preguntas abiertas de negocio y fiscales sin cerrar** (§9). NO se debe implementar Fase 2+ sin cerrar esas preguntas y sin pasar por `software-architect-reviewer` + `travel-agency-accountant-argentina`.

---

## 1. Contexto

### 1.1 Hechos verificados en el repo (2026-06-02)

Confirmados leyendo código, no asumidos:

- **DraftAsync infiere el operador SOLO de la tabla genérica.** `BookingCancellationService.cs:169-176` consulta `_db.Servicios` (entidad `ServicioReserva`, `SupplierId int?` nullable) y toma el primero con `SupplierId != null`. Si no encuentra, tira `InvalidOperationException`. (Bug 1 confirmado.)
- **El modelo de servicios evolucionó a 5 entidades tipadas**, cada una con su tabla y su `SupplierId int NOT NULL` y `ReservaId int NOT NULL`:
  - `HotelBooking.cs:15`, `FlightSegment.cs:15`, `TransferBooking.cs:15`, `PackageBooking.cs:15`, `AssistanceBooking.cs:31`.
  - Todas tienen además un `Status` string libre y un `SalePrice decimal`.
  - **DraftAsync no mira ninguna de las 5.** Una reserva cuyos servicios viven solo en las tablas tipadas NO se puede cancelar hoy. (Confirma el síntoma "casi ninguna reserva real se puede cancelar".)
- **`BookingCancellation.SupplierId` es un solo `int` NOT NULL** (`BookingCancellation.cs:38`). Aggregate 1:1 con Reserva, UNIQUE en BD (INV-081, `DraftAsync:141-147`). (Bug 2 confirmado.)
- **INV-126 (mono-operador duro)**: `OperatorRefundService.cs:320-325` rechaza cualquier refund cuyo `SupplierId != bc.SupplierId`. (Bug 2 confirmado, blindado por invariante.)
- **Solo cancelación TOTAL**: la cancelación setea el estado de la reserva ENTERA (`BookingCancellationService.cs:1065` → `EstadoReserva.Cancelled`; otros puntos → `PendingOperatorRefund`). No hay concepto de "servicio cancelado, resto vivo". (Bug 3 confirmado.)
- **`DraftCancellationRequest`** (`CancellationDtos.cs:138`) es un record posicional con `ReservaPublicId` + `Reason` solamente. El comentario en `BookingCancellationService.cs:159-162` ya anticipa `DraftCancellationRequest.SupplierPublicId`.
- **NC fiscal**: es UNA, vinculada a `OriginatingInvoiceId` (la factura de venta de la agencia al cliente). El cliente le compró a la agencia, no a los operadores → la dimensión "operador" es de pago a proveedores / refunds, no de la relación fiscal agencia↔cliente. (Ancla fiscal del domain-expert; coherente con el modelo de datos: `BookingCancellation.OriginatingInvoiceId` es la única factura, y `CreditNoteInvoiceId` la única NC.)
- **Máquina de NC parcial ya existe** detrás de `EnablePartialCreditNotes` (ADR-009): `IFiscalLiquidationCalculator`, emisión de NC parcial real al ARCA (ADR-009 Fase 2), bandeja de reconciliación (ADR-010). **Hoy scoped a Hotel**: servicios distintos siguen flujo NC total, y si la reserva **mezcla** servicios, FC1.3 rechaza (ADR-009 §1.4).
- **Flags vigentes**: `EnableNewCancellationFlow` (FC1.2), `EnablePartialCreditNotes` (ADR-009), `EnableCancellationDebitNote` (ADR-013), `EnableSoldToSettleStates` (rediseño estados reserva). Editables desde el panel admin (no se toca DB).
- **Convención de migraciones**: aditivas, prefijo por feature (`FC1_`, `Fase2_`, `Adr013_M1`…), Postgres, CHECK constraints, `xmin` concurrency. La última es `Adr014_M1_AddDeferredPenaltyConfirmation`.

### 1.2 El problema de negocio real

Hay tres bugs encadenados, pero la causa raíz es **un modelo que asume 1 reserva = 1 operador = 1 evento fiscal total**:

1. **Bug 1 (bloqueante operativo)**: la inferencia de operador quedó vieja respecto al modelo de servicios tipados → desbloqueo urgente.
2. **Bug 2 (modelo)**: 1 BC soporta 1 solo operador, blindado por INV-126 y UNIQUE INV-081. La operación real mezcla operadores (paquete dinámico: hotel de un operador, aéreo de otro, cada uno con su política de penalidad).
3. **Bug 3 (modelo)**: no existe cancelación parcial (cancelar un servicio, dejar el resto vivo).

Bug 1 es un **parche de inferencia**. Bug 2 y 3 son **cambios de modelo** y comparten la misma decisión estructural: cómo se relaciona la dimensión operador/servicio (varios) con el evento fiscal hacia el cliente (uno).

---

## 2. Decisión central de modelado

> **La NC al cliente es ÚNICA por factura de venta. La multiplicidad (operadores, servicios) vive en una capa de *líneas* DEBAJO del BC, no en múltiples BC por reserva.**

Es decir: **NO** rompemos el UNIQUE 1-BC-por-reserva (INV-081). Mantenemos **un único `BookingCancellation` por reserva** como aggregate root del evento de cancelación, y le agregamos **líneas hijas** (`BookingCancellationLine`) — una por servicio cancelado / por operador involucrado. Cada línea lleva su `SupplierId`, su monto, su penalidad, su estado de refund.

### 2.1 Por qué un BC-padre con líneas (y no N BC por reserva)

Evaluado contra la alternativa "1 BC por operador" (§8, Alternativa B):

| Eje | BC-padre + líneas (elegida) | N BC por operador (descartada) |
|---|---|---|
| **NC fiscal única** | Natural: el BC-padre es el dueño de `OriginatingInvoiceId` y de la NC. Las líneas no emiten NC propia. Coherente con "el cliente le compró a la agencia". | Forzado: ¿quién emite la NC si hay N BC? Hay que elegir un BC "principal" igual → se reintroduce el problema. O N NC sobre la misma factura → riesgo fiscal alto (NC fragmentada sin criterio). |
| **INV-081 (1 cancelación activa por reserva)** | Se preserva tal cual. Cero migración de la constraint. | Hay que **romper** el UNIQUE y reemplazarlo por lógica de "no solapar líneas/servicios", más frágil. |
| **Cancelación parcial** | Una línea = un servicio cancelado. El resto de la reserva sigue vivo sin tocar el BC de los servicios no cancelados. | Cada cancelación parcial = un BC nuevo → proliferación de BC, difícil de reconciliar contra una sola factura. |
| **Refund por operador (INV-126)** | INV-126 se reformula: el refund se asocia a la **línea** cuyo `SupplierId` coincide, no al BC. Cada línea tiene su cap de refund. | INV-126 queda igual pero ahora hay que rutear el refund al BC correcto → más superficie de error. |
| **Penalidad/ND por operador (ADR-013/014)** | Penalidad por línea. La ND propia se sigue emitiendo a nivel del evento (factura única), sumando las penalidades propias de las líneas que correspondan. | 1 ND por BC → N ND por reserva sobre la misma factura → complejidad fiscal. |
| **Auditoría / reporting por operador** | `GROUP BY line.SupplierId`. | `GROUP BY bc.SupplierId` pero con N BC → ok pero más joins. |
| **Compatibilidad con lo construido (ADR-009/013/014)** | Alta: el BC sigue siendo el aggregate, los campos fiscales actuales (FiscalSnapshot, FiscalLiquidation, ND) quedan a nivel evento. Las líneas son aditivas. | Baja: todo lo que asume 1 BC = 1 reserva se rompe. |

**Conclusión.** El patrón BC-padre + líneas:
- preserva el ancla fiscal (1 factura → 1 NC),
- preserva INV-081 (cero migración destructiva),
- modela naturalmente parcial y multi-operador en la misma estructura,
- reusa lo ya construido (ADR-009/013/014 viven a nivel evento o se bajan a nivel línea de forma aditiva).

Es además **menos código nuevo** que mantener N BC coherentes contra una factura.

### 2.2 Forma del modelo objetivo (Fase 2+, NO Fase 1)

```
Reserva (1) ──< (1) BookingCancellation          [aggregate root, INV-081 intacto]
                       │  OriginatingInvoiceId  (1 factura)
                       │  CreditNoteInvoiceId   (1 NC)            <- nivel EVENTO
                       │  DebitNote* (ADR-013/14)                 <- nivel EVENTO (suma de penalidades propias)
                       │  FiscalSnapshot / FiscalLiquidation      <- nivel EVENTO
                       │
                       └──< (N) BookingCancellationLine            <- nivel LÍNEA (NUEVO)
                                  SupplierId            (operador de esta línea)
                                  ServiceRef            (qué servicio: tabla + id, ver §6.3)
                                  Scope = Full | Partial
                                  LineAmount            (porción de venta cancelada de este servicio)
                                  PenaltyAmount / PenaltyStatus / ConceptKind  (por operador)
                                  RefundCap / ReceivedRefundAmount             (refund de ESTE operador)
```

La NC sigue siendo **una** sobre la factura. Si la cancelación es parcial, la NC es parcial (reusa ADR-009): acredita la porción de las líneas canceladas. INV-126 se reformula a "el refund se asocia a la línea con el `SupplierId` que coincide".

---

## 3. Plan por fases (entregable, no big-bang)

### Fase 1 — Desbloqueo (chica, segura, implementable YA)

**Objetivo**: que las reservas mono-operador con servicios tipados se puedan cancelar HOY, sin tocar el modelo fiscal, detrás del flag existente `EnableNewCancellationFlow`.

**Alcance**:
1. **Inferencia de operador desde las 5 tablas tipadas + genérica.** Reemplazar la query única de `DraftAsync:169-176` por una **unión** de:
   - `HotelBooking`, `FlightSegment`, `TransferBooking`, `PackageBooking`, `AssistanceBooking` (todas `SupplierId NOT NULL`),
   - `ServicioReserva` (genérica, `SupplierId != null`),
   - filtrando por `ReservaId == reserva.Id`, **dedupe por `SupplierId`**, devolviendo el conjunto distinto de operadores de la reserva.
   - **Performance**: 6 queries `SELECT DISTINCT SupplierId` (una por tabla) o una unión; NO traer entidades completas (evitar N+1). Cada tabla ya tiene índice por `ReservaId` (verificar en la migración correspondiente antes de implementar — si falta, agregarlo).
2. **Selector de operador en el request.** Agregar `SupplierPublicId Guid?` **opcional** a `DraftCancellationRequest` (record posicional → agregar al final con default para no romper callers).
   - Si la reserva tiene **1 solo operador** → autorresuelto, `SupplierPublicId` puede venir null.
   - Si tiene **>1 operador** → `SupplierPublicId` **obligatorio**; si viene null, rechazar con error claro (nuevo invariante, ej. `INV-150 MultiOperatorRequiresSelection`).
   - **Validación backend**: el `SupplierPublicId` elegido **debe pertenecer** a la reserva (estar en el conjunto inferido en (1)). Si no, rechazar (`INV-151`). NO confiar en el frontend.
3. **Salvaguarda visible multi-operador.** Cuando hay >1 operador y se elige uno, el BC se crea solo para ese operador (comportamiento mono-operador de hoy). Los demás operadores quedan **fuera** de esta cancelación y se gestionan a mano. Persistir esa info para la UI y la auditoría:
   - **Decisión**: NO crear columnas nuevas en Fase 1. Registrar los operadores no incluidos en el **audit log** del draft (`AuditActions.BookingCancellationDrafted`, ya existe) dentro del `details` JSON (ej. `excludedSuppliers: [{publicId, name}]`). Es aditivo, no toca el esquema. La UI lee el dato del DTO de respuesta (que sí puede exponer `excludedSuppliers` calculado al vuelo).
   - **Riesgo aceptado y declarado**: en Fase 1 la agencia debe gestionar a mano los otros operadores. La salvaguarda es **informativa**, no transaccional. Esto es intencional: Fase 1 NO resuelve multi-operador, solo lo hace **visible y seguro** (no cancela silenciosamente ignorando operadores).
4. **Selector en el modal de cancelación** (`CancelReservaModal.jsx`, paso 1 "draft"): combo de operadores de la reserva, default al de mayor `SalePrice` agregado (criterio del domain-expert), visible solo si hay >1. Si hay 1, no se muestra. Banner de salvaguarda si hay >1 ("se cancelará solo con {operador}; los demás se gestionan aparte"). Manejo de error `INV-150/151` (409).

**Lo que Fase 1 NO hace**: no introduce líneas, no toca INV-126, no cancela parcial, no toca el modelo fiscal. Una reserva multi-operador se cancela **eligiendo un operador**; el resto es manual.

**Flag**: `EnableNewCancellationFlow` (ya existe). Sin flag nuevo. Con el flag OFF, `DraftAsync` ni se ejecuta (kill switch `EnsureFeatureFlagOnAsync`), así que el cambio es inerte hasta que esté ON. **Pero ojo (riesgo declarado): el flag ya está ON en algunos entornos** — ver §7.

**Esfuerzo**: bajo. **Riesgo**: bajo-medio (toca el path de cancelación que ya está ON; mitigado por tests + el hecho de que hoy ese path está roto para reservas tipadas, así que cualquier cosa es mejora). **Reversible**: sí, el cambio de inferencia es autocontenido.

---

### Fase 2 — Líneas de cancelación + multi-operador real

**Objetivo**: modelar el evento de cancelación como BC-padre + N líneas (§2). Cancelar una reserva multi-operador en **un solo BC**, con refund y penalidad **por operador**.

**Alcance**:
1. **Entidad nueva `BookingCancellationLine`** (§2.2): `Id`, `PublicId`, `BookingCancellationId` (FK), `SupplierId`, referencia al servicio (§6.3), `Scope` (Full/Partial), `LineAmount`, campos de penalidad por línea, `RefundCap`, `ReceivedRefundAmount`. Migración **aditiva** (`Adr015_M1`), tabla nueva, sin tocar `BookingCancellations`.
2. **Backfill conceptual**: los BC existentes (1 operador) se modelan como BC con **una sola línea** implícita. Decisión: ¿se crea físicamente la línea por backfill, o el código trata "BC sin líneas" como "1 línea = bc.SupplierId"? **Recomendación**: backfill que crea 1 línea por BC histórico (más limpio, evita ramas legacy en el código). A confirmar volumen de BC históricos antes (si es chico, backfill directo).
3. **INV-126 reformulado**: el refund se asocia a la **línea** cuyo `SupplierId` coincide. `OperatorRefundService:320-325` cambia de `refund.SupplierId == bc.SupplierId` a `refund.SupplierId ∈ {lines.SupplierId}` y se imputa a la línea correcta. Cada línea tiene su `RefundCap`. `bc.SupplierId` se mantiene como denormalización del operador "principal" (compat) o se deprecia gradualmente — **decisión de migración a tomar** (§9 Q-T1).
4. **NC sigue siendo única** sobre la factura. En multi-operador con cancelación total, la NC es total (como hoy). El detalle por operador vive en las líneas, no en la NC.
5. **Penalidad/ND por operador**: cada línea con penalidad propia de la agencia contribuye a la ND. La ND fiscal sigue siendo a nivel evento (sobre la factura única). **Esto requiere validación del contador** (§9 Q-F1): ¿una ND que suma penalidades de varios operadores es correcta fiscalmente, o cada penalidad propia necesita su propio comprobante? ADR-013/014 hoy asume 1 penalidad por BC.

**Flag**: nuevo `EnableMultiOperatorCancellation`, default OFF. Con OFF, el flujo Fase 1 (mono-operador con selector) sigue intacto. **Pre-condición de startup**: igual que ADR-009 con FC1.2, exigir `EnableNewCancellationFlow=ON` si este flag está ON.

**Esfuerzo**: alto. **Riesgo**: alto (toca refund, penalidad, invariantes fiscales, migración con backfill). **Reversible**: el flag aísla; la migración es aditiva (la tabla nueva no rompe nada con flag OFF).

---

### Fase 3 — Cancelación parcial (cancelar un servicio, dejar el resto vivo)

**Objetivo**: cancelar 1 servicio (1 operador) y mantener viva la reserva por el resto.

**Alcance**:
1. **`Scope = Partial`** en la línea: la cancelación afecta solo a los servicios referenciados por las líneas, no a toda la reserva.
2. **NC parcial fiscal**: **reusar ADR-009** (`EnablePartialCreditNotes`, `IFiscalLiquidationCalculator`, emisión NC parcial real, bandeja ADR-010). La NC parcial acredita la porción de venta de los servicios cancelados; la factura original sigue viva por el resto. Esto es **exactamente** lo que ADR-009 construyó — el peso fiscal ya está resuelto y homologado (o en proceso). **Restricción heredada de ADR-009**: hoy está scoped a Hotel y rechaza reservas mixtas (ADR-009 §1.4). Ampliar a otros servicios y a reservas mixtas es trabajo de esta fase + validación contador.
3. **Estado de la reserva**: hoy la cancelación setea el estado de la reserva ENTERA (`:1065`, `:588`, etc.). En parcial **no se debe** mover toda la reserva a `Cancelled`. Decisión de modelado: el estado de cancelación pasa a vivir a nivel **servicio** (cada tabla tipada ya tiene su `Status` string) — la línea cancelada marca su servicio como cancelado; la reserva permanece en su estado vivo. **Esto interactúa con el rediseño de estados de reserva (`EnableSoldToSettleStates`)** — coordinar (§9 Q-N1).
4. **Saldo**: cancelar un servicio reduce el saldo de la reserva por la porción cancelada (menos penalidades retenidas). El cálculo de saldo debe excluir los servicios cancelados. **Verificar el flujo de cálculo de saldo antes** (no inspeccionado en este ADR — supuesto declarado).
5. **Factura**: la factura original sigue viva por el remanente; la NC parcial la reduce. (Reuso ADR-009.)

**Flag**: nuevo `EnablePartialServiceCancellation`, default OFF. Depende de Fase 2 (líneas) y de ADR-009 (`EnablePartialCreditNotes`) prendido y homologado.

**Esfuerzo**: alto. **Riesgo**: alto (fiscal + saldo + estado de reserva + interacción con dos rediseños en vuelo). **Reversible**: flag.

---

## 4. Resumen de fases (esfuerzo / riesgo relativo)

| Fase | Qué desbloquea | Esfuerzo | Riesgo | Flag | Depende de |
|---|---|---|---|---|---|
| **1 — Desbloqueo** | Cancelar reservas mono-operador con servicios tipados (HOY). Multi-operador = elegir 1 + salvaguarda manual. | **Bajo** | Bajo-medio | `EnableNewCancellationFlow` (existe) | — |
| **2 — Líneas / multi-operador** | Cancelar multi-operador en 1 BC, refund + penalidad por operador. | **Alto** | Alto | `EnableMultiOperatorCancellation` (nuevo) | Fase 1; validación contador Q-F1 |
| **3 — Parcial** | Cancelar 1 servicio dejando la reserva viva, NC parcial. | **Alto** | Alto | `EnablePartialServiceCancellation` (nuevo) | Fase 2 + ADR-009 ON/homologado; Q-F2, Q-N1 |

**Recomendación de orden y corte para el dueño**: construir **Fase 1 ya** (desbloquea operación, riesgo bajo). Parar y medir: ¿cuántas cancelaciones reales son multi-operador y cuántas parciales? Eso decide si Fase 2 o Fase 3 va primero. Si la mayoría del dolor es "no puedo cancelar" → Fase 1 alcanza un buen rato. Fase 2 y 3 son inversiones grandes que requieren cerrar preguntas fiscales (§9) **antes** de tocar código.

---

## 5. Reuso de lo ya construido (no duplicar)

- **NC parcial (ADR-009 + ADR-010)**: es el motor fiscal de la Fase 3. NO se reimplementa. La cancelación parcial **invoca** el flujo de NC parcial existente. Restricción a levantar: scope Hotel-only y rechazo de reservas mixtas.
- **ND penalidad (ADR-013/014)**: el motor de ND propia se reusa en Fase 2 a nivel evento, sumando las penalidades propias de las líneas. El modelo de "penalidad por BC" se baja a "penalidad por línea" de forma aditiva (los campos ADR-013 en `BookingCancellation` quedan como agregado del evento; el detalle por operador va en la línea).
- **Bandeja de reconciliación (ADR-010)** y **bandeja de ND pendiente (ADR-013 §3.10)**: reutilizables; pasan a agrupar/filtrar por línea.
- **Approval / 4-eyes (`ApprovalRequest`)**: el patrón existente cubre los overrides de Fase 2/3 sin bandeja nueva (igual que ADR-009 G2).

---

## 6. Modelo de datos y migraciones

### 6.1 Fase 1 — sin cambios de esquema

- `DraftCancellationRequest` gana `SupplierPublicId Guid?` (DTO, no esquema).
- Los operadores excluidos se registran en el `details` JSON del audit log existente. **Cero migración.**

### 6.2 Fase 2 — migración aditiva `Adr015_M1`

- Tabla nueva `BookingCancellationLines` (FK a `BookingCancellations`, `xmin` concurrency, CHECK constraints de coherencia). Aditiva, no toca tablas existentes.
- Backfill: 1 línea por BC histórico (§3 Fase 2.2) — script SQL idempotente, prevalidado contra dump como manda la convención del proyecto.
- Índices: por `BookingCancellationId` y por `SupplierId`.

### 6.3 Referencia al servicio en la línea (decisión a cerrar en Fase 2)

Las 5 tablas tipadas + la genérica son entidades distintas. Una línea necesita apuntar a "qué servicio se cancela". Opciones:
- **(a) Par `(ServiceTable enum, ServiceId int)`** — discriminador + id. Simple, sin polimorfismo EF. Recomendado por pragmatismo.
- **(b) `PublicId` del servicio + tabla resuelta por lookup** — más indirecto.
- **(c) Tabla de servicios unificada** — refactor grande, fuera de scope.

**Recomendación**: (a). Es lo que menos acopla y no exige rediseñar el modelo de servicios. A validar con `backend-dotnet-senior` en el diseño detallado de Fase 2.

### 6.4 Estrategia general

- Todas las migraciones **aditivas y flag-gated**. Con flags OFF, esquema nuevo presente pero inerte (byte-idéntico en comportamiento).
- Prevalidación SQL contra dump antes de migrar (convención del repo).
- Sin DROP ni cambios de tipo en columnas existentes en ninguna fase.

---

## 7. Rollback y compatibilidad

- **Fase 1**: el cambio de inferencia es autocontenido. Rollback = revertir el commit. Riesgo: el flag `EnableNewCancellationFlow` **ya está ON en algunos entornos** (verificado en memoria del proyecto: se prendió para probar). Por lo tanto el cambio **no es inerte** al deployar: impacta el path real de cancelación. Mitigación: tests + el path hoy está roto para reservas tipadas, así que el cambio solo puede mejorar; aún así, **smoke test en staging con flag ON antes de prod**.
- **Fase 2/3**: aisladas por flags nuevos default OFF + pre-condición de startup. La migración aditiva no rompe nada con flag OFF. Rollback = apagar el flag desde el panel admin (no requiere redeploy).
- **Compatibilidad de datos**: ningún BC histórico se invalida. El backfill de Fase 2 es aditivo (crea líneas, no modifica BC).

---

## 8. Alternativas consideradas

- **Alternativa A — Parche mono-operador (lo mínimo del Bug 1)**: solo arreglar la inferencia para que mire las tablas tipadas, sin selector ni multi-operador. **Descartada como solución completa** porque el dueño dijo que multi-operador es frecuente; pero **es exactamente la Fase 1** (con el selector agregado para no cancelar silenciosamente ignorando operadores). O sea: la Fase 1 ES el parche, hecho bien.
- **Alternativa B — N BC por operador (romper INV-081)**: descartada (§2.1). Reintroduce el problema de "quién emite la NC", rompe la constraint UNIQUE, prolifera BC contra una factura única, complica la reconciliación. Más frágil y más código.
- **Alternativa C — Tabla de servicios unificada (gran refactor)**: unificar las 5 tablas tipadas + genérica en un modelo polimórfico. Descartada: es un rewrite del modelo de servicios, alto riesgo, no requerido para resolver la cancelación. La referencia `(tabla, id)` (§6.3) evita esta necesidad.
- **Alternativa D — Multi-operador vía múltiples NC sobre la factura**: descartada por riesgo fiscal (NC fragmentada sin criterio claro contradice el ancla "1 factura → 1 NC" y el criterio del contador de ADR-009).

---

## 9. Preguntas abiertas (negocio y fiscal) — BLOQUEAN Fase 2+

**No inventar respuestas. Estas preguntas deben cerrarse con el dueño y/o el contador antes de implementar Fase 2/3.**

### Negocio (dueño)
- **Q-N1**: En cancelación parcial, ¿qué pasa con el estado de la reserva? ¿Hay un estado "parcialmente cancelada" o solo se marca el servicio? (Interactúa con el rediseño de estados `EnableSoldToSettleStates`.)
- **Q-N2**: ¿El refund del operador se trackea en el sistema o a mano? (Hoy "mezcla / no seguro".) Define cuánto de Fase 2 es transaccional vs informativo.
- **Q-N3**: En multi-operador, ¿el cliente recibe **un** reembolso agregado (la agencia recupera de cada operador y reintegra el total) o reembolsos separados? El domain-expert sugirió agregado — confirmar.
- **Q-N4 (Fase 1)**: ¿El default del selector "operador de mayor SalePrice agregado" es el criterio correcto, o el dueño prefiere otro (ej. operador del servicio más caro individual, o sin default)?

### Fiscal (contador / `travel-agency-accountant-argentina`)
- **Q-F1**: ¿Una ND única que suma penalidades propias de **varios** operadores es fiscalmente correcta, o cada penalidad propia necesita comprobante separado? (ADR-013/014 asume 1 penalidad por BC.)
- **Q-F2**: NC parcial por servicio en reserva **mixta** (varios tipos de servicio / operadores) — ADR-009 hoy lo rechaza. ¿Bajo qué criterio fiscal se puede acreditar parcialmente una factura que mezcla servicios? (Probable: requiere el análisis integrado del `travel-agency-accountant-argentina`.)
- **Q-F3**: Multimoneda + multi-operador (operadores cotizando en monedas distintas dentro de una factura) — interacción con ADR-011/012. Fuera de scope inicial, marcar como fase futura.

### Migración (técnica, a cerrar en diseño de Fase 2)
- **Q-T1**: ¿`bc.SupplierId` se mantiene como denormalización del operador principal o se deprecia tras introducir líneas?
- **Q-T2**: Referencia al servicio en la línea: confirmar opción (a) `(tabla, id)` (§6.3) con `backend-dotnet-senior`.
- **Q-T3**: Volumen de BC históricos para decidir backfill directo vs lazy.

---

## 10. Estrategia de testing

- **Fase 1**:
  - Unit: inferencia de operadores (0 / 1 / N operadores; mezcla de tablas tipadas + genérica; dedupe). Negativos: `SupplierPublicId` ausente con N>1 (INV-150); `SupplierPublicId` que no pertenece a la reserva (INV-151).
  - Integración: draft de reserva mono-operador tipada (camino que hoy falla); draft multi-operador con selección válida; audit log con `excludedSuppliers`.
  - Frontend: selector visible solo con N>1; default al de mayor SalePrice; manejo 409 INV-150/151; banner salvaguarda.
- **Fase 2/3**: definir junto con `qa-automation-senior` al diseñar cada fase. Cubrir: refund ruteado a línea correcta (INV-126 reformulado), cap por línea, NC parcial reusando ADR-009, ND multi-línea (tras cerrar Q-F1). Tests de migración/backfill contra Postgres real (no mocks), por la convención del proyecto.

---

## 11. Riesgos operativos y de seguridad/datos

- **Fiscal (alto, Fase 2/3)**: emitir comprobantes (NC parcial, ND multi-operador) sin cerrar Q-F1/Q-F2 es el mayor riesgo. **Mitigación**: flags OFF + validación contador obligatoria + homologación ARCA antes de prender.
- **Datos (medio, Fase 2)**: backfill de líneas sobre BC históricos. **Mitigación**: script idempotente, prevalidación contra dump, aditivo (no modifica BC).
- **Seguridad / autorización**: el selector de operador y la clasificación de penalidad por línea deben respetar los permisos existentes (`cancellations.classify_agency_penalty`, 4-eyes). NO confiar en el frontend para validar pertenencia del operador a la reserva (INV-151 server-side). Requiere `security-data-risk-reviewer` en Fase 2/3 (toca cancelaciones, refunds, comprobantes).
- **Coordinación (medio)**: Fase 3 interactúa con dos rediseños en vuelo (`EnableSoldToSettleStates`, NC parcial/ND). Riesgo de pisarse. **Mitigación**: cerrar Q-N1 y secuenciar.

---

## 12. Consecuencias

**Positivas**:
- Fase 1 desbloquea la operación real con riesgo bajo y sin tocar fiscal.
- El modelo BC-padre + líneas preserva el ancla fiscal y las invariantes existentes, y reusa ADR-009/013/014 en vez de duplicar.
- Cada fase es entregable y reversible por flag.

**Negativas / costos**:
- Fase 2/3 son inversiones grandes con dependencia de decisiones fiscales no cerradas.
- Introduce una capa de líneas que el código de refund/penalidad/NC debe aprender a recorrer (más superficie, mitigada por reuso).
- `bc.SupplierId` queda como dato de transición hasta resolver Q-T1.

**Neutrales**:
- El frontend de cancelación (`CancelReservaModal.jsx`) gana un selector en Fase 1; en Fase 2/3 gana vistas por operador/servicio.
```
