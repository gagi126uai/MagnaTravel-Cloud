# Plan tactico tecnico FC1.3 Fase 2 (NC parcial real al ARCA)

- **Fecha**: 2026-05-22.
- **Autor**: `software-architect` agent.
- **Status**: Propuesta para `software-architect-reviewer`.
- **Base**: Fase 1 mergeada (HEAD `06585e6`, 9 sub-fases, 66 unit tests verdes). Doc cierre Fase 1: `docs/explicaciones/2026-05-22-fc1-3-fase-1-implementacion-completa.md`.
- **ADR asociado**: extension del [ADR-009](adr/ADR-009-partial-credit-note.md) (NO ADR nuevo — ver §T7 decision al final).
- **Stack confirmado**: .NET 8 + EF Core 8.x + PostgreSQL via Npgsql 8.0.11 + xmin shadow concurrency token + `BusinessInvariantInterceptor` mapea `SqlState=23514` a `BusinessInvariantViolationException`. Verificado durante Fase 1.

> **Regla operativa Gaston aplicada**: este documento NO contiene estimaciones de horas. Las sub-fases estan listadas con dependencias + criterios de aceptacion verificables. La cadencia real depende del orden en que el subagente backend las tome.

---

## T1. Resumen tecnico

### T1.1 Que cierra Fase 2

Fase 1 dejo explicitamente afuera (ADR-009 §1.4):

| Item Fase 1 dejado afuera | Quien lo cierra en Fase 2 |
|---|---|
| Emision real de NC parcial al ARCA (hoy AfipService emite total) | T3 / FC1.3.F2.2..F2.3 |
| Persistencia entera de `FiscalLiquidation` (GR-004 difirio) | T3 / FC1.3.F2.1 + backfill |
| Auto-procesamiento de `TotalPlusNewInvoice` (casos 4 y 7 — GR-001 hoy tira `InvalidOperationException`) | T3 / FC1.3.F2.4 |
| Multimoneda en NC parcial (hoy `MultiCurrency` flag manual obligatorio) | T3 / FC1.3.F2.5 |
| Settings nuevos `EnableTotalPlusNewInvoiceAutoProcessing` + `IvaProrrateoMode` | T3 / FC1.3.F2.0 |

### T1.2 Que NO entra a Fase 2 (queda Fase 3 o despues)

- **UI nueva** (modal admin para revisar/editar liquidacion, bandeja con filtros, alertas in-app). Fase 3.
- **Activar setting `CommissionOnly` auto** (GR-003) y **activar setting `Reseller + penalty` auto** (GR-006). Esto depende de respuestas F2 + F4 del contador, **no de codigo**. Cuando el contador responda, se prende un setting y eventualmente se ajusta la formula del calculator (Fase 2 si llega la respuesta antes; sino queda en backlog).
- **Promover `ApprovalRequest.Metadata` a `jsonb`** (N-006 round 3 ADR-009). Si Fase 2 no necesita queries dentro del Metadata, se difiere a un trabajo de hardening separado.
- **E2E tests con `CustomWebApplicationFactory`**. Quedan diferidos a sesion QA dedicada (regla Gaston, ya aplicada en Fase 1).

### T1.3 Dependencias externas (lo que bloquea pleno cierre, pero NO bloquea arranque)

| Dependencia | Que bloquea exactamente | Estrategia Fase 2 |
|---|---|---|
| **F1 contador** (prorrateo IVA: proporcional al neto vs item por item) | Detalle exacto de calculo de `<ImpIVA>` en NC parcial. | Defaults conservadores: `IvaProrrateoMode = ProportionalToNet`. Si el contador responde `PerItem`, se cambia el setting + se ajusta el calculator. Cambio aislado. |
| **F2 contador** (cuotas operador: una NC en T0 vs sucesivas) | Decision "emitir una sola NC parcial en T0 vs N NCs sucesivas". | Default: **una sola NC en T0** (consistente con la propuesta del mensaje round 3). Si el contador pide sucesivas, queda como sub-fase opt-in posterior. |
| **F3 contador** (correccion de NC parcial ya emitida) | Diseño del flow correctivo (ND complementaria vs anular + emitir nueva). | **NO se disena flow correctivo en Fase 2**. Si una NC parcial sale mal, queda flagged en BD + el admin la maneja manualmente fuera del sistema. Backlog Fase 3. |
| **F4 contador** (penalty + reseller en TotalToCustomer: reduce NC o no) | Auto-procesamiento del caso 3 (hoy manual review por GR-006). | **NO se desbloquea en Fase 2**. Se mantiene manual review hasta respuesta F4. |

**Conclusion T1.3**: Fase 2 puede arrancar sin esperar al contador con defaults conservadores explicitos. F1/F2 ajustan settings post-respuesta sin reescribir codigo.

### T1.4 Asunciones explicitas

Lo que NO esta verificado y se asume razonable hasta que el contador o un proveedor lo refute:

1. **El ARCA acepta `<ImpIVA>` con dos decimales redondeado bancariamente** (round-half-to-even) cuando el prorrateo no da entero. Hoy `AfipService.cs:870-875` usa `ToString("0.00", InvariantCulture)`. Verificado el formato — NO verificado el comportamiento del ARCA con sumas que no cuadran por 0.01.
2. **El `<CbtesAsoc>` con factura origen sigue valido para NC parcial**. Hoy `AfipService.cs:827-838` emite `<CbtesAsoc>` SOLO cuando `invoice.OriginalInvoiceId.HasValue`. Asumo que ARCA acepta multiples NCs (parcial 1 + parcial 2 + ...) asociadas a la misma factura origen sin rechazar por correlativo duplicado. **A verificar en QA staging con ARCA homologacion antes de prod**.
3. **Multimoneda factura USD pagada en ARS**: hoy `FiscalSnapshot.ExchangeRateAtOriginalInvoice` se carga al confirmar (verificado FC1.2). Asumo que ARCA acepta NC en USD con `MonId=DOL` + `MonCotiz` igual al TC del momento del **comprobante original**, no al TC del dia de la NC. Esto es regla fiscal estandar pero a confirmar con contador (sub-pregunta nueva para round 4).
4. **Casos `TotalPlusNewInvoice` (4 y 7) son raros** (< 5% del total). Si fueran mayoritarios, la complejidad del flow doble (NC total + factura nueva, ver §T5) cambiaria el orden de prioridades. Esto se valida con auditoria de `BookingCancellation.ReviewRequiredReason & (OriginalInvoiceUnclear | RetentionChangesNature)` post-Fase 1.

---

## T2. Orden + dependencias entre sub-fases

```
FC1.3.F2.0  Settings nuevos + flag maestro Fase 2 (default OFF)         ─┐
            (EnablePartialCreditNoteRealEmission default false,           │
             EnableTotalPlusNewInvoiceAutoProcessing default false,        │
             IvaProrrateoMode default ProportionalToNet,                   │
             PartialCreditNoteRoundingTolerance default 0.01)             │
                                                                          │
            Migracion Fase2.M0 + extension OperationalFinanceSettings     │
            + validacion startup (pre-condicion: si F2.flag ON, F1.flag    │
            tambien ON). Mismo patron GR-002.                              │
                                                                ─────────┤
                                                                          │
FC1.3.F2.1  Persistir FiscalLiquidation completo                          │ depende F2.0
            (Owned VO en BookingCancellation, 10 columnas prefijo          │
             FiscalLiquidation_*, CHECK suma componentes,                  │
             backfill desde ApprovalRequest.Metadata)                      │
                                                                          │
            Migracion Fase2.M1 + backfill SQL leyendo Metadata JSON.       │
                                                                ─────────┤
                                                                          │
FC1.3.F2.2  AfipService refactor: EnqueuePartialCreditNoteAsync           │ depende F2.1
            (nuevo metodo en InvoiceService + IInvoiceService),            │
            ProcessPartialCreditNoteJob (analogo a                         │
             ProcessAnnulmentJob pero parametrizado por items + IVA        │
             prorrateado).                                                 │
            EnqueueAnnulmentAsync existente NO se toca (back-compat        │
            con FC1.2 ON + FC1.3 OFF).                                     │
                                                                ─────────┤
                                                                          │
FC1.3.F2.3  BC service: reemplazar fallback FC1.2 en OnApprovedAsync.     │ depende F2.2
            Hoy lineas 1431-1468: log warning + EnqueueAnnulmentAsync      │
            (NC TOTAL). Fase 2: si EnablePartialCreditNoteRealEmission     │
            ON -> EnqueuePartialCreditNoteAsync con liquidation persistida.│
            Mantener fallback FC1.2 si flag F2 OFF (rollback granular).    │
                                                                ─────────┤
                                                                          │
FC1.3.F2.4  Caso TotalPlusNewInvoice (casos 4 y 7) — flow dual.           │ depende F2.3
            (a) Anular factura original (NC total existente).              │
            (b) Emitir factura nueva por el remanente conceptual           │
                (FinalNetInvoiced).                                        │
            Idempotencia critica: si (a) sale OK y (b) falla, BC queda    │
            en estado intermedio nuevo PartialFiscalAwaitingNewInvoice.   │
            Job de reconciliacion + endpoint admin force-continue.        │
            Gated por EnableTotalPlusNewInvoiceAutoProcessing.              │
                                                                ─────────┤
                                                                          │
FC1.3.F2.5  Multimoneda en NC parcial.                                    │ depende F2.2
            Quitar el flag MultiCurrency del clasificador (Fase 1 lo       │
             ponia como manual review obligatorio).                        │
            Validar que el AfipService usa FiscalSnapshot.                │
             ExchangeRateAtOriginalInvoice como MonCotiz (no el TC del     │
             dia de la NC).                                                │
                                                                ─────────┤
                                                                          │
FC1.3.F2.6  Observabilidad + reconciliacion bridge ARCA-NC parcial.      │ depende F2.2
            Counters Serilog (mismo patron FC1.2.7b).                     │
            Extension de ArcaAnnulmentReconciliationJob (existente FC1.2) │
            para reconciliar NCs parciales en Pending con ARCA.            │
                                                                ─────────┤
                                                                          │
FC1.3.F2.7  Doc explicativo trainee + actualizacion MEMORY.md.            │ depende todo
            docs/explicaciones/YYYY-MM-DD-fc1-3-fase-2-implementacion.md   │
            Cierre formal Fase 2 + apuntador a Fase 3 (UI).                │
                                                                ─────────┘
```

### T2.1 Dependencias entre sub-fases (resumen)

- **F2.0** es prerequisito de todo.
- **F2.1** (persistencia) habilita **F2.2** (AfipService) porque el job necesita leer los montos de BD, no de Metadata JSON.
- **F2.2** habilita **F2.3** (BC service llama al nuevo metodo) y **F2.5** (multimoneda usa el mismo flow).
- **F2.4** depende de **F2.3** porque reusa parte del flow de emision.
- **F2.6** (observabilidad) depende de **F2.2** porque cuenta lo que emite.
- **F2.7** cierra cuando todo lo demas paso.

### T2.2 Orden recomendado de merge

`F2.0` -> `F2.1` -> `F2.2` -> `F2.3` -> `F2.6` -> `F2.5` -> `F2.4` -> `F2.7`.

Razon: F2.4 (TotalPlusNewInvoice) es el mas complejo y se beneficia de tener todo lo demas mergeado + observabilidad funcionando para depurar.

---

## T3. Sub-fases atomicas con criterios de aceptacion

### FC1.3.F2.0 — Settings nuevos + flag maestro Fase 2

**Tareas atomicas**:

1. Extender `OperationalFinanceSettings` con 4 columnas nuevas:
   - `EnablePartialCreditNoteRealEmission` (bool, default `false`). Master flag Fase 2. Si OFF, el flujo se comporta como Fase 1 (log warning + emite NC total via FC1.2 path).
   - `EnableTotalPlusNewInvoiceAutoProcessing` (bool, default `false`). Si OFF, los casos 4 y 7 siguen tirando `InvalidOperationException` (GR-001 vigente). Si ON, flow dual de F2.4.
   - `IvaProrrateoMode` (enum: `ProportionalToNet = 0` default, `PerItem = 1`). Configurable post-respuesta F1 contador.
   - `PartialCreditNoteRoundingTolerance` (`numeric(18,2)`, default `0.01`). Tolerancia para validacion `ImpTotal = ImpNeto + ImpIVA + ImpTrib` antes de mandar al ARCA. Si la suma se va por mas que este valor, throw + log error (defensivo, no enviar XML inconsistente).

2. Crear nuevo enum `src/TravelApi.Domain/Entities/IvaProrrateoMode.cs` con dos valores (`ProportionalToNet`, `PerItem`).

3. Crear migracion EF `Fase2_M0_AddFc13Phase2Settings`. Aditiva, sin DROP, columnas con DEFAULT inline. Postgres syntax (`numeric(18,2)`, comillas dobles).

4. **Validacion startup pre-condicion (mismo patron GR-002)**:
   - Si `EnablePartialCreditNoteRealEmission=true` AND `EnablePartialCreditNotes=false` -> rechazar arranque con mensaje claro. F2 depende de F1 (sin Fase 1 el clasificador no corre y no hay liquidacion para emitir).
   - Si `EnableTotalPlusNewInvoiceAutoProcessing=true` AND `EnablePartialCreditNoteRealEmission=false` -> rechazar arranque. El flow dual necesita el plumbing de emision real.

5. **Validacion runtime en `OperationalFinanceSettingsService.UpdateAsync`**: mismo enforcement que GR-002 Fase 1 (rechaza el guardado de la combinacion invalida con `ValidationException`).

6. **FluentValidation del DTO request** (defense-in-depth, mismo patron Fase 1).

**Criterio de aceptacion FC1.3.F2.0**:

- `dotnet build` verde.
- Migracion aplica contra BD vacia + contra dump de staging sin errores.
- Test integration `Startup_Fase2OnWithoutFase1_RejectsWithError`: configurar settings con `EnablePartialCreditNoteRealEmission=true && EnablePartialCreditNotes=false` directamente en BD, app falla al arrancar.
- Test integration `Settings_AdminTriesToEnableF2WithoutF1_Rejects` (runtime): admin invoca `UpdateAsync` con combinacion invalida, `ValidationException` (HTTP 400).
- Test integration `Startup_TotalPlusNewInvoiceOnWithoutEmissionOn_Rejects`.

**Dependencias**: ninguna (puede mergear suelto, sin tocar codigo de runtime).

---

### FC1.3.F2.1 — Persistir `FiscalLiquidation` completo + backfill

**Tareas atomicas**:

1. Crear `src/TravelApi.Domain/Entities/FiscalLiquidation.cs` como **Owned VO** dentro de `BookingCancellation` (mismo patron que `FiscalSnapshot`). 10 propiedades:
   - `decimal OriginalInvoiceAmount`
   - `decimal CancellationAmount`
   - `decimal OperatorPenaltyAmount`
   - `decimal NonRefundableItemsAmount`
   - `decimal FiscalAmountToCredit`
   - `decimal AmountToRefundCustomer`
   - `decimal FinalNetInvoiced`
   - `string Currency` (max 3 chars)
   - `DateTime? ComputedAt` (timestamptz)
   - `string? ComputedByUserId`, `string? ComputedByUserName`

2. Agregar `public FiscalLiquidation? FiscalLiquidation { get; set; }` a `BookingCancellation`. Nullable porque BCs Fase 1 (pre-F2.1) tienen el detalle solo en `ApprovalRequest.Metadata`, no en columnas dedicadas. Backfill cierra ese gap.

3. Configurar el owned VO en `AppDbContext.OnModelCreating` con prefijo `FiscalLiquidation_*` (igual que `FiscalSnapshot_*`). 10 columnas resultantes.

4. **Migracion Fase2.M1**:
   - `ALTER TABLE "BookingCancellations"` con 10 columnas nuevas, nullable salvo `FiscalLiquidation_Currency` (string, default `'ARS'` para no romper backfill si la columna queda con valor por defecto).
   - CHECK constraint nuevo `chk_BookingCancellations_fiscalliquidation_sum` (INV-FC1.3-005 promovido a SQL ahora que persistimos los componentes):
     ```sql
     ALTER TABLE "BookingCancellations"
       ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_sum
       CHECK (
         "FiscalLiquidation_FiscalAmountToCredit" IS NULL
         OR ABS(
              "FiscalLiquidation_FiscalAmountToCredit"
              + "FiscalLiquidation_NonRefundableItemsAmount"
              + "FiscalLiquidation_OperatorPenaltyAmount"
              - "FiscalLiquidation_OriginalInvoiceAmount"
            ) <= 0.01
       );
     ```
   - CHECK adicional `chk_BookingCancellations_fiscalliquidation_consistency`: si `FiscalLiquidation_ComputedAt` no es null, `LiquidationComputedAt` (columna summary Fase 1) tampoco puede ser null y deben coincidir (tolerancia 1 segundo). Evita estados degenerados pos-backfill.

5. **Backfill SQL** (dentro de la misma migracion, en `Up`):
   - Para cada BC en estados `ManualReviewPending (9)`, `ManualReviewApproved (10)` y los terminales que pasaron por ahi (`AwaitingFiscalConfirmation (1)` con `PartialCreditNoteApprovalRequestId IS NOT NULL`, `Closed (4)`, etc.), leer el `ApprovalRequest.Metadata` JSON y popular las columnas `FiscalLiquidation_*`.
   - Usa `jsonb_extract_path_text` (Postgres native) en el UPDATE. NO requiere C# code-behind si todos los Metadata estan bien formateados (Fase 1 los serializa con schemaVersion=1).
   - Pseudo SQL:
     ```sql
     UPDATE "BookingCancellations" bc
     SET
       "FiscalLiquidation_OriginalInvoiceAmount" = (m.meta->>'originalInvoiceAmount')::numeric,
       "FiscalLiquidation_CancellationAmount"    = (m.meta->>'cancellationAmount')::numeric,
       "FiscalLiquidation_OperatorPenaltyAmount" = (m.meta->>'operatorPenaltyAmount')::numeric,
       "FiscalLiquidation_NonRefundableItemsAmount" = (m.meta->>'nonRefundableItemsAmount')::numeric,
       "FiscalLiquidation_FiscalAmountToCredit"  = (m.meta->>'fiscalAmountToCredit')::numeric,
       "FiscalLiquidation_AmountToRefundCustomer"= (m.meta->>'amountToRefundCustomer')::numeric,
       "FiscalLiquidation_FinalNetInvoiced"      = (m.meta->>'finalNetInvoiced')::numeric,
       "FiscalLiquidation_Currency"              = COALESCE(m.meta->>'currency', 'ARS'),
       "FiscalLiquidation_ComputedAt"            = (m.meta->>'computedAt')::timestamptz,
       "FiscalLiquidation_ComputedByUserId"      = m.meta->>'computedByUserId',
       "FiscalLiquidation_ComputedByUserName"    = m.meta->>'computedByUserName'
     FROM (
       SELECT ar."Id" as id, ar."Metadata"::jsonb as meta
       FROM "ApprovalRequests" ar
       WHERE ar."RequestType" = 11  -- PartialCreditNoteApproval
         AND ar."Metadata" IS NOT NULL
     ) m
     WHERE bc."PartialCreditNoteApprovalRequestId" = m.id
       AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;  -- idempotente
     ```
   - **Edge case 1**: si `ApprovalRequest.Metadata` no es JSON valido o falta una llave, el cast `::numeric` o `::timestamptz` falla con `invalid input syntax`. La migracion debe **envolver en una sub-query con `WHERE ar."Metadata" ~ '^\s*\{.*\}\s*$'`** o usar `jsonb_typeof(...) = 'object'` para filtrar Metadata malformado y loguear cuantas filas se saltaron (via `RAISE NOTICE`).
   - **Edge case 2**: BCs en estados `ManualReviewRejected (11)` ya tienen `PartialCreditNoteApprovalRequestId = NULL` (Fase 1 los nulea en `OnRejectedAsync` linea 1564-1569). Esos BCs NO tienen approval asociado para leer, asi que sus columnas `FiscalLiquidation_*` quedan NULL — correcto (la liquidacion no aplica porque el caso fue rechazado).
   - **Edge case 3**: BCs Fase 1 que **NO pasaron por manual review** (caso ReasonNone -> auto -> FC1.2 path) tampoco tienen approval. Esos BCs tampoco se backfillean. Si Fase 2 los necesita para reporting, una sub-fase posterior podria popularlos re-corriendo el calculator en background. Por ahora **YAGNI** — esos casos no necesitan liquidacion explicita porque la NC TOTAL del FC1.2 path ya cubre el caso.

6. Actualizar `FiscalLiquidationCalculator` (Fase 1) para que al producir el DTO, el caller (en `BookingCancellationService.ConfirmAsync` y `EditLiquidationAsync`) **persista tambien las columnas dedicadas** (no solo el JSON del approval). Cambio aditivo, sin romper Fase 1.

7. Actualizar `BookingCancellationDto` para exponer `FiscalLiquidation` (campos opcionales) cuando exista.

**Criterio de aceptacion FC1.3.F2.1**:

- `dotnet build` verde.
- Migracion aplica contra BD vacia + dump de staging.
- Test integration `Backfill_FromExistingApprovalMetadata_PopulatesAllColumns`: seed 3 BCs en ManualReviewPending con Metadata JSON correcto. Correr migracion. Verificar que las 10 columnas estan pobladas exactamente igual que el JSON.
- Test integration `Backfill_SkipsRejectedBCs`: BC en ManualReviewRejected (FK approval nulled). Migracion no toca esa fila. Columnas FiscalLiquidation_* siguen null.
- Test integration `Backfill_SkipsMalformedMetadata_LogsWarning`: BC con Metadata = `"not a json"`. Migracion skip + RAISE NOTICE.
- Test integration `Confirm_PostFase2_PersistsBothApprovalMetadataAndDedicatedColumns`: nuevo BC post-Fase 2 -> ambas representaciones de la liquidacion coinciden (Metadata JSON == columnas).
- Test integration `CheckConstraint_SumMismatch_RejectedByPostgres`: INSERT raw con `FiscalAmountToCredit=500 + NonRefundableItemsAmount=100 + OperatorPenaltyAmount=100 != OriginalInvoiceAmount=1000` -> Postgres rechaza con SqlState 23514 -> interceptor mapea a `BusinessInvariantViolationException`.

**Dependencias**: FC1.3.F2.0.

---

### FC1.3.F2.2 — `IInvoiceService.EnqueuePartialCreditNoteAsync` + `ProcessPartialCreditNoteJob`

**Tareas atomicas**:

1. Agregar metodo nuevo a `IInvoiceService`:
   ```csharp
   /// <summary>
   /// FC1.3.F2.2: emite NC PARCIAL al ARCA. A diferencia de
   /// <see cref="EnqueueAnnulmentAsync"/> (que emite NC TOTAL replicando los
   /// items de la factura origen 1:1), este metodo recibe la liquidacion ya
   /// calculada y prorratea IVA segun <see cref="OperationalFinanceSettings.IvaProrrateoMode"/>.
   ///
   /// Idempotencia: rechaza si la factura origen tiene
   /// <see cref="AnnulmentStatus.Pending"/> o <see cref="AnnulmentStatus.Succeeded"/>.
   /// Si esta en Failed, permite reintento.
   ///
   /// Precondicion: la factura origen tiene
   /// <see cref="OperationalFinanceSettings.EnablePartialCreditNoteRealEmission"/>=true
   /// (chequeo defensivo, el caller del BC service ya valida).
   /// </summary>
   Task EnqueuePartialCreditNoteAsync(
       int originalInvoiceId,
       FiscalLiquidationInput liquidation,  // los items + montos + currency
       string userId,
       string? userName,
       string? reason,
       int approvalRequestId,                // siempre exige approval (no hay path Admin bypass aca)
       CancellationToken ct);
   ```

2. Crear DTO `FiscalLiquidationInput` (record) en `src/TravelApi.Application/DTOs/Cancellation/` con shape:
   - `decimal OriginalNetAmount` (ImpNeto de la factura origen).
   - `decimal OriginalVatAmount` (ImpIVA de la factura origen).
   - `decimal OriginalTotalAmount` (ImpTotal).
   - `decimal FiscalAmountToCredit` (el neto+iva a acreditar, en moneda original).
   - `string Currency` (`"ARS"`, `"USD"`, ...).
   - `decimal ExchangeRateAtOriginalInvoice` (T0 del comprobante origen, ver §T1.4 punto 3).
   - `IReadOnlyList<PartialCreditNoteLineDto> Lines` (cada linea con `Description`, `Quantity`, `UnitPrice`, `Total`, `AlicuotaIvaId`). Estas lineas son las que arma F2.3 a partir de los items de la factura origen filtrando los `IsRefundable=false` y reduciendo cantidades/totales segun el caso 1..8.

3. Implementar `EnqueuePartialCreditNoteAsync` en `InvoiceService` (replica el patron de `EnqueueAnnulmentAsync:469-583`):
   - Validar precondiciones (`AnnulmentStatus` no Pending/Succeeded, factura soporta anulacion via `IsSupportedForAnnulment`).
   - Persistir `Invoice.AnnulmentStatus = Pending`, `AnnulledByUserId/Name = userId/userName`, `AnnulmentReason = reason`, `AnnulmentApprovalRequestId = approvalRequestId`.
   - Encolar `_backgroundJobClient.Enqueue<IInvoiceService>(s => s.ProcessPartialCreditNoteJob(originalInvoiceId, liquidationDto, userId, approvalRequestId))`.
   - Serializar `liquidation` a JSON via `JsonSerializer.Serialize` para pasarlo a Hangfire (background jobs no aceptan record con `IReadOnlyList` directamente sin custom serializer; usar string parameter o un DTO POJO simple).

4. Implementar `ProcessPartialCreditNoteJob`:
   - Recupera factura origen (mismo Include que `ProcessAnnulmentJob:596-600`).
   - Idempotencia: si ya existe NC parcial reciente para este approval, abort + notify.
   - **Calculo de prorrateo IVA** segun `settings.IvaProrrateoMode`:
     - `ProportionalToNet` (default): para cada `AlicIva` grupo en `Lines` agrupados por `AlicuotaIvaId`, calcular `BaseImp = sum(Lines.Total)` + `Importe = BaseImp * GetVatMultiplier(alicuotaId)`. Es decir, prorratea **a nivel grupo de alicuota**, no item por item. La suma debe coincidir con `FiscalAmountToCredit` (tolerancia `PartialCreditNoteRoundingTolerance`).
     - `PerItem`: cada linea tiene su propio IVA calculado individualmente y la suma forma `<Iva>`. Para casos donde el contador exige discriminacion fina.
   - **Validacion defensiva pre-envio ARCA**: `ABS(ImpTotal - (ImpNeto + ImpIVA + ImpTrib)) <= PartialCreditNoteRoundingTolerance`. Si falla, throw + log error + marcar `AnnulmentStatus = Failed` (no enviar XML al ARCA inconsistente — rebota con error oscuro y deja el job huerfano).
   - **Mapping tipo NC** (preserva el patron de `InvoiceService.cs:622-639`):
     - Factura A (1) -> NC tipo 3.
     - Factura B (6) -> NC tipo 8.
     - Factura C (11) -> NC tipo 13.
     - Factura A MiPyME (51) -> NC tipo 53.
   - Construir `CreateInvoiceRequest` con `OriginalInvoiceId = original.PublicId.ToString()`, `IsCreditNote = true`, `Items = liquidation.Lines mapped`, `Tributes = []` (Fase 2 NO prorratea tributos provinciales; ver §T8 OQ-3).
   - Llamar `_afipService.CreatePendingInvoice(reservaId, request)` -> retorna `Invoice` con la NC nueva (que llega al SOAP de `AfipService:780+` con `<CbtesAsoc>` ya pluged correctamente por el path existente).
   - Llamar el flow normal `_afipService.ProcessInvoiceJob(newInvoiceId)` para encolar el envio al ARCA.
   - Si el ARCA aprueba: setear `original.AnnulmentStatus = Succeeded`, `AnnulledAt = UtcNow`. Persistir.
   - Si rechaza: `Failed` + notify.

5. **Tests unitarios** (~12) del calculo de prorrateo IVA aislado en una clase helper `PartialCreditNoteIvaCalculator`. Cubrir:
   - `ProportionalToNet` con 1 sola alicuota -> IVA = total * multiplier.
   - `ProportionalToNet` con 2 alicuotas mezcladas (10.5% y 21%).
   - `PerItem` con misma config.
   - Casos borde: `FiscalAmountToCredit` con prorrateo que da .005 (redondeo bancario).
   - Casos que romperian tolerancia: throw.

6. **NO tocar `AfipService` directamente** salvo necesidad real. El path existente `CreatePendingInvoice` + `ProcessInvoiceJob` ya soporta NC con `<CbtesAsoc>`. El trabajo nuevo es en `InvoiceService.ProcessPartialCreditNoteJob` que arma el `CreateInvoiceRequest` correcto y lo entrega al pipeline existente.

**Criterio de aceptacion FC1.3.F2.2**:

- `dotnet build` verde.
- Los 12 tests unitarios del calculator de IVA pasan.
- Test integration `EnqueuePartialCreditNoteAsync_HappyPath_PersistsPendingAndEnqueuesJob`: setup factura B + liquidation con FiscalAmountToCredit=300 (de 1000 total). Verificar que `Invoice.AnnulmentStatus=Pending` post-llamada + 1 job en cola Hangfire mock.
- Test integration `ProcessPartialCreditNoteJob_BuildsCorrectInvoiceRequest`: mockear AfipService, capturar el `CreateInvoiceRequest` que arma el job. Verificar Items, OriginalInvoiceId, IsCreditNote=true, totales correctos.
- Test integration `ProcessPartialCreditNoteJob_SumMismatch_FailsBeforeArca`: liquidation con `FiscalAmountToCredit=100` pero suma de lineas `99.50` (gap > tolerancia). Job marca Failed sin llamar AFIP.
- Test integration `EnqueuePartialCreditNoteAsync_AlreadyPending_Rejects`: factura con AnnulmentStatus=Pending, segunda llamada throw.
- Test E2E (diferido a sesion QA): emitir NC parcial real contra ARCA homologacion + validar respuesta CAE.

**Dependencias**: FC1.3.F2.1 (liquidacion persistida) + FC1.3.F2.0 (settings).

---

### FC1.3.F2.3 — Reemplazar fallback FC1.2 en `BookingCancellationService.OnApprovedAsync`

**Tareas atomicas**:

1. En `BookingCancellationService.cs:1431-1468` (bloque del log warning + EnqueueAnnulmentAsync), reescribir el flow:
   ```
   if (settings.EnablePartialCreditNoteRealEmission && bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal):
       // Fase 2: NC parcial real
       1. Cargar items de la factura origen + filtrar IsRefundable=false segun ReviewRequiredReason flags.
       2. Construir FiscalLiquidationInput con los Lines reducidos + montos de bc.FiscalLiquidation (persistido en F2.1).
       3. Llamar _invoiceService.EnqueuePartialCreditNoteAsync(...).
       4. bc.Status -> AwaitingFiscalConfirmation (igual que hoy).
       5. Marcar approval Consumed.

   else if (bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal):
       // Fase 1 fallback (flag F2 OFF). MANTENER el path actual + log warning explicito.
       [comportamiento actual lineas 1431-1468 sin cambios]
   ```

2. **Construccion de los `Lines`**: este es el corazon del cambio.
   - Si `ReviewRequiredReason.HasFlag(HasNonRefundableItems)`: del `OriginatingInvoice.Items`, **excluir** los que tienen `IsRefundable=false`. Los restantes van como `Lines` proporcionales: cada uno con su `Total` original ajustado para que `SUM(Lines.Total) == FiscalLiquidation.FiscalAmountToCredit` (factor de escala `FiscalAmountToCredit / SUM(refundable_items.Total)`).
   - Si no hay items no reintegrables: los `Lines` son una unica linea con `Description = settings.PartialNcDescriptionTemplate` renderizado + `Total = FiscalAmountToCredit` + `AlicuotaIvaId` = el dominante de la factura origen (mayor `Total` por alicuota).
   - **Si hay multiples alicuotas en la factura origen**: el caller decide entre (a) reproducir todas las alicuotas con prorrateo proporcional por alicuota o (b) colapsar a la dominante. Para Fase 2, **default es (a)** porque preserva fidelidad fiscal. Configurable por `IvaProrrateoMode` (el modo `PerItem` reproduce todas, `ProportionalToNet` colapsa a la dominante — esto puede confundir, ver §T8 OQ-2 para confirmacion).

3. **Idempotencia del callback**: si `OnApprovedAsync` se invoca dos veces para el mismo approval (job de reconciliacion + endpoint admin a la vez), el segundo intento detecta `bc.Status != ManualReviewPending` (ya fue procesado) y retorna no-op + log warning. Esto **ya esta hoy** en el bridge handler — verificar que sigue funcionando con el flow nuevo.

4. **Defensa contra divergencia BD-XML**: antes de llamar `EnqueuePartialCreditNoteAsync`, **re-validar INV-FC1.3-005 sobre `bc.FiscalLiquidation`** persistido. Si la suma se rompio (concurrent edit malicioso entre el approval y la emision), throw + log critical + abortar emision. El CHECK SQL (F2.1) lo cubre tambien, esto es defensa adicional para mejor mensaje de error.

5. **Tests integration nuevos** (~6):
   - `OnApprovedAsync_Fase2On_EmitsRealPartialCreditNote`: setup BC con liquidation persistida + flag F2 ON. Verificar que `_invoiceService.EnqueuePartialCreditNoteAsync` fue llamado (mock) con los `Lines` correctos.
   - `OnApprovedAsync_Fase2Off_FallsBackToFc12FlowWithWarning`: mismo setup + flag F2 OFF. Verificar comportamiento actual + log warning presente.
   - `OnApprovedAsync_NonRefundableItems_ExcludedFromLines`: factura con 3 items (2 refundable + 1 no). Verificar que los Lines del request al InvoiceService NO contienen el item no refundable.
   - `OnApprovedAsync_MultipleAlicuotas_PreservesAll`: factura con items 10.5% + 21%. Verificar que el request mantiene ambas alicuotas con prorrateo proporcional.
   - `OnApprovedAsync_LiquidationSumMismatch_AbortsEmission`: simular concurrent edit que rompio INV-FC1.3-005 (UPDATE raw que bypassa CHECK por algun motivo). Service detecta + throw + audit log + emit no se llama.
   - `OnApprovedAsync_IdempotenceTwoCallsSecondNoop`: invocar `OnApprovedAsync` dos veces — segunda no-op.

**Criterio de aceptacion FC1.3.F2.3**:

- Los 6 tests integration pasan.
- Los 5 tests existentes del bridge (FC1.3.4) siguen verdes (sin regresion del fallback FC1.2).
- Re-correr los 66 unit tests Fase 1 — todos verdes.

**Dependencias**: FC1.3.F2.2 (metodo nuevo en InvoiceService) + FC1.3.F2.1 (liquidacion en BD).

---

### FC1.3.F2.4 — Caso `TotalPlusNewInvoice` (casos 4 y 7) — flow dual

**Tareas atomicas**:

1. **Modificar `BookingCancellationService.ConfirmAsync`** (donde hoy esta el `throw InvalidOperationException` de GR-001, lineas 443-449):
   - Si `settings.EnableTotalPlusNewInvoiceAutoProcessing == false` -> mantener el throw actual (back-compat estricta).
   - Si `true` -> pasar al flow dual. Persistir summary + liquidation completa + setear `bc.CreditNoteKind = TotalPlusNewInvoice` + ir a `ManualReviewPending` (siempre review obligatorio para casos 4/7 por sensibilidad).

2. **Agregar nuevo estado intermedio** `BookingCancellationStatus.PartialFiscalAwaitingNewInvoice = 12`. Necesario porque el flow dual tiene 2 eventos fiscales atomicos pero el segundo puede fallar entre medio.
   - Migracion Fase2.M2 amplia el CHECK Status (si existe enum CHECK) para incluir el 12.

3. **Reescribir `OnApprovedAsync` para casos TotalPlusNewInvoice**:
   ```
   Si bc.CreditNoteKind == TotalPlusNewInvoice && settings.EnableTotalPlusNewInvoiceAutoProcessing:
     Step 1: Llamar _invoiceService.EnqueueAnnulmentAsync (NC TOTAL, codigo existente).
             Hangfire procesa async. Cuando ARCA aprueba la NC, callback (FC1.2 path)
             setea Invoice.AnnulmentStatus=Succeeded.
     Step 2: bc.Status -> PartialFiscalAwaitingNewInvoice (12).
             Persistir + audit BookingCancellationFiscalDualStep1Submitted.
     Step 3: NUEVO job de reconciliacion FC13Phase2DualFlowJob (cron cada 5 min):
             - Detecta BCs en PartialFiscalAwaitingNewInvoice + Invoice.AnnulmentStatus=Succeeded.
             - Llama InvoiceService.EnqueueNewInvoiceForRemainder(bc) que arma la
               factura nueva por bc.FiscalLiquidation.FinalNetInvoiced (con misma config
               fiscal cliente RI/CF, mismo tipo A/B/C).
             - Al success de la nueva factura: bc.Status -> AwaitingFiscalConfirmation
               + bc.CreditNoteInvoiceId (ya seteado en step 1 NC) +
               bc.PostCancellationInvoiceId (nuevo campo persistido por F2.4).
     Step 4: Si step 1 falla en ARCA: bc.Status -> ArcaRejected (estado FC1.2 existente).
             No avanza a step 2.
     Step 5: Si step 2 OK pero step 3 falla en ARCA: bc queda en
             PartialFiscalAwaitingNewInvoice indefinidamente. Job sigue reintentando.
             Endpoint admin force-step3 con InvariantOverride scoped (mismo patron Q3
             round 3 Fase 1) para casos persistentes.
   ```

4. **Agregar columna nueva** `BookingCancellation.PostCancellationInvoiceId` (nullable FK a Invoices). Solo se setea para casos TotalPlusNewInvoice. Migracion Fase2.M2.

5. **Tests integration** (~8):
   - `Confirm_Case4_Fase2DualFlowEnabled_PersistsAndGoesToManualReview`: caso 4 con flag F2.4 ON -> persiste liquidation + ManualReviewPending. NO throw.
   - `Confirm_Case4_Fase2DualFlowDisabled_StillThrows`: flag OFF -> throw (back-compat GR-001).
   - `OnApprovedAsync_DualFlow_Step1Success_TransitionsToPartialFiscalAwaitingNewInvoice`.
   - `DualFlowJob_DetectsStep1SuccessfulBCs_EnqueuesStep2Invoice`.
   - `DualFlowJob_Step1Failed_DoesNotEnqueueStep2`.
   - `DualFlowJob_Step2Failed_RetriesWithBackoffAndNotifies`.
   - `ForceStep2Endpoint_RequiresInvariantOverride_HappyPath`.
   - `ForceStep2Endpoint_NoOverride_Rejects`.

**Criterio de aceptacion FC1.3.F2.4**:

- Los 8 tests pasan.
- Tests Fase 1 del rechazo `InvalidOperationException` (GR-001) **siguen verdes** (porque el flag F2.4 default OFF preserva comportamiento).
- Endpoint force-step3 documentado en doc trainee Fase 2.

**Dependencias**: FC1.3.F2.3 (flow base de emision real). **Esta sub-fase es opcional**: si la auditoria post-Fase 1 muestra que los casos 4/7 son < 5% del volumen, podemos diferir F2.4 a un trabajo posterior y dejar el `throw` como esta. Si son frecuentes, F2.4 es obligatoria.

---

### FC1.3.F2.5 — Multimoneda en NC parcial

**Tareas atomicas**:

1. **Modificar `FiscalLiquidationCalculator`** (Fase 1): hoy `STEP 1` flagea `MultiCurrency` siempre que `Currency != "ARS"`. Fase 2 cambia el criterio:
   - Si `settings.EnablePartialCreditNoteRealEmission == true` -> `MultiCurrency` deja de ser disparador automatico de review manual. La NC se emite en la moneda del comprobante origen con `FiscalSnapshot.ExchangeRateAtOriginalInvoice` como `<MonCotiz>`.
   - Si `false` (Fase 1) -> sigue siendo disparador (comportamiento actual).

2. **Validar en `ProcessPartialCreditNoteJob`**:
   - Si `liquidation.Currency != "ARS"`: setear `MonId = "DOL"` (o el codigo ARCA correspondiente) + `MonCotiz = liquidation.ExchangeRateAtOriginalInvoice`.
   - Hoy `AfipService.cs:879-880` hardcodea `MonId=PES + MonCotiz=1`. Cambio: extraer a `request.MonId` + `request.MonCotiz` y leer de la NC parcial.
   - **Cuidado**: el cambio en `AfipService` afecta TODAS las emisiones, no solo NC parcial. Para no romper FC1.2 ni facturas normales, **agregar parametros opcionales con default `("PES", 1)` en `CreateInvoiceRequest`**. Solo el path nuevo de NC parcial los setea distinto.

3. **Tests unit** (~4):
   - `Calculator_MultiCurrencyWithFase2On_DoesNotFlagReason`.
   - `Calculator_MultiCurrencyWithFase2Off_StillFlagsReason` (back-compat Fase 1).
   - `PartialCreditNoteJob_UsdInvoice_UsesSnapshotExchangeRate`.
   - `PartialCreditNoteJob_ArsInvoice_KeepsDefaultPesoMapping`.

4. **Test integration** (1):
   - `Confirm_UsdInvoice_Fase2On_EmitsNcInUsdWithT0Rate`: setup factura USD, Fase 2 ON, BC auto-aprueba caso 1. Verificar `<MonId>DOL</MonId>` + `<MonCotiz>` igual al TC del snapshot.

**Criterio de aceptacion FC1.3.F2.5**:

- 4 unit + 1 integration test pasan.
- Tests Fase 1 con factura ARS siguen verdes.
- Asuncion §T1.4 punto 3 validada con contador (envia sub-pregunta nueva al contador para round 4) — si rechaza, ajustar logica.

**Dependencias**: FC1.3.F2.2.

---

### FC1.3.F2.6 — Observabilidad + reconciliacion ARCA-NC parcial

**Tareas atomicas**:

1. **Counters Serilog** (mismo patron FC1.2.7b verificado en HEAD):
   - `Fc13.PartialCreditNote.Emitted` por currency/case/InvoicingMode.
   - `Fc13.PartialCreditNote.ArcaApproved`.
   - `Fc13.PartialCreditNote.ArcaRejected` con tag `RejectReason`.
   - `Fc13.PartialCreditNote.SumValidationFailed` (defensivo, no deberia incrementar).
   - `Fc13.PartialCreditNote.DualFlowStep1Succeeded`, `Fc13.PartialCreditNote.DualFlowStep2Succeeded`, `Fc13.PartialCreditNote.DualFlowStuck` (BC en `PartialFiscalAwaitingNewInvoice` > N min).

2. **Extender `ArcaAnnulmentReconciliationJob` existente** (FC1.2):
   - Hoy reconcilia anulaciones tipo NC TOTAL en `Pending` consultando AFIP. Mismo patron sirve para NC parcial: el `Invoice.AnnulmentStatus` y el `Resultado` (`PENDING` -> `A`) son las mismas columnas.
   - El job no necesita saber que la NC es parcial — solo trabaja sobre la `Invoice` que representa la NC. Verificar que el flow actual de `ArcaAnnulmentReconciliationJob.cs` (chequear `src/TravelApi.Infrastructure/Jobs/`) sigue valido para Invoice que tiene `OriginalInvoiceId != null` y `Resultado=PENDING`.
   - Si falta cobertura: extender el WHERE del job para incluir tambien NCs parciales.

3. **Tests integration** (~3):
   - `ReconciliationJob_PartialNcStuckInPending_QueriesArcaAndUpdatesStatus`.
   - `ReconciliationJob_PartialNcSucceededInArca_TransitionsBCToAwaitingFiscal`.
   - `CountersIncremented_OnEmissionAndOutcome`.

**Criterio de aceptacion FC1.3.F2.6**:

- 3 tests pasan.
- Counters visibles en Serilog logs durante un test E2E manual contra ARCA homologacion (diferido a sesion QA).

**Dependencias**: FC1.3.F2.2.

---

### FC1.3.F2.7 — Doc explicativo + memoria

**Tareas atomicas**:

1. Crear `docs/explicaciones/YYYY-MM-DD-fc1-3-fase-2-implementacion.md` nivel trainee.
   - Ejemplo pelotudo (fiambreria evolucionado a "ahora la fiambreria pega bien el ticket a la maquina fiscal del gobierno").
   - Que cambio respecto Fase 1 (antes solo calculaba + logueaba; ahora emite real).
   - Como prender el flag F2 (paso a paso panel admin).
   - Que NO hace Fase 2 (UI, casos 4/7 si flag F2.4 OFF, correccion ND).
   - Tabla con los 4 settings nuevos + sus defaults + cuando cambiar.

2. Actualizar `MEMORY.md` (raiz `.claude/agent-memory/software-architect/`):
   - Nuevo entry "FC1.3 Fase 2 cerrada YYYY-MM-DD".
   - Marcar memorias Fase 1 como superseded donde aplique.

**Criterio de aceptacion FC1.3.F2.7**:

- Doc revisado por Gaston.
- Memoria actualizada.

**Dependencias**: todas las anteriores.

---

## T4. Archivos a tocar / crear

### T4.1 Archivos NUEVOS

| Archivo | Sub-fase | Rationale |
|---|---|---|
| `src/TravelApi.Domain/Entities/FiscalLiquidation.cs` | F2.1 | Owned VO con 10 props. Mismo patron que `FiscalSnapshot`. |
| `src/TravelApi.Domain/Entities/IvaProrrateoMode.cs` | F2.0 | Enum 2 valores (ProportionalToNet, PerItem). |
| `src/TravelApi.Application/DTOs/Cancellation/FiscalLiquidationInput.cs` | F2.2 | Record con items + montos + currency + TC. Lo que `EnqueuePartialCreditNoteAsync` recibe. |
| `src/TravelApi.Application/DTOs/Cancellation/PartialCreditNoteLineDto.cs` | F2.2 | Una linea individual de la NC parcial (Description, Quantity, Total, AlicuotaIvaId). |
| `src/TravelApi.Infrastructure/Services/PartialCreditNoteIvaCalculator.cs` | F2.2 | Helper puro para prorratear IVA segun mode. Testeable sin DB. |
| `src/TravelApi.Infrastructure/Jobs/FC13Phase2DualFlowJob.cs` | F2.4 | Job step 2 para casos TotalPlusNewInvoice. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M0_AddFc13Phase2Settings.cs` | F2.0 | 4 cols settings + validacion. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M1_AddFiscalLiquidationOwnedVoAndBackfill.cs` | F2.1 | 10 cols owned VO + CHECK suma + backfill desde Metadata JSON. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M2_AddDualFlowSupport.cs` | F2.4 | `PostCancellationInvoiceId` + enum status 12. |
| `src/TravelApi.Tests/Unit/PartialCreditNoteIvaCalculatorTests.cs` | F2.2 | 12 unit tests. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteRealEmissionTests.cs` | F2.2 + F2.3 | 12 integration tests del flow real. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteBackfillTests.cs` | F2.1 | Tests del backfill. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteDualFlowTests.cs` | F2.4 | 8 tests del flow dual. |
| `docs/explicaciones/YYYY-MM-DD-fc1-3-fase-2-implementacion.md` | F2.7 | Doc trainee cierre Fase 2. |

### T4.2 Archivos MODIFICADOS

| Archivo | Sub-fase | Que cambia |
|---|---|---|
| `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs` | F2.0 | + 4 settings nuevos. |
| `src/TravelApi.Domain/Entities/BookingCancellation.cs` | F2.1 + F2.4 | + `FiscalLiquidation` owned VO + `PostCancellationInvoiceId`. |
| `src/TravelApi.Domain/Entities/BookingCancellationStatus.cs` | F2.4 | + `PartialFiscalAwaitingNewInvoice = 12`. |
| `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` | F2.1 | + config owned VO `FiscalLiquidation` con prefix. |
| `src/TravelApi.Application/Interfaces/IInvoiceService.cs` | F2.2 | + `EnqueuePartialCreditNoteAsync`. |
| `src/TravelApi.Infrastructure/Services/InvoiceService.cs` | F2.2 + F2.4 | + `EnqueuePartialCreditNoteAsync` + `ProcessPartialCreditNoteJob` + `EnqueueNewInvoiceForRemainder`. |
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | F2.3 + F2.4 | Reemplazar lineas 1431-1468 con flow nuevo (gated por flag F2). + handler dual flow casos 4/7. |
| `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` | F2.5 | Quitar disparador `MultiCurrency` cuando F2 ON. |
| `src/TravelApi/Program.cs` | F2.0 + F2.6 | + registro `FC13Phase2DualFlowJob` + validacion startup pre-condicion F2 requiere F1. |
| `src/TravelApi.Application/DTOs/Cancellation/BookingCancellationDto.cs` | F2.1 | + exposicion del FiscalLiquidation. |
| `src/TravelApi.Application/Constants/AuditActions.cs` | F2.3 + F2.4 | + `BookingCancellationPartialCreditNoteEmitted`, `BookingCancellationFiscalDualStep1Submitted`, `BookingCancellationFiscalDualStep2Submitted`, `BookingCancellationDualFlowForceStep2`. |
| `src/TravelApi.Infrastructure/Services/AfipService.cs` | F2.5 | + soporte `MonId` + `MonCotiz` parametrizables (con defaults PES/1 para no romper resto). Solo si F2.5 lo necesita; si AfipService ya los acepta via `CreateInvoiceRequest`, NO se toca. |

### T4.3 Archivos NO tocados

- `src/TravelApi.Application/Interfaces/IFiscalLiquidationCalculator.cs`: interface intacta. Solo el impl agrega lectura del flag F2 para decidir MultiCurrency.
- `src/TravelApi.Application/Interfaces/IPartialCreditNoteApprovalBridge.cs`: contrato intacto. El bridge handler internamente decide path Fase 1 vs Fase 2 por settings, no por nuevo metodo.
- Frontend: NO se toca Fase 2. UI = Fase 3.

---

## T5. Diagrama del flow nuevo emision NC parcial real

### T5.1 Flow normal (casos 1, 2, 3, 5, 6, 8 — kind `PartialOnOriginal`)

```
[Admin aprueba PartialCreditNoteApproval via endpoint REST]
                |
                v
ApprovalRequestService.ApproveAsync (FC1.2 patron + bridge call FC1.3.4)
                |
                v
PartialCreditNoteApprovalBridge.OnApprovedAsync  (BookingCancellationService:1300+)
                |
                v  [verificar idempotencia + 4-eyes + GR-005 bypass]
                |
                v
       settings.EnablePartialCreditNoteRealEmission?
        / NO                                  \ SI
       v                                       v
  [Fase 1 fallback]                       [Fase 2 real]
   - log warning                            1. Cargar items factura origen
   - EnqueueAnnulmentAsync                  2. Filtrar IsRefundable=false segun flags
     (NC TOTAL real)                        3. Construir FiscalLiquidationInput
   - bc.Status =                               con Lines prorrateados
     AwaitingFiscalConfirmation             4. Re-validar suma INV-FC1.3-005
   - MarkConsumed                              sobre bc.FiscalLiquidation persistido
                                            5. _invoiceService.EnqueuePartialCreditNoteAsync(
                                                originalInvoiceId,
                                                liquidationInput,
                                                userId, userName, reason,
                                                approvalRequestId, ct)
                                                  |
                                                  v
                                            InvoiceService.EnqueuePartialCreditNoteAsync
                                              - Validar AnnulmentStatus
                                              - Validar IsSupportedForAnnulment
                                              - invoice.AnnulmentStatus = Pending
                                              - SaveChanges
                                              - Hangfire Enqueue ProcessPartialCreditNoteJob
                                                  |
                                                  v
                                            ProcessPartialCreditNoteJob (background)
                                              - Reload invoice + Reserva
                                              - cbteTipo = mapNcType(original)
                                              - lines = liquidation.Lines
                                              - IVA prorrateo segun
                                                settings.IvaProrrateoMode
                                              - validacion defensiva
                                                ABS(ImpTotal - (Neto+IVA+Trib)) <= tolerance
                                              - request = CreateInvoiceRequest {
                                                  ReservaId, OriginalInvoiceId,
                                                  IsCreditNote=true, Items=lines,
                                                  MonId, MonCotiz (F2.5)
                                                }
                                              - newInvoice = _afipService.CreatePendingInvoice(...)
                                              - _afipService.ProcessInvoiceJob(newInvoice.Id)
                                                  |
                                                  v
                                            ProcessInvoiceJob (AfipService:729+)
                                              - Build SOAP XML con <CbtesAsoc>
                                                (codigo existente AfipService:827-838)
                                              - POST a WSFE
                                              - Si CAE: invoice.Resultado=A
                                              - Si rechaza: Resultado=R + log
                                                  |
                                                  v
                                            (callback FC1.2 IInvoiceAnnulmentBcBridge
                                             ya existe, dispara cascade BC ->
                                             AwaitingFiscalConfirmation + receipt void
                                             cascade y todo el flow FC1.2 sigue normal)
                                           - MarkConsumed approval
```

### T5.2 Flow dual (casos 4 y 7 — kind `TotalPlusNewInvoice`, gated por F2.4)

```
[Admin aprueba con flag F2.4 ON]
                |
                v
OnApprovedAsync (rama TotalPlusNewInvoice)
                |
                v
  Step 1: EnqueueAnnulmentAsync (NC TOTAL existente, FC1.2 path)
                |
                v
  bc.Status = PartialFiscalAwaitingNewInvoice (12)
                |
                v
       [ARCA procesa NC en background]
                |
                v
       Invoice.AnnulmentStatus = Succeeded (FC1.2 callback)
                |
                v
       [FC13Phase2DualFlowJob cron 5min detecta]
                |
                v
  Step 2: _invoiceService.EnqueueNewInvoiceForRemainder(bc)
       - request = CreateInvoiceRequest sin OriginalInvoiceId,
         conceptos = ["Refacturacion post cancelacion Fc {N}"]
         Total = bc.FiscalLiquidation.FinalNetInvoiced
       - newPostInvoice = CreatePendingInvoice + ProcessInvoiceJob
                |
                v
       [ARCA procesa factura nueva]
                |
                v
       newPostInvoice.Resultado = A
                |
                v
  bc.PostCancellationInvoiceId = newPostInvoice.Id
  bc.Status = AwaitingFiscalConfirmation
                |
                v
       [Si step 2 falla en ARCA -> bc se queda en estado 12 +
        job sigue reintentando. Endpoint force-step2 con
        InvariantOverride para casos extremos.]
```

---

## T6. Feature flags + rollback estrategia

### T6.1 Tabla de flags Fase 2

| Flag | Default prod | Que hace si OFF | Que hace si ON |
|---|---|---|---|
| `EnableNewCancellationFlow` (FC1.2, existente) | `false` (hasta signoff OPS-FISCAL-001) | Kill switch FC1.2. | FC1.2 vigente. |
| `EnablePartialCreditNotes` (FC1.3 Fase 1, existente) | `false` | Calculator no corre. | Calculator + estados manual review activos. |
| **`EnablePartialCreditNoteRealEmission`** (FC1.3 Fase 2, nuevo) | **`false`** | Fallback FC1.2 con log warning (comportamiento Fase 1 actual). | Emite NC parcial REAL al ARCA. |
| **`EnableTotalPlusNewInvoiceAutoProcessing`** (FC1.3 Fase 2 sub-fase F2.4) | **`false`** | Casos 4/7 tiran `InvalidOperationException` (GR-001 vigente). | Flow dual step1+step2 activo. |
| **`IvaProrrateoMode`** (FC1.3 Fase 2 F2.0) | `ProportionalToNet` | — | — |
| **`PartialCreditNoteRoundingTolerance`** (FC1.3 Fase 2 F2.0) | `0.01` | — | — |

### T6.2 Combinaciones validas

| FC1.2 | FC1.3 F1 | FC1.3 F2 emision | FC1.3 F2 dual flow | Resultado |
|---|---|---|---|---|
| ON | ON | OFF | OFF | **Estado actual post-Fase 1**. Calculator + manual review + fallback NC total. |
| ON | ON | ON | OFF | NC parcial real al ARCA para casos 1/2/3/5/6/8. Casos 4/7 throw. |
| ON | ON | ON | ON | Flow completo Fase 2. Casos 4/7 flow dual. |
| ON | ON | OFF | ON | **RECHAZO startup** (F2.4 requiere F2 emision). |
| cualquiera | OFF | ON | cualquiera | **RECHAZO startup** (F2 requiere F1). |

### T6.3 Secuencia de prendido recomendada

1. **Pre-condicion**: Fase 1 ya esta ON en staging y prod (post merge de Fase 1, sin incidentes durante N dias).
2. **Mergeamos PR Fase 2** con TODOS los flags F2 en `false`. App arranca normal. Sin cambio de comportamiento.
3. **Staging**: admin prende `EnablePartialCreditNoteRealEmission=true`. Probar contra ARCA homologacion con casos 1, 2, 3, 5, 6, 8. Casos 4/7 siguen tirando — OK (esperado).
4. **Staging duracion**: 1-2 ciclos de cancelaciones reales (segun decida Gaston).
5. **Prod**: admin prende `EnablePartialCreditNoteRealEmission=true`. Validar primeras 5 NCs parciales reales manualmente con contador.
6. **Mes despues**: si volumen de casos 4/7 lo justifica, prender `EnableTotalPlusNewInvoiceAutoProcessing=true` en staging primero.

### T6.4 Rollback granular

| Escenario | Accion | Impacto |
|---|---|---|
| NC parcial real explota (ARCA rechaza por XML invalido, prorrateo IVA mal, etc.) | Apagar `EnablePartialCreditNoteRealEmission`. | Vuelve al fallback FC1.2 con log warning. NCs ya emitidas en ARCA quedan validas (no revertibles desde nuestro lado). Los BC nuevos siguen pasando por manual review pero al aprobar emiten NC total. |
| Flow dual explota | Apagar `EnableTotalPlusNewInvoiceAutoProcessing`. | Casos 4/7 vuelven a tirar throw. BCs que ya estaban en `PartialFiscalAwaitingNewInvoice (12)` quedan en limbo — admin los maneja manualmente fuera del sistema o con endpoint force-step2 una vez que el bug este fixeado. |
| Settings F2 entran con valores invalidos por restore de backup | Validacion startup detecta + app no arranca. Operador debe ajustar manualmente la BD antes de re-arrancar. | Defense-in-depth, mismo patron GR-002. |
| Reverse migraciones Fase 2 | Fase2.M2 -> M1 -> M0. Cada reverse es aditivo -> nullable, no pierde data. **Antes de reverse M1**: script que valida que no hay BCs en estados 9/10 con `FiscalLiquidation_FiscalAmountToCredit IS NOT NULL` y campos faltantes en `ApprovalRequest.Metadata` (porque despues del reverse, la unica fuente de verdad vuelve a ser Metadata). | Bajo riesgo si Metadata sigue intacto. |

---

## T7. Decisiones rechazadas con justificacion

### T7.1 ADR nuevo (ADR-010) vs extension de ADR-009: **extension preferida**

**Decision recomendada**: extender ADR-009 con una seccion `§12 - Fase 2 Amendments`. NO crear ADR-010.

**Justificacion**:

- Los cambios Fase 2 son **aditivos sobre el diseño Fase 1** sin alterar las decisiones estructurales:
  - `FiscalLiquidation` como Owned VO ya estaba **proyectado** en ADR-009 §2.4 ("Fase 2 introduce persistencia completa cuando AfipService emita NC parcial real").
  - `EnqueuePartialCreditNoteAsync` es un metodo nuevo en una interfaz existente — extension natural.
  - Los flags F2 son aditivos al patron GR-002 ya documentado.
  - `PartialFiscalAwaitingNewInvoice (12)` es un nuevo estado dentro del enum existente.
- Crear ADR-010 separado **duplicaria contexto** (habria que repetir "que es FC1.3", "que casos cubre", la matriz 8, las decisiones GR-001..GR-006) sin agregar claridad.
- El reviewer puede leer Fase 1 + §12 Fase 2 amendments en un solo documento y mantener trazabilidad.

**Cuando si crearia ADR-010**:

- Si decidiera cambiar `FiscalLiquidation` de Owned VO a entidad propia (cambio estructural).
- Si cambiara el contrato de `IPartialCreditNoteApprovalBridge` (cambio breaking).
- Si introdujera un nuevo aggregate (ej. `ProvincialTaxAdjustment`).
- Ninguno de estos esta en Fase 2.

**Contenido sugerido de `§12 Fase 2 Amendments` para ADR-009**:

1. `§12.1` — Owned VO `FiscalLiquidation` (promueve §2.4): 10 columnas + CHECK suma + backfill.
2. `§12.2` — `EnqueuePartialCreditNoteAsync` + `ProcessPartialCreditNoteJob` (aditivo a `IInvoiceService`).
3. `§12.3` — `IvaProrrateoMode` enum + default + comportamiento.
4. `§12.4` — Estado `PartialFiscalAwaitingNewInvoice (12)` + flow dual gated por `EnableTotalPlusNewInvoiceAutoProcessing`.
5. `§12.5` — Multimoneda: el flag `MultiCurrency` cambia comportamiento (review manual -> auto) cuando F2 ON.
6. `§12.6` — 4 settings nuevos + validacion startup.
7. `§12.7` — Decisiones cerradas Fase 2 (mismo formato que GR-001..GR-006).
8. `§12.8` — Open questions de Fase 2 (no las del contador, las arquitectonicas).

### T7.2 Alternativas rechazadas dentro de Fase 2

| Alternativa | Por que NO | Que se eligio |
|---|---|---|
| **Promover `ApprovalRequest.Metadata` a `jsonb`** ahora | Fuera de scope. La migracion requiere backfill + cambios a queries que lean Metadata (FC1.2 ya usa Metadata). Si Fase 2 no necesita queries dentro del JSON, es trabajo sin valor. | Mantener `string`. Si Fase 3 o reporting lo requieren, se hace en una sesion dedicada. |
| **Persistir `FiscalLiquidation` como entidad propia** (no Owned VO) | YAGNI. Owned VO ya estaba proyectado por ADR-009 §2.4 y permite leer/editar como parte del aggregate BC sin tener que cargar otra tabla. No hay caso de uso de queries cross-BC que justifique entidad separada. | Owned VO con prefijo `FiscalLiquidation_*`. |
| **Tocar `AfipService` directamente con un nuevo metodo `CreatePartialCreditNote`** | El path `CreatePendingInvoice + ProcessInvoiceJob` ya soporta NC con `<CbtesAsoc>`. Duplicarlo introduce divergencia. | Reusar el path existente. La logica nueva vive en `InvoiceService.ProcessPartialCreditNoteJob` que arma el `CreateInvoiceRequest` correcto. |
| **Emitir multiples NCs parciales sucesivas (una por cuota operador)** | F2 contador todavia no respondio. Default conservador: una NC en T0. | Default una sola NC. Si contador pide sucesivas, sub-fase posterior. |
| **Soportar correccion de NC ya emitida (ND complementaria)** | F3 contador todavia no respondio. Disenar sin respuesta agregaria complejidad innecesaria. | Diferido a Fase 3 o backlog. Por ahora el admin corrige fuera del sistema. |
| **Hardcodear `IvaProrrateoMode` a `ProportionalToNet`** | Si contador pide `PerItem`, hay que reescribir. Setting es trivial. | Setting con default `ProportionalToNet`. |
| **No validar suma defensiva pre-envio ARCA** | Riesgo de mandar XML inconsistente al ARCA y obtener rechazo oscuro. | Validacion defensiva + tolerance configurable. |
| **Eliminar GR-001 (rechazar `TotalPlusNewInvoice`) completamente sin gated flag** | Casos 4/7 son sensibles. Sin flag, un bug del flow dual rompe operativa criticamente. | Gated por `EnableTotalPlusNewInvoiceAutoProcessing` default OFF. Permite rollback granular. |
| **Job F2.4 step 2 que reintenta indefinidamente** | Spam de notificaciones (mismo problema N-003 round 3 Fase 1). | Reusar el patron `BridgeReconciliationMaxRetries` Fase 1: contador + notify una vez + endpoint force con `InvariantOverride`. |
| **Modificar `EnqueueAnnulmentAsync` existente para parametrizar items** | Riesgo de regresion en FC1.2 (todo el path actual depende de "reconstruccion 1:1 desde items origen"). | Nuevo metodo `EnqueuePartialCreditNoteAsync` separado. Convivencia. |
| **Compartir `Tributes` de la factura origen 1:1 en la NC parcial** | Tributos provinciales (IIBB, etc.) tienen reglas de proration complejas que dependen de la jurisdiccion. Fase 2 NO maneja. | NC parcial Fase 2 emite SIN tributos. Si la factura origen los tenia, queda un gap (la NC fiscal no acredita IIBB). OQ-3 abajo. |

---

## T8. Open questions / dependencias del contador

**Importante**: estas son las open questions de **arquitectura tecnica**. Las del contador (12 puntos round 3) ya estan en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md` y se mapean a F1..F4 de §T1.3.

### OQ-1: Verificar comportamiento ARCA con multiples NCs parciales sobre misma factura origen

**Pregunta**: el ARCA acepta NC1 + NC2 + NC3 todas con el mismo `<CbtesAsoc>` apuntando a la factura origen? O rechaza la segunda por "ya tiene NC asociada"?

**Bloqueante de**: caso "operador devuelve en N cuotas" si el contador termina pidiendo NCs sucesivas en F2.

**Plan**: probar en ARCA homologacion **antes de prender Fase 2 en prod**. Si rechaza, default "una sola NC en T0" se mantiene como regla unica.

### OQ-2: `IvaProrrateoMode = ProportionalToNet` con multiples alicuotas mezcladas — colapsa a dominante o preserva todas?

**Pregunta**: si la factura origen tiene items con 10.5% Y items con 21%, el modo `ProportionalToNet` deberia:
- (a) Colapsar a la alicuota dominante (la de mayor `Total`) — mas simple, IVA "aproximado".
- (b) Preservar las dos alicuotas con prorrateo proporcional por grupo — mas fiel, IVA exacto.

**Mi propuesta**: (b) **preservar todas** porque la fidelidad fiscal pesa mas que la simpleza. El modo `PerItem` es un super-set de (b) que ademas usa la alicuota de cada linea individual.

**Plan**: implementar (b) como default. Documentar en doc trainee. Si contador pide (a), agregar tercer valor al enum `IvaProrrateoMode.ProportionalToNetDominantAlicuota`.

### OQ-3: Tributos provinciales (IIBB) en NC parcial

**Pregunta**: la factura origen tenia $X de IIBB Capital. La NC parcial fiscal debe acreditar proporcional ($X * fraccion)?

**Mi posicion**: **NO en Fase 2**. Las reglas de prorrateo de tributos provinciales requieren saber:
- Si la jurisdiccion permite NC sobre IIBB ya percibido.
- Si requiere reporte separado al fisco provincial.
- Si la NC al cliente refleja IIBB o solo IVA federal.

Esto es **fuera de scope de FC1.3** completo (Fase 1, 2, 3). Si el contador insiste, agregar como `OQ-tributos` para Fase 4 separada.

**Mitigacion**: log warning explicito en `ProcessPartialCreditNoteJob` cuando `original.Tributes.Any()` indicando "NC parcial no prorratea tributos provinciales". Admin maneja fuera del sistema.

### OQ-4: Que pasa con `Receipt` (recibos de pago) asociados a la factura origen cuando emitimos NC parcial?

**Hoy** (FC1.2 path): `ApplyCreditNoteEconomicReversalAsync` (`AfipService:1006`) crea un `Payment` reversal y cascade-voida los receipts. Esto asume NC total.

**Para NC parcial**: solo se reversa una fraccion del pago. Los receipts pueden quedar parcialmente activos.

**Mi propuesta**: Fase 2 NO modifica esa logica. El reversal sigue siendo "por total de la NC nueva" (que es parcial). Verificar que `ApplyCreditNoteEconomicReversalAsync` ya usa `invoice.ImporteTotal` (la NC nueva, no la origen) para el monto — si si, el reversal sera parcial naturalmente. Si no, ajustar.

**Plan**: leer `ApplyCreditNoteEconomicReversalAsync` durante implementacion F2.3 y agregar test integration `OnApprovedAsync_Fase2_RealEmission_ReversesOnlyFiscalAmount`.

### OQ-5: Comision vendedor sobre `FinalNetInvoiced` (G6 cerrada Fase 1)

**Pregunta**: cuando la NC parcial real se emite, donde se dispara el ajuste de comision vendedor sobre `FinalNetInvoiced`?

**Mi propuesta**: NO en Fase 2. La comision es un calculo del modulo de comisiones (fuera de FC1.3). Esa parte lee `bc.FiscalLiquidation.FinalNetInvoiced` cuando le toca recalcular. Fase 2 garantiza que `FinalNetInvoiced` esta persistido y disponible — el resto es trabajo de otro modulo.

**Plan**: agregar nota en doc trainee.

---

## T9. Trazabilidad

- **ADR base**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-009-partial-credit-note.md` (Fase 2 sera extension `§12`).
- **Plan tactico Fase 1**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-3.md`.
- **Doc cierre Fase 1**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-22-fc1-3-fase-1-implementacion-completa.md`.
- **Mensaje contador round 3**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md` (F1..F4 son las preguntas que ajustan Fase 2).
- **Criterio contador NC parcial**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` (matriz 8 casos).
- **Patron `EnqueueAnnulmentAsync`** (FC1.2 vigente, base de la replica F2.2): `src\TravelApi.Infrastructure\Services\InvoiceService.cs:469-583`.
- **Patron `ProcessAnnulmentJob`** (FC1.2 vigente, base de `ProcessPartialCreditNoteJob`): `src\TravelApi.Infrastructure\Services\InvoiceService.cs:585+`.
- **Patron `CreatePendingInvoice` con `<CbtesAsoc>`**: `src\TravelApi.Infrastructure\Services\AfipService.cs:564-713` (NC) y `AfipService.cs:827-838` (XML).
- **Patron Owned VO con prefijo**: `src\TravelApi.Infrastructure\Persistence\AppDbContext.cs` (`FiscalSnapshot` config, leido durante Fase 1).
- **Patron job de reconciliacion**: `ArcaAnnulmentReconciliationJob` (FC1.2) y `PartialCreditNoteBridgeReconciliationJob` (FC1.3 Fase 1).
- **Patron endpoint force con `InvariantOverride`**: ADR-009 §2.12 + `BookingCancellationService.cs:261-281`.
- **Patron counters Serilog**: FC1.2.7b en HEAD `5c60ce8`.
- **Patron validacion startup pre-condicion**: ADR-009 §2.10 GR-002 (validacion FC1.3 requiere FC1.2).

---

**Fin del plan tactico Fase 2.** Pendiente review de `software-architect-reviewer` para validar:

1. Decision T7.1 (extension ADR-009 vs ADR-010 nuevo).
2. Estrategia "defaults conservadores Fase 2 sin esperar contador" (T1.3).
3. Asunciones T1.4 (especialmente la 2 sobre multiples NCs con mismo `<CbtesAsoc>`).
4. Idempotencia de flow dual F2.4 (estado intermedio nuevo + job + endpoint force).
5. Backfill SQL desde `ApprovalRequest.Metadata` (F2.1) — especificamente el manejo de Metadata malformado.
