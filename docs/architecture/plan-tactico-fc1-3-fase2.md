# Plan tactico tecnico FC1.3 Fase 2 (NC parcial real al ARCA)

- **Version**: v5 firmada (post review `software-architect-reviewer` round 4 — Approved with Minors).
- **Fecha original**: 2026-05-22. **Editado**: 2026-05-22 (round 2), 2026-05-25 (round 3), 2026-05-26 (round 4), 2026-05-26 (v5 fixes post-round-4).
- **Autor**: `software-architect` agent + edits directos post-round-4.
- **Status**: **FIRMADO** (Approved with Minors round 4). Implementacion habilitada: F2.0 + F2.1.0 + F2.1 pueden mergear directo; los 3 fixes v5 (RH4-001, RH4-002, RH3-004) ya estan aplicados a este doc antes de iniciar F2.2.
- **Base**: Fase 1 mergeada (HEAD `06585e6`, 9 sub-fases, 66 unit tests verdes). Doc cierre Fase 1: `docs/explicaciones/2026-05-22-fc1-3-fase-1-implementacion-completa.md`.
- **ADR asociado**: extension del [ADR-009](adr/ADR-009-partial-credit-note.md) con `§12 Fase 2 Amendments` (NO ADR nuevo — decision validada por reviewer round 1).
- **Stack confirmado**: .NET 8 + EF Core 8.x + PostgreSQL via Npgsql 8.0.11 + xmin shadow concurrency token + `BusinessInvariantInterceptor` mapea `SqlState=23514` a `BusinessInvariantViolationException`. Verificado durante Fase 1.

> **Regla operativa Gaston aplicada**: este documento NO contiene estimaciones de horas. Las sub-fases estan listadas con dependencias + criterios de aceptacion verificables. La cadencia real depende del orden en que el subagente backend las tome.

## Changelog v4 -> v5 (post round 4, 2026-05-26)

Esta version cierra los 3 hallazgos minor pendientes del review round 4 (Approved with Minors): RH4-001 (MAJOR scoped a F2.2), RH4-002 (MINOR), RH3-004 (NIT). Aplicados con edits directos.

1. **RH4-001 [MAJOR scoped F2.2]**: el snapshot del numerador ARCA (`LastSeenNumeroBeforePost`) ahora se captura DENTRO de `ProcessPartialCreditNoteJob` como primera operacion, ANTES del INSERT en `ArcaIdempotencyKeys`. La v4 lo capturaba en `EnqueuePartialCreditNoteAsync` (encolado), lo cual permitia que el numerador ARCA avanzara por otros emisores entre encolado y ejecucion del job. Sub-tarea A.7 reescrita + nuevo test `ProcessPartialCreditNoteJob_SnapshotCapturedAtJobStartNotAtEnqueueTime`.
2. **RH4-002 [MINOR]**: sub-tarea A.3.1 ahora documenta el contrato defensivo del array `CbtesAsoc` del response `FECompConsultar`: 1 item -> mapear, 0 items -> null, >1 items -> log warning + null (no elegir uno, ARCA podria devolver toda la cadena de asociaciones en versiones futuras). Nuevo test `GetVoucherDetails_MultipleCbtesAsoc_LogsWarningAndReturnsNullCbteAsoc`.
3. **RH3-004 [NIT]**: nueva sub-tarea A.7.0 que crea helper publico `GetLastAuthorizedNumeroAsync(int puntoVenta, int cbteTipo, CancellationToken ct)` en `IAfipService`. Encapsula la carga de `AfipSettings` adentro del servicio para evitar exponer `GetNextVoucherNumber` privado (que requiere `AfipSettings` como param). Usado por la sub-tarea A.7 y por `QueryLastAuthorizedWithDetailsAsync` internamente.

---

## Changelog v3 -> v4 (round 4, 2026-05-26)

Esta version cierra 1 bloqueante (RH3-001) + 2 minors (RH3-002, RH3-003) + 1 info (RH2-008) del reviewer round 3. Ver §T10.5 al final para la tabla detallada de cierre.

Cambios principales:

1. **RH3-001 [BLOQUEANTE]**: capa 1.5 stale key recovery del plan v3 invocaba un metodo `_afipService.QueryLastAuthorizedAsync(...)` y un shape `arcaResult.{Found, CbteAsoc, IssuedAt, Cae}` que **no existian** en el codigo. Verificacion: `IAfipService.cs` no expone `QueryLastAuthorizedAsync`; `AfipService.GetNextVoucherNumber:1152` es private y devuelve `int`; `AfipService.GetVoucherDetails:1195` retorna `AfipVoucherDetails` que solo tiene `ImporteTotal/Neto/Iva/Trib + VatDetails + TributeDetails` (sin `Cae`, `CbteAsoc`, `IssuedAt`). Gaston eligio **Camino A (robusto)**: crear DTO + metodo + columna nuevos. Ver §FC1.3.F2.2 capa 1.5 reescrita + §T4.1 (DTO nuevo) + §T4.2 (extension `IAfipService` + `AfipService` + `Invoice` con `LastSeenNumeroBeforePost` en `ArcaIdempotencyKeys`).
2. **RH3-002 [MINOR]**: pre-refactor F2.5 ahora arranca con una tarea atomica 0 de auditoria `grep -n "MonId\|MonCotiz"` sobre `AfipService.cs`. Verificado: hoy las ocurrencias estan en las lineas 879-880 (envelope FECAESolicitar de `ProcessInvoiceJob`). Si el grep aparece en otro lado, integrarlo al refactor. Criterio de cierre: grep post-refactor con cero ocurrencias hardcoded.
3. **RH3-003 [MINOR]**: backfill paso 5.B ahora lee `bc."LiquidationComputedAt"` directamente en lugar de `(m.meta->>'computedAt')::timestamptz`. Elimina el riesgo de divergencia por serializacion JSON. El CHECK `chk_BookingCancellations_fiscalliquidation_consistency` queda como igualdad exacta sin tolerancia.
4. **RH2-008 [INFO]**: justificacion §FC1.3.F2.6a reescrita. `ArcaAnnulmentReconciliationJob` **NO existe como clase implementada** (verificado con `Grep "class ArcaAnnulmentReconciliationJob"` -> 0 matches; solo aparece como comentario aspiracional en `Invoice.cs:62` y en migracion legacy). El job que SI existe y SI funciona es `PartialCreditNoteBridgeReconciliationJob` (replicado por Fase 2). Deuda historica documentada como fuera de scope Fase 2.

Nuevos elementos en v4 derivados de los fixes:

- DTO nuevo `ArcaCompoundQueryResult` en `src/TravelApi.Application/DTOs/AfipDtos.cs` (§T4.1).
- Metodo nuevo `QueryLastAuthorizedWithDetailsAsync` en `IAfipService` + implementacion en `AfipService` (§T4.2).
- Extension del SOAP parsing de `GetVoucherDetails` para sumar `Cae`, `CbteAsoc`, `CbteFch -> IssuedAt`, `MonId`, `MonCotiz` al DTO `AfipVoucherDetails` como campos OPCIONALES nullable (§T4.2 + sub-tarea F2.2).
- Columna nueva `ArcaIdempotencyKeys.LastSeenNumeroBeforePost int NULL` (sumada a migracion M1, §T4.1).
- Counter nuevo Serilog `Fc13.PartialCreditNote.RecoveredFromStaleKey` (§FC1.3.F2.6).
- 3 tests integration nuevos: `ProcessPartialCreditNoteJob_KeyOrphanedPostNeverArrived_DeletesKeyAndRetries`, `ProcessPartialCreditNoteJob_KeyOrphanedPostArrived_RecoversFromArca`, `ProcessPartialCreditNoteJob_KeyOrphanedMismatchAmount_TreatsAsPostNeverArrived`.

---

## Changelog v2 -> v3 (round 3)

Esta version cierra 2 bloqueantes (RH2-001, RH2-002) + 2 majors (RH2-003, RH2-004) + 3 minors (RH2-005, RH2-006, RH2-007) del reviewer round 2. Ver §T10.4 al final para la tabla detallada de cierre.

Cambios principales:

1. **RH2-001 [BLOQUEANTE]**: query SQL §FC1.3.F2.4.0 reescrita usando `bc.CreditNoteKind` directamente. La version v2 usaba bitflags `("ReviewRequiredReason" & 32) <> 0 AS is_case_4` que sobrecuenta porque el calculator (`FiscalLiquidationCalculator.cs:417-440`) clasifica con prioridad estricta — `CustomerIsRiOrFacturaA` gana sobre `OriginalInvoiceUnclear` y `RetentionChangesNature`. Una BC con Factura A + RetentionChangesNature se clasifica como Case 8 pero la query la contaba como caso 7. La discriminacion correcta es por kind persistido.
2. **RH2-002 [BLOQUEANTE]**: §FC1.3.F2.5 multimoneda expandida con plan de refactor concreto del XML SOAP. Se suman columnas `Invoice.MonId nvarchar(3) DEFAULT 'PES'` + `Invoice.MonCotiz numeric(18,6) DEFAULT 1` a la migracion M1, `CreatePendingInvoice` las puebla, `CreateInvoiceRequest` gana 2 props opcionales, y `ProcessInvoiceJob` lee de la `Invoice` para interpolar. La v2 decia "extraer a `request.MonId` + `request.MonCotiz`" sin tocar el XML hardcoded en `AfipService.cs:879-880` — ahora hay plan completo.
3. **RH2-003 [MAJOR]**: §FC1.3.F2.3 punto 5 discrimina NC total vs parcial usando `bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal` como criterio **primario**, con fallback historico por monto solo cuando el BC no esta asociado (NCs pre-FC1.3). La v2 dejaba el kind como "opcion mas robusta" alternativa.
4. **RH2-004 [MAJOR]**: §FC1.3.F2.2 punto 4 completa la idempotencia con "stale key recovery" — si `ArcaIdempotencyKeys` tiene un INSERT con `ResolvedAt IS NULL` mas viejo que `IdempotencyKeyStaleThresholdMinutes` (default 10 min), el reintento consulta `FECompUltimoAutorizado` para decidir si recuperar (POST viajo) o limpiar key + reintentar (POST nunca viajo). La v2 dejaba un limbo permanente con `AnnulmentStatus = Failed` en ese escenario.
5. **RH2-005 [MINOR]**: ruta del job nuevo migrada de `Infrastructure/Jobs/` a `Infrastructure/Services/` para alinear con la convencion existente (`PartialCreditNoteBridgeReconciliationJob` esta en `Services/`).
6. **RH2-006 [MINOR]**: §FC1.3.F2.3 punto 5 + audit `PartialCreditNoteEconomicReversalNoCascade` ahora incluyen la query de receipts vivos especificada (filtrando por `r.Payment.RelatedInvoiceId == invoice.OriginalInvoiceId && r.Status == PaymentReceiptStatuses.Issued`). La v2 prometia `ReceiptsAffected = [...]` sin decir como cargarlos.
7. **RH2-007 [INFO]**: §T8 corrige "12 puntos round 3" a "5 preguntas fiscales (F1..F5) + 8 confirmaciones profesionales + 1 confirmacion G4 = 14 puntos" alineado con `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md` (F5 sumada round 2).

Nuevos elementos en v3 derivados de los fixes:

- Setting `IdempotencyKeyStaleThresholdMinutes` (default 10) en `OperationalFinanceSettings` (§FC1.3.F2.0).
- Columnas `Invoice.MonId` + `Invoice.MonCotiz` en migracion Fase2.M1 (§FC1.3.F2.1 + §FC1.3.F2.5).
- DTO `CreateInvoiceRequest` gana props opcionales `MonId` (default `"PES"`) y `MonCotiz` (default `1`) (§FC1.3.F2.5).
- 2 tests integration de regresion FC1.2: `Fc12NormalInvoice_StillEmitsWithPesos`, `Fc12Annulment_StillEmitsWithPesos`.
- 1 test integration nuevo: `ProcessPartialCreditNoteJob_KeyOrphanedAfterCrash_AllowsRetryAfterStaleThreshold`.

---

## Changelog v1 -> v2 (round 2)

Esta version cierra 6 bloqueantes (RH-001..RH-006) + 4 majors + 5 minors del reviewer round 1, mas 3 decisiones nuevas G-F2-A/C/D de Gaston. Ver §T10 al final para la tabla detallada de cierre.

Cambios principales:

1. **G-F2-A**: F2.4 (flow dual `TotalPlusNewInvoice`) pasa a **GATED por criterio cuantitativo post-prod**. Si casos 4 y 7 son < 5% del volumen total de cancelaciones, queda en backlog. Si >= 5%, se hace en sesion separada. Ver §FC1.3.F2.4 reescrita.
2. **G-F2-C**: tributos provinciales (`Invoice.Tributes.Any() == true`) ahora son **disparador de manual review obligatorio**. Nuevo flag `ReviewRequiredReason.HasProvincialTributes = 1 << 11`. Cierra OQ-3 v1.
3. **G-F2-D**: multi-recibos es **comun en MagnaTravel** (no raro). El cascade de receipts en NC parcial NO es trivial. Politica: dejar todos los receipts vivos + audit explicito + UI Fase 3 permitira marcar manualmente. Ver §FC1.3.F2.3 punto 6 (nuevo) + §T8 OQ-4 actualizada.
4. **RH-001**: backfill F2.1 ahora tiene pre-step `F2.1.0` con script de validacion sobre dump staging/prod ANTES de la migracion + dos pasos en la migracion con abort defensivo.
5. **RH-002**: el caller persiste doble representacion (Metadata JSON + columnas dedicadas) **obligatoriamente**, sin opcion de skip. Test integration `Confirm_PostFase2_BothRepresentationsMatch`.
6. **RH-003**: eliminada referencia incorrecta a "Factura A MiPyME (51) -> NC tipo 53". Tipo 51 es Factura M, **fuera de scope Fase 2**.
7. **RH-004**: idempotencia ARCA pos-reintento Hangfire via `IdempotencyKey` persistido pre-POST + chequeo `FECompUltimoAutorizado` opcional.
8. **RH-005**: `OnApprovedAsync` distingue NC total vs NC parcial — NC parcial NO hace cascade receipts (alineado con G-F2-D).
9. **RH-006**: job `PartialCreditNotePostingReconciliationJob` es **NUEVO**, no extension. Patron del job existente `PartialCreditNoteBridgeReconciliationJob`.

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
3. **Multimoneda factura USD pagada en ARS**: hoy `FiscalSnapshot.ExchangeRateAtOriginalInvoice` se carga al confirmar (verificado FC1.2). Asumo que ARCA acepta NC en USD con `MonId=DOL` + `MonCotiz` igual al TC del momento del **comprobante original**, no al TC del dia de la NC. Esto es regla fiscal estandar pero a confirmar con contador (pregunta F5 ya sumada al mensaje round 3, ver `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md`).
4. **Casos `TotalPlusNewInvoice` (4 y 7)**: **G-F2-A round 2 formaliza esta asuncion como GATE cuantitativo**. La query SQL §FC1.3.F2.4.0 se corre post-prod (al menos 30 dias despues de prender Fase 2). Si `casos_4_y_7 / total >= 5%`, sesion separada para F2.4. Si `< 5%`, queda en backlog indefinidamente. F2.4 NO entra al PR Fase 2 base — solo el flag `EnableTotalPlusNewInvoiceAutoProcessing` se crea (default OFF, validacion startup garantiza que no se puede activar sin codigo merged).

---

## T2. Orden + dependencias entre sub-fases

```
FC1.3.F2.0  Settings nuevos + flag maestro Fase 2 (default OFF)         ─┐
            (EnablePartialCreditNoteRealEmission default false,           │
             EnableTotalPlusNewInvoiceAutoProcessing default false [GATED]│
             IvaProrrateoMode default ProportionalToNet,                   │
             PartialCreditNoteRoundingTolerance default 0.01)             │
                                                                          │
            Migracion Fase2.M0 + extension OperationalFinanceSettings     │
            + validacion startup (pre-condicion: si F2.flag ON, F1.flag    │
            tambien ON). Mismo patron GR-002.                              │
                                                                          │
            G-F2-C: nuevo flag bitwise ReviewRequiredReason                │
              HasProvincialTributes = 1 << 11                              │
                                                                ─────────┤
                                                                          │
FC1.3.F2.1.0 Pre-step VALIDACION script (NO migracion).                   │ depende F2.0
            Script SQL standalone que cuenta filas tipo 11                 │
              con Metadata vacio, malformado o claves criticas faltantes.  │
            Correr contra dump staging Y prod. Si count > 0,               │
              revisar caso a caso ANTES de avanzar a F2.1 real.            │
            Output: count + lista filas (id + razon).                      │
                                                                ─────────┤
                                                                          │
FC1.3.F2.1  Persistir FiscalLiquidation completo                          │ depende F2.1.0
            (Owned VO en BookingCancellation, 10 columnas prefijo          │
             FiscalLiquidation_*, CHECK suma componentes,                  │
             backfill desde ApprovalRequest.Metadata).                     │
                                                                          │
            Migracion Fase2.M1 con DOS PASOS atomicos:                     │
              Paso A: UPDATE solo filas validas + RAISE NOTICE count       │
                       de filas saltadas + ABORT si count > 0.             │
              Paso B: tests integration pos-migracion.                     │
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
FC1.3.F2.4  [GATED — G-F2-A]                                              │ depende F2.3
            Caso TotalPlusNewInvoice (casos 4 y 7) — flow dual.            │ (gated)
            (a) Anular factura original (NC total existente).              │
            (b) Emitir factura nueva por el remanente conceptual           │
                (FinalNetInvoiced).                                        │
            Idempotencia critica: si (a) sale OK y (b) falla, BC queda    │
            en estado intermedio nuevo PartialFiscalAwaitingNewInvoice.   │
            Job de reconciliacion + endpoint admin force-continue.        │
            Gated por EnableTotalPlusNewInvoiceAutoProcessing (default OFF)│
                                                                          │
            CRITERIO DE ACTIVACION (G-F2-A):                               │
              Post-prod de F2.3, correr query SQL §FC1.3.F2.4.0.           │
              Si casos 4+7 / total cancelaciones >= 5% -> sesion separada │
                hace F2.4.                                                 │
              Si < 5% -> queda en backlog indefinidamente.                 │
              Flag siempre default OFF — casos 4/7 siguen                  │
                rechazandose con InvalidOperationException (GR-001).       │
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
FC1.3.F2.6  Observabilidad + counters Serilog.                            │ depende F2.2
            Counters Serilog (mismo patron FC1.2.7b).                     │
                                                                ─────────┤
                                                                          │
FC1.3.F2.6a Job NUEVO PartialCreditNotePostingReconciliationJob.          │ depende F2.2
            (NO extension del ArcaAnnulmentReconciliationJob — el job      │
            existente reconcilia NC TOTAL via AnnulmentStatus,             │
            no aplica al modelo NC parcial que persiste el resultado       │
            ARCA en Invoice.Resultado de la NC nueva).                     │
            Patron: replicar PartialCreditNoteBridgeReconciliationJob     │
            (cron, contador anti-spam, max retries antes de notify).       │
            Scope: Invoice con OriginalInvoiceId IS NOT NULL +             │
              Resultado = 'PENDING' por mas de N min.                      │
            Si pos prendido Fase 2 se detectan NCs FC1.2 huerfanas         │
              (anteriores, sin reconciliacion automatica), sub-fase        │
              EXPLICITA F2.6b (no se asume).                               │
                                                                ─────────┤
                                                                          │
FC1.3.F2.7  Doc explicativo trainee + actualizacion MEMORY.md.            │ depende todo
            docs/explicaciones/YYYY-MM-DD-fc1-3-fase-2-implementacion.md   │
            Cierre formal Fase 2 + apuntador a Fase 3 (UI).                │
                                                                ─────────┘
```

### T2.1 Dependencias entre sub-fases (resumen)

- **F2.0** es prerequisito de todo.
- **F2.1.0** (pre-step script validacion) corre ANTES de F2.1 contra dump staging/prod. Es un script SQL standalone, NO migracion.
- **F2.1** (persistencia + backfill) habilita **F2.2** (InvoiceService) porque el job necesita leer los montos de BD, no de Metadata JSON.
- **F2.2** habilita **F2.3** (BC service llama al nuevo metodo) y **F2.5** (multimoneda usa el mismo flow).
- **F2.4** depende de **F2.3** + criterio cuantitativo G-F2-A. Solo se hace si volumen >= 5%.
- **F2.6** (counters Serilog) depende de **F2.2** porque cuenta lo que emite.
- **F2.6a** (job NUEVO de reconciliacion) depende de **F2.2** porque persiste la NC nueva.
- **F2.7** cierra cuando todo lo demas paso.

### T2.2 Orden recomendado de merge

`F2.0` -> `F2.1.0 (script standalone)` -> `F2.1` -> `F2.2` -> `F2.3` -> `F2.6` -> `F2.6a` -> `F2.5` -> `F2.7` -> [auditoria post-prod G-F2-A] -> `F2.4` opcional.

Razon: F2.4 (TotalPlusNewInvoice) queda fuera del PR Fase 2 base. Despues de prender Fase 2 en prod, se mide el volumen de casos 4 y 7. Si justifica, sesion separada.

**Nota round 3 (RH2-002)**: en v2 F2.5 figuraba como "ajuste de logica" de bajo riesgo. La expansion round 3 de F2.5 ahora toca el SOAP envelope (XML hardcoded en `AfipService.cs:879-880`) + suma columnas `Invoice.MonId/MonCotiz` + extiende el DTO `CreateInvoiceRequest`. Esto cambia el perfil de riesgo: F2.5 puede regresionar FC1.2 si los defaults no se aplican correctamente. Recomendacion: mergear F2.5 como sub-PR aparte (no en el mismo PR que F2.6/F2.6a) con los 2 tests de regresion FC1.2 (`Fc12NormalInvoice_StillEmitsWithPesos` + `Fc12Annulment_StillEmitsWithPesos`) corriendo en verde antes del merge.

---

## T3. Sub-fases atomicas con criterios de aceptacion

### FC1.3.F2.0 — Settings nuevos + flag maestro Fase 2

**Tareas atomicas**:

1. Extender `OperationalFinanceSettings` con 5 columnas nuevas (round 3 suma `IdempotencyKeyStaleThresholdMinutes`):
   - `EnablePartialCreditNoteRealEmission` (bool, default `false`). Master flag Fase 2. Si OFF, el flujo se comporta como Fase 1 (log warning + emite NC total via FC1.2 path).
   - `EnableTotalPlusNewInvoiceAutoProcessing` (bool, default `false`). Si OFF, los casos 4 y 7 siguen tirando `InvalidOperationException` (GR-001 vigente). Si ON, flow dual de F2.4.
   - `IvaProrrateoMode` (enum: `ProportionalToNet = 0` default, `PerItem = 1`). Configurable post-respuesta F1 contador.
   - `PartialCreditNoteRoundingTolerance` (`numeric(18,2)`, default `0.01`). Tolerancia para validacion `ImpTotal = ImpNeto + ImpIVA + ImpTrib` antes de mandar al ARCA. Si la suma se va por mas que este valor, throw + log error (defensivo, no enviar XML inconsistente). Expresado en la moneda original del comprobante (no necesariamente ARS).
   - **`IdempotencyKeyStaleThresholdMinutes`** (`int`, default `10`). **RH2-004 (round 3)**. Umbral en minutos a partir del cual una key sin resolver en `ArcaIdempotencyKeys` se considera huerfana (probable crash entre INSERT y POST) y dispara recovery via `FECompUltimoAutorizado`. Ver §FC1.3.F2.2 capa 1.5.

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

### FC1.3.F2.0.G-F2-C — Nuevo flag bitwise + calculator update

**Tareas atomicas** (cierra **G-F2-C**):

1. Sumar al enum `ReviewRequiredReason` (`src/TravelApi.Domain/Entities/ReviewRequiredReason.cs`) un valor nuevo:
   ```csharp
   /// <summary>
   /// G-F2-C (2026-05-22): la factura origen tiene Tributes.Any() == true (tributos
   /// provinciales tipo IIBB, percepciones de Capital, percepciones de provincia, etc.).
   /// El sistema FRENA la cancelacion auto y dispara manual review obligatorio porque
   /// el prorrateo de tributos provinciales NO esta modelado en Fase 2 (ver §T8 OQ-3).
   /// El admin debe revisar caso a caso y decidir si la NC parcial se emite manualmente
   /// con tributos prorrateados (fuera del sistema) o se reformula la operacion.
   /// </summary>
   HasProvincialTributes = 1 << 11,
   ```

2. Modificar `FiscalLiquidationCalculator` (implementado en Fase 1) para sumar la deteccion:
   - Al inicio del calculo (STEP 1, despues de cargar la factura origen), chequear `invoice.Tributes != null && invoice.Tributes.Any()`. Si true, agregar `ReviewRequiredReason.HasProvincialTributes` al flag acumulado.
   - El resto del calculo prosigue normal — el flag dispara manual review, NO la auto-emision.

3. **Cubre OQ-3 v1**: NC parcial con tributos provinciales SIEMPRE pasa por manual review. NO se emite automaticamente. El admin decide.

**Criterio de aceptacion**:

- Test unit `Calculator_InvoiceWithTributes_AddsHasProvincialTributesFlag`: factura con `Tributes = [IIBB Capital $100]` -> `ReviewRequiredReason.HasFlag(HasProvincialTributes) == true`.
- Test unit `Calculator_InvoiceWithoutTributes_DoesNotAddFlag`.
- Test integration `Confirm_InvoiceWithProvincialTributes_RoutesToManualReview`: BC con factura origen que tenia IIBB -> `bc.Status == ManualReviewPending` + `bc.ReviewRequiredReason` contiene `HasProvincialTributes`.
- Tests Fase 1 existentes siguen verdes (el flag nuevo es aditivo, no rompe ningun comportamiento).

**Dependencias**: ninguna (cambio aditivo en codigo, sin migracion).

---

### FC1.3.F2.1.0 — Pre-step validacion Metadata (cierra RH-001)

**Tareas atomicas**:

1. Crear script SQL standalone `tools/sql/fase2-m1-prevalidation-metadata.sql`. NO es migracion EF, NO se ejecuta como parte del deploy automatico. Output esperado: lista de filas `ApprovalRequest` tipo 11 que NO se pueden backfillear de forma segura.

   ```sql
   -- FC1.3.F2.1.0 — pre-validacion del backfill de FiscalLiquidation.
   -- Correr contra dump staging Y dump prod ANTES de aplicar Fase2.M1.
   -- Si el conteo de filas problematicas > 0, REVISAR CASO A CASO antes de avanzar.

   WITH ar11 AS (
       SELECT ar."Id", ar."Metadata"
       FROM "ApprovalRequests" ar
       WHERE ar."RequestType" = 11
   ),
   problematic AS (
       SELECT
           id,
           CASE
               WHEN "Metadata" IS NULL OR length(trim("Metadata")) = 0 THEN 'METADATA_VACIO'
               WHEN jsonb_typeof("Metadata"::jsonb) IS DISTINCT FROM 'object' THEN 'METADATA_NO_OBJETO'
               WHEN NOT ("Metadata"::jsonb ? 'originalInvoiceAmount') THEN 'FALTA_originalInvoiceAmount'
               WHEN NOT ("Metadata"::jsonb ? 'fiscalAmountToCredit') THEN 'FALTA_fiscalAmountToCredit'
               WHEN NOT ("Metadata"::jsonb ? 'currency') THEN 'FALTA_currency'
               WHEN NOT ("Metadata"::jsonb ? 'computedAt') THEN 'FALTA_computedAt'
               ELSE NULL
           END AS razon
       FROM ar11
   )
   SELECT id, razon
   FROM problematic
   WHERE razon IS NOT NULL
   ORDER BY id;
   ```

2. **Cobertura**: el script chequea cinco cosas:
   - Metadata vacio o null.
   - Metadata no es objeto JSON valido (intento de cast a `::jsonb` falla mas tarde).
   - Claves criticas faltantes (`originalInvoiceAmount`, `fiscalAmountToCredit`, `currency`, `computedAt`).
   - Las otras claves (`cancellationAmount`, `operatorPenaltyAmount`, etc.) NO son criticas porque la migracion las puede dejar null sin romper el CHECK suma (numericas nullable).

3. **Output esperado en dumps recientes** (Fase 1 acaba de mergear): cero filas problematicas, porque el serializer de Fase 1 escribe schemaVersion=1 con todas las claves. Si aparece algo, investigar caso a caso — puede ser un BC manualmente editado o un test que escapo a prod.

4. **Criterio de cierre F2.1.0**: ejecutar contra dump staging Y prod, conteo == 0 documentado. Si > 0, abrir tareas por cada fila para limpiar manualmente ANTES de avanzar a F2.1.

**Criterio de aceptacion FC1.3.F2.1.0**:

- Script existe en `tools/sql/fase2-m1-prevalidation-metadata.sql`.
- Documentado en `docs/operations/` el output de correrlo contra dump staging Y prod, con fecha + count + lista de IDs si los hay.
- Signoff explicito de Gaston de "count = 0, podemos avanzar".

**Dependencias**: FC1.3.F2.0 (porque ya hace falta el enum nuevo si encontramos algo y queremos retaggear).

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
   - CHECK adicional `chk_BookingCancellations_fiscalliquidation_consistency`: si `FiscalLiquidation_ComputedAt` no es null, debe coincidir **exactamente** con `LiquidationComputedAt` (columna summary Fase 1). Sin tolerancia. **RH3-003 (round 4)**: el backfill paso 5.B ahora lee `bc.LiquidationComputedAt` directamente (no del JSON Metadata), eliminando el riesgo de divergencia por serializacion de fechas. SQL exacto del CHECK:
     ```sql
     ALTER TABLE "BookingCancellations"
       ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_consistency
       CHECK (
         "FiscalLiquidation_ComputedAt" IS NULL
         OR "LiquidationComputedAt" = "FiscalLiquidation_ComputedAt"
       );
     ```

5. **Backfill SQL en dos pasos atomicos** (cierra **RH-001**). La migracion va dividida en bloques `DO $$ ... $$` para poder usar variables y `RAISE NOTICE`:

   **Paso 5.A — pre-check defensivo dentro de la migracion**:
   ```sql
   DO $$
   DECLARE
     v_problematic_count int;
   BEGIN
     SELECT COUNT(*) INTO v_problematic_count
     FROM "ApprovalRequests" ar
     WHERE ar."RequestType" = 11
       AND (
         ar."Metadata" IS NULL
         OR length(trim(ar."Metadata")) = 0
         OR jsonb_typeof(ar."Metadata"::jsonb) IS DISTINCT FROM 'object'
         OR NOT (ar."Metadata"::jsonb ? 'originalInvoiceAmount')
         OR NOT (ar."Metadata"::jsonb ? 'fiscalAmountToCredit')
         OR NOT (ar."Metadata"::jsonb ? 'currency')
       );

     IF v_problematic_count > 0 THEN
       RAISE EXCEPTION 'FC1.3.F2.1 backfill ABORTED: % ApprovalRequests tipo 11 con Metadata invalido o claves criticas faltantes. Correr tools/sql/fase2-m1-prevalidation-metadata.sql para identificar filas y limpiarlas ANTES de re-aplicar la migracion.', v_problematic_count;
     END IF;

     RAISE NOTICE 'FC1.3.F2.1 paso 5.A OK: 0 filas problematicas en ApprovalRequests tipo 11.';
   END $$;
   ```
   Si el script F2.1.0 paso bien antes, este pre-check no aborta. Si alguien salteo F2.1.0 o aparecio una fila nueva malformada despues del pre-check, la migracion **falla rapido** con mensaje claro en vez de dejar columnas NULL silenciosamente.

   **Paso 5.B — UPDATE acotado a filas seguras**:
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
     "FiscalLiquidation_ComputedAt"            = bc."LiquidationComputedAt",  -- RH3-003 (round 4): leer de columna summary, no del JSON, para evitar divergencia por serializacion. El CHECK chk_BookingCancellations_fiscalliquidation_consistency exige igualdad exacta.
     "FiscalLiquidation_ComputedByUserId"      = m.meta->>'computedByUserId',
     "FiscalLiquidation_ComputedByUserName"    = m.meta->>'computedByUserName'
   FROM (
     SELECT ar."Id" as id, ar."Metadata"::jsonb as meta
     FROM "ApprovalRequests" ar
     WHERE ar."RequestType" = 11
       AND ar."Metadata" IS NOT NULL
       AND jsonb_typeof(ar."Metadata"::jsonb) = 'object'
       AND ar."Metadata"::jsonb ? 'originalInvoiceAmount'
       AND ar."Metadata"::jsonb ? 'fiscalAmountToCredit'
       AND ar."Metadata"::jsonb ? 'currency'
   ) m
   WHERE bc."PartialCreditNoteApprovalRequestId" = m.id
     AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;  -- idempotente
   ```

   **Paso 5.C — count + RAISE NOTICE final**:
   ```sql
   DO $$
   DECLARE
     v_backfilled int;
     v_orphan int;
   BEGIN
     SELECT COUNT(*) INTO v_backfilled
     FROM "BookingCancellations"
     WHERE "FiscalLiquidation_FiscalAmountToCredit" IS NOT NULL;

     SELECT COUNT(*) INTO v_orphan
     FROM "BookingCancellations" bc
     WHERE bc."PartialCreditNoteApprovalRequestId" IS NOT NULL
       AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;

     RAISE NOTICE 'FC1.3.F2.1 backfill done. backfilled=% orphan_skipped=%', v_backfilled, v_orphan;
   END $$;
   ```

   - **Edge case 1** (resuelto por paso 5.A): Metadata malformado aborta la migracion entera con mensaje claro.
   - **Edge case 2**: BCs en estados `ManualReviewRejected (11)` ya tienen `PartialCreditNoteApprovalRequestId = NULL` (Fase 1 los nulea en `OnRejectedAsync` linea 1564-1569). Esos BCs NO tienen approval asociado para leer, asi que sus columnas `FiscalLiquidation_*` quedan NULL — correcto (la liquidacion no aplica porque el caso fue rechazado).
   - **Edge case 3**: BCs Fase 1 que **NO pasaron por manual review** (caso ReasonNone -> auto -> FC1.2 path) tampoco tienen approval. Esos BCs tampoco se backfillean. Si Fase 2 los necesita para reporting, una sub-fase posterior podria popularlos re-corriendo el calculator en background. Por ahora **YAGNI** — esos casos no necesitan liquidacion explicita porque la NC TOTAL del FC1.2 path ya cubre el caso.

6. **Doble-write OBLIGATORIO post-F2.1** (cierra **RH-002**):
   - El caller en `BookingCancellationService.ConfirmAsync` y `EditLiquidationAsync` debe persistir **simultaneamente** las dos representaciones:
     1. `ApprovalRequest.Metadata` JSON (lo que hoy hace Fase 1, no se toca).
     2. Las 10 columnas dedicadas `BookingCancellation.FiscalLiquidation_*` (nuevas en F2.1).
   - NO hay opcion de skip de Metadata. NO hay flag para escribir solo columnas. El doble-write es **invariante** de Fase 2.
   - Justificacion: el reverse de la migracion M1 vuelve la fuente de verdad a Metadata. Si una version intermedia dejara de escribir Metadata, el reverse perderia data y no habria forma de recuperar.
   - Documentado en §T6.4 (rollback) que reverse de M1 es seguro **solo mientras Metadata se siga escribiendo**.

7. Actualizar `BookingCancellationDto` para exponer `FiscalLiquidation` (campos opcionales) cuando exista.

**Criterio de aceptacion FC1.3.F2.1**:

- `dotnet build` verde.
- Migracion aplica contra BD vacia + dump de staging (asumiendo que F2.1.0 paso clean).
- Test integration `Backfill_FromExistingApprovalMetadata_PopulatesAllColumns`: seed 3 BCs en ManualReviewPending con Metadata JSON correcto. Correr migracion. Verificar que las 10 columnas estan pobladas exactamente igual que el JSON.
- Test integration `Backfill_SkipsRejectedBCs`: BC en ManualReviewRejected (FK approval nulled). Migracion no toca esa fila. Columnas FiscalLiquidation_* siguen null.
- **Test integration `Backfill_MissingKey_RaisesAndAborts`** (cierra **RH-001**): seed un `ApprovalRequest` tipo 11 con `Metadata = '{"originalInvoiceAmount": 100}'` (falta `fiscalAmountToCredit`, `currency`). Correr migracion. Verificar que falla con `RAISE EXCEPTION` y el mensaje contiene la cuenta de filas problematicas.
- **Test integration `Backfill_MalformedMetadata_RaisesAndAborts`** (cierra **RH-001**): seed un `ApprovalRequest` tipo 11 con `Metadata = '"not a json object"'`. Correr migracion. Verificar abort.
- **Test integration `Confirm_PostFase2_BothRepresentationsMatch`** (cierra **RH-002**): post-F2.1 crear un BC nuevo via `ConfirmAsync`. Leer `ApprovalRequest.Metadata` JSON Y las columnas `FiscalLiquidation_*`. Verificar que coinciden field by field. Si la divergencia es > 0.01 en algun monto, test rojo.
- **Test integration `EditLiquidation_PostFase2_UpdatesBothRepresentations`** (cierra **RH-002**): admin edita la liquidacion via `EditLiquidationAsync`. Verificar que Metadata Y columnas reflejan el cambio.
- Test integration `CheckConstraint_SumMismatch_RejectedByPostgres`: INSERT raw con `FiscalAmountToCredit=500 + NonRefundableItemsAmount=100 + OperatorPenaltyAmount=100 != OriginalInvoiceAmount=1000` -> Postgres rechaza con SqlState 23514 -> interceptor mapea a `BusinessInvariantViolationException`.

**Dependencias**: FC1.3.F2.0 + FC1.3.F2.1.0 (script standalone corrido + signoff).

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
   - **Mapping tipo NC** (cierra **RH-003**, reusa el helper canonico `InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice` ya en uso por Fase 1 + FC1.2):
     - Factura A (1) -> NC tipo 3.
     - Factura B (6) -> NC tipo 8.
     - Factura C (11) -> NC tipo 13.
     - **Factura M (51): NO soportada por Fase 2**. El helper devuelve `null`. El job debe abort + log + marcar `AnnulmentStatus = Failed` con razon `"Factura M no soportada para NC parcial en Fase 2 (RH-003). Si hay demanda real, sub-tarea separada con mapeo 51 -> 53 + tests + verificacion contador."`. La precondicion `IsSupportedForAnnulment(originalTipo)` ya filtra esto a nivel `EnqueuePartialCreditNoteAsync` — el chequeo en el job es defensa adicional.
     - NDs (2/7/12/52) y NCs (3/8/13/53) NUNCA pueden ser origen — `IsSupportedForAnnulment` los rechaza.
   - Construir `CreateInvoiceRequest` con `OriginalInvoiceId = original.PublicId.ToString()`, `IsCreditNote = true`, `Items = liquidation.Lines mapped`, `Tributes = []` (Fase 2 NO prorratea tributos provinciales — ver §T8 OQ-3 y G-F2-C: si la factura origen tiene `Tributes.Any()`, NUNCA llegamos aca porque `HasProvincialTributes` rerouted a manual review en F2.0.G-F2-C).
   - Llamar `_afipService.CreatePendingInvoice(reservaId, request)` -> retorna `Invoice` con la NC nueva (que llega al SOAP de `AfipService:780+` con `<CbtesAsoc>` ya pluged correctamente por el path existente).
   - Llamar el flow normal `_afipService.ProcessInvoiceJob(newInvoiceId)` para encolar el envio al ARCA.
   - Si el ARCA aprueba: setear `original.AnnulmentStatus = Succeeded`, `AnnulledAt = UtcNow`. Persistir.
   - Si rechaza: `Failed` + notify.

   **Idempotencia ARCA pos-reintento Hangfire (cierra RH-004 round 2 + RH2-004 round 3)**:

   El riesgo: Hangfire reintenta el job tras timeout/crash. Sin guard, podriamos POSTear dos veces al ARCA y emitir CAEs duplicados. El path FC1.2 `ProcessAnnulmentJob` ya tiene un guard parcial via `AnnulmentStatus`, pero el guard puede ser pasado por un reintento del job mismo entre el set a `Pending` y el POST efectivo. Para FC1.3 NC parcial reforzamos con dos capas + recovery de keys huerfanas:

   - **Capa 1 — IdempotencyKey persistido pre-POST**:
     - Cada job de NC parcial calcula al arrancar: `idemKey = SHA256($"{originalInvoiceId}|{approvalRequestId}|{liquidation.FiscalAmountToCredit:F2}|{liquidation.Currency}")`.
     - Antes de llamar `_afipService.CreatePendingInvoice`, hacer `INSERT INTO "ArcaIdempotencyKeys"(Key, JobId, CreatedAt) VALUES (...)` con UNIQUE constraint sobre `Key`. Si el INSERT falla por unique violation, **NO** se marca inmediatamente `Failed` — primero se aplica la logica de "stale key recovery" (ver capa 1.5 abajo) porque la key podria ser huerfana de un crash previo.
     - Tabla nueva `ArcaIdempotencyKeys` (Fase2.M1 incluye su creacion): `Id` PK, `Key` text UNIQUE, `JobId` text, `CreatedAt` timestamptz, `ResolvedAt` timestamptz NULL.
     - Al finalizar (success o failure terminal), setear `ResolvedAt = now()`. Los registros viejos (>30d) pueden purgarse via job de housekeeping (no Fase 2).

   - **Capa 1.5 — Stale key recovery (RH2-004 round 3 + RH3-001 round 4 Camino A robusto)**:

     **Problema que cierra**: la capa 1 v2 cubria "POST viajo pero respuesta no llego" — el reintento detectaba key + consultaba ARCA + recuperaba. Pero NO cubria "INSERT key OK + proceso muere ANTES del POST". En ese escenario el reintento detectaba key existente, consultaba ARCA via `FECompUltimoAutorizado`, venia vacio (porque el POST nunca viajo), y el plan v2 marcaba `AnnulmentStatus = Failed` con razon "Idempotency duplicate" — dejando la NC en limbo permanente sin posibilidad de retry limpio.

     **RH3-001 (round 4) — alineacion con codigo real**: la version v3 del pseudocodigo invocaba `_afipService.QueryLastAuthorizedAsync(...)` y un shape `arcaResult.{Found, CbteAsoc, IssuedAt, Cae}` que **NO existen** en el codigo. Verificado:
     - `IAfipService` (`src/TravelApi.Application/Interfaces/IAfipService.cs`) NO expone `QueryLastAuthorizedAsync`.
     - `AfipService.GetNextVoucherNumber` (`AfipService.cs:1152`) es private y devuelve solo `int`.
     - `AfipService.GetVoucherDetails` (`AfipService.cs:1195`) retorna `AfipVoucherDetails` (`AfipDtos.cs:3-11`) que solo tiene `ImporteTotal/Neto/Iva/Trib + VatDetails + TributeDetails`. NO tiene `Cae`, `CbteAsoc`, ni `IssuedAt`.

     Gaston eligio **Camino A (robusto)**: agregar metodo + DTO + columna nuevos para sostener el recovery de manera testeable. NO degradar a "borrar key + retry siempre" (perdida de informacion de CAE si POST viajo).

     **Sub-tareas atomicas Camino A** (parte de F2.2):

     **Sub-tarea A.1** — Crear DTO `ArcaCompoundQueryResult` en `src/TravelApi.Application/DTOs/AfipDtos.cs`:
     ```csharp
     public record ArcaCompoundQueryResult(
         bool Found,
         int? LastNumero,
         string? Cae,
         int? CbteAsoc,         // OriginalInvoice id derivable del campo ARCA correspondiente
         DateTime? IssuedAt,
         decimal? ImporteTotal,
         string? MonId,
         decimal? MonCotiz);
     ```

     **Sub-tarea A.2** — Agregar metodo nuevo a `IAfipService`:
     ```csharp
     /// <summary>
     /// RH3-001 (round 4): consulta compuesta a ARCA para stale key recovery.
     /// Llama FECompUltimoAutorizado + FECompConsultar si el numerador avanzo
     /// desde lastSeenNumeroBeforePost. Usado por ProcessPartialCreditNoteJob
     /// cuando detecta una IdempotencyKey huerfana mas antigua que
     /// IdempotencyKeyStaleThresholdMinutes.
     /// </summary>
     Task<ArcaCompoundQueryResult> QueryLastAuthorizedWithDetailsAsync(
         int puntoVenta,
         int cbteTipo,
         int? lastSeenNumeroBeforePost,
         CancellationToken ct);
     ```

     **Sub-tarea A.3** — Implementacion en `AfipService.QueryLastAuthorizedWithDetailsAsync`. Pseudocodigo:
     ```
     1. var ultimo = await GetNextVoucherNumber(settings, cbteTipo) - 1;  // ultimo autorizado
     2. if (lastSeenNumeroBeforePost == null || ultimo <= lastSeenNumeroBeforePost):
          return new ArcaCompoundQueryResult(
              Found: false, LastNumero: ultimo,
              Cae: null, CbteAsoc: null, IssuedAt: null,
              ImporteTotal: null, MonId: null, MonCotiz: null);
          // El numerador no avanzo, POST nunca viajo.
     3. var detail = await GetVoucherDetails(cbteTipo, puntoVenta, ultimo);
     4. return new ArcaCompoundQueryResult(Found: true, LastNumero: ultimo, ...detail...);
     ```
     El metodo `GetVoucherDetails` actual solo expone `ImporteTotal/Neto/Iva/Trib`. **Sub-tarea A.3.1**: extender el parseo del SOAP de `FECompConsultar` para incluir tambien `Cae`, `CbteAsoc`, `CbteFch` (mapear a `IssuedAt` con formato `yyyyMMdd`), `MonId`, `MonCotiz`. **Decision de compatibilidad**: extender `AfipVoucherDetails` con esos campos como **OPCIONALES nullable** para no romper otros callers existentes. El parseo agrega los nodos cuando estan presentes en el response y deja `null` cuando no.

     **Contrato del array `CbtesAsoc` [RH4-002 round 4]**: por construccion, la NC parcial Fase 2 emite exactamente 1 `<CbteAsoc>` apuntando a la factura origen (`AfipService.cs:830-838`). El response del `FECompConsultar` para esa NC debe traer exactamente 1 item bajo `CbtesAsoc`. Comportamiento defensivo del parseo:
     - **Array con 1 item** (caso normal): `CbteAsoc = ese item.Cbte`.
     - **Array vacio (0 items)**: `CbteAsoc = null` -> la capa 1.5 lo trata como mismatch -> borra key + retry limpio (seguro).
     - **Array con N>1 items** (defensa proactiva por si ARCA cambia comportamiento futuro): log warning `"FECompConsultar devolvio multiples CbtesAsoc inesperado"` + `CbteAsoc = null` -> mismatch -> borra key + retry limpio. **NO** intentar elegir uno: el riesgo de elegir mal y derivar CAE incorrecto es peor que un retry extra.

     **Sub-tarea A.4** — Extender tabla `ArcaIdempotencyKeys` (M1) con columna nueva:
     - `LastSeenNumeroBeforePost int NULL` — guardada al crear la key, ANTES del POST a ARCA. El reintento la usa para saber contra que numerador comparar. NULL para keys pre-existentes (sin info historica del numerador, el recovery se degrada con `Found = false` y borra la key + reintenta).

     **Sub-tarea A.5** — Reescribir el pseudocodigo de la capa 1.5 con la firma real:
     ```csharp
     // Pseudocodigo del flow del reintento dentro de ProcessPartialCreditNoteJob:
     var existingKey = await _context.ArcaIdempotencyKeys
         .FirstOrDefaultAsync(k => k.Key == idemKey, ct);

     if (existingKey != null && existingKey.ResolvedAt == null)
     {
         var ageMinutes = (DateTime.UtcNow - existingKey.CreatedAt).TotalMinutes;

         if (ageMinutes > settings.IdempotencyKeyStaleThresholdMinutes)
         {
             // Posible escenario: proceso muerto entre INSERT y POST,
             // O entre POST y persistencia de la respuesta ARCA.
             var arcaResult = await _afipService.QueryLastAuthorizedWithDetailsAsync(
                 puntoVenta: originalInvoice.PuntoDeVenta,
                 cbteTipo: creditNoteCbteTipo,
                 lastSeenNumeroBeforePost: existingKey.LastSeenNumeroBeforePost,
                 ct);

             if (arcaResult.Found
                 && arcaResult.CbteAsoc == originalInvoice.Id
                 && Math.Abs((arcaResult.ImporteTotal ?? 0) - liquidation.FiscalAmountToCredit)
                    <= settings.PartialCreditNoteRoundingTolerance)
             {
                 // Caso A: POST viajo + comprobante ARCA matchea factura origen + monto.
                 // Derivar CAE + resolver la key. NO re-POSTear.
                 existingKey.ResolvedAt = DateTime.UtcNow;
                 newCreditNoteInvoice.Cae = arcaResult.Cae;
                 newCreditNoteInvoice.Resultado = "A";
                 // ... otros campos derivados del response (CbteFch, MonId/MonCotiz) ...
                 await _context.SaveChangesAsync(ct);
                 // Counter Serilog (ver §FC1.3.F2.6):
                 //   Fc13.PartialCreditNote.RecoveredFromStaleKey++
                 _logger.LogWarning(
                     "Idempotency recovery: derivado CAE de comprobante ya emitido. " +
                     "InvoiceId={InvoiceId} CAE={Cae} KeyAgeMin={Age}",
                     newCreditNoteInvoice.Id, arcaResult.Cae, ageMinutes);
                 // Audit: Fc13.PartialCreditNote.RecoveredFromStaleKey
                 return; // job termina sin volver a POSTear.
             }
             else
             {
                 // Caso B: POST nunca viajo (Found = false) o el comprobante
                 // ARCA encontrado NO matchea con nuestra factura origen / monto
                 // esperado (otro proceso ocupo el numerador). Borrar key
                 // huerfana + permitir reintento limpio en este mismo job.
                 _context.ArcaIdempotencyKeys.Remove(existingKey);
                 await _context.SaveChangesAsync(ct);
                 _logger.LogWarning(
                     "Idempotency stale key removed (orphan from previous crash or " +
                     "numerator mismatch). Key={Key} KeyAgeMin={Age} ArcaFound={Found} " +
                     "ArcaCbteAsoc={CbteAsoc} ArcaImporte={Importe}. Allowing fresh retry.",
                     idemKey, ageMinutes, arcaResult.Found,
                     arcaResult.CbteAsoc, arcaResult.ImporteTotal);
                 // continuar flow normal: ahora el INSERT de la capa 1
                 // tendra exito porque la key fue borrada.
             }
         }
         else
         {
             // Key reciente (< staleThreshold). Otro job/reintento esta procesando.
             // No abortar al toque con Failed: el job de reconciliacion lo recoge
             // mas tarde si nadie lo resuelve.
             throw new IdempotencyDuplicateException(
                 $"IdempotencyKey activa, otro job procesando (age={ageMinutes}min). " +
                 $"Reintento en el proximo ciclo.");
         }
     }
     ```

     **Sub-tarea A.6** — Sumar tests integration al criterio de aceptacion F2.2:
     - `ProcessPartialCreditNoteJob_KeyOrphanedPostNeverArrived_DeletesKeyAndRetries`: seed key con `ResolvedAt = NULL` + `LastSeenNumeroBeforePost = 1234` + ARCA mock que devuelve `LastNumero = 1234` (no avanzo). Verificar: (a) `arcaResult.Found == false`; (b) key borrada; (c) retry limpio + INSERT exitoso; (d) `CreatePendingInvoice` invocado exactamente 1 vez.
     - `ProcessPartialCreditNoteJob_KeyOrphanedPostArrived_RecoversFromArca`: seed key con `LastSeenNumeroBeforePost = 1234` + ARCA mock que devuelve `LastNumero = 1235 + CbteAsoc = originalInvoice.Id + ImporteTotal = liquidation.FiscalAmountToCredit + Cae = "12345678901234"`. Verificar: (a) `arcaResult.Found == true`; (b) `Cae` derivado y persistido; (c) `key.ResolvedAt` seteado; (d) counter `Fc13.PartialCreditNote.RecoveredFromStaleKey` incrementado; (e) `CreatePendingInvoice` NO invocado (no re-POST).
     - `ProcessPartialCreditNoteJob_KeyOrphanedMismatchAmount_TreatsAsPostNeverArrived`: seed key + ARCA mock devuelve `Found = true + CbteAsoc = originalInvoice.Id + ImporteTotal distinto` (otro proceso ocupo el numerador). Verificar: tratado como caso B -> key borrada + retry limpio + log warning con `ArcaImporte` distinto.

     **Sub-tarea A.7.0 [RH3-004 round 4]** — Agregar helper publico `GetLastAuthorizedNumeroAsync(int puntoVenta, int cbteTipo, CancellationToken ct)` a `IAfipService`. Devuelve `Task<int>` con el ultimo numerador autorizado por ARCA (= `GetNextVoucherNumber - 1` internamente).

     **Razon**: usar el `private GetNextVoucherNumber` desde otros servicios forzaria a (a) cambiar visibilidad a `internal`/`public`, lo cual rompe la encapsulacion porque su firma actual requiere `AfipSettings settings` como parametro (`AfipService.cs:1152`), exponiendo dependencia interna al caller; o (b) cargar y pasar `AfipSettings` desde el caller, multiplicando acoplamiento. El helper publico encapsula la carga de settings adentro del servicio.

     ```csharp
     public async Task<int> GetLastAuthorizedNumeroAsync(
         int puntoVenta, int cbteTipo, CancellationToken ct)
     {
         var settings = await GetSettingsAsync(ct);  // ya existe en IAfipService
         var proximo = await GetNextVoucherNumber(settings, cbteTipo);
         return proximo - 1;
     }
     ```

     Tambien usado internamente por `QueryLastAuthorizedWithDetailsAsync` (sub-tarea A.3 paso 1, en lugar de invocar el private directamente).

     **Sub-tarea A.7 [REVISADA ROUND 4 — RH4-001]** — El snapshot del numerador se captura DENTRO de `ProcessPartialCreditNoteJob` como **primera operacion antes del INSERT en `ArcaIdempotencyKeys`**. `EnqueuePartialCreditNoteAsync` **NO toca ARCA ni `ArcaIdempotencyKeys`**: solo valida + encola el job Hangfire + termina.

     **Razon**: el encolado y la ejecucion del job pueden separarse 5+ minutos bajo carga Hangfire (otros jobs en cola, reintentos, worker saturado). Si el snapshot se hiciera en el encolado, el numerador ARCA podria avanzar decenas de comprobantes en el medio por otros emisores del mismo PV (NCs totales FC1.2 simultaneas, etc.). El recovery posterior compararia contra un snapshot desactualizado y podria falsamente concluir "POST viajo + alguien ocupo el numerador" cuando en realidad el POST nunca se hizo.

     **Flow correcto**:
     1. `EnqueuePartialCreditNoteAsync`: valida liquidacion + `Invoice.AnnulmentStatus = Pending` + encola job + termina.
     2. `ProcessPartialCreditNoteJob` arranca:
        - (a) Invoca `await _afipService.GetLastAuthorizedNumeroAsync(puntoVenta, cbteTipo, ct)` -> guarda en variable local `lastSeenNumeroBeforePost`.
        - (b) Calcula `idemKey = SHA256(originalInvoiceId|approvalRequestId|FiscalAmountToCredit|Currency)`.
        - (c) Intenta `INSERT INTO ArcaIdempotencyKeys (Key, JobId, CreatedAt, LastSeenNumeroBeforePost)` con el valor del paso (a).
        - (d) Si el INSERT falla por unique violation -> dispara capa 1.5 (que lee `existingKey.LastSeenNumeroBeforePost` ya persistido por la corrida anterior y consulta ARCA via `QueryLastAuthorizedWithDetailsAsync`).
        - (e) Si el INSERT tiene exito -> POST a ARCA.

     **Invariante**: el snapshot vive en la misma ejecucion del job que el POST efectivo. Si Hangfire reintenta el job entero, la capa 1.5 detecta la key huerfana del intento anterior y arbitra correctamente.

   - **Capa 2 — chequeo ARCA `QueryLastAuthorizedWithDetailsAsync` invocado por capa 1.5**: el path A de la capa 1.5 ya consulta ARCA via el metodo nuevo. La capa 2 v2 quedaba como "defense-in-depth" pero desde v3 esta consolidada dentro de la capa 1.5. NO se necesita una invocacion adicional separada.

   - **Nuevo setting** (sumar a §FC1.3.F2.0): `IdempotencyKeyStaleThresholdMinutes` (`int`, default `10`). Define el umbral en minutos a partir del cual una key sin resolver se considera "potencialmente huerfana" y dispara el recovery. Valores recomendados: 5-15 min (mayor que el timeout tipico de Hangfire pero menor que el time-to-live de la respuesta ARCA).

5. **Tests unitarios** (~12) del calculo de prorrateo IVA aislado en una clase helper `PartialCreditNoteIvaCalculator`. Cubrir:
   - `ProportionalToNet` con 1 sola alicuota -> IVA = total * multiplier.
   - `ProportionalToNet` con 2 alicuotas mezcladas (10.5% y 21%).
   - `PerItem` con misma config.
   - Casos borde: `FiscalAmountToCredit` con prorrateo que da .005 (redondeo bancario).
   - Casos que romperian tolerancia: throw.

6. **NO tocar `AfipService` directamente** salvo necesidad real. El path existente `CreatePendingInvoice` + `ProcessInvoiceJob` ya soporta NC con `<CbtesAsoc>`. El trabajo nuevo es en `InvoiceService.ProcessPartialCreditNoteJob` que arma el `CreateInvoiceRequest` correcto y lo entrega al pipeline existente.

7. **Validacion defensiva pre-encolado** (cierra **M4** del reviewer):
   - En `EnqueuePartialCreditNoteAsync` (PRE-encolado, NO solo en el job), validar `ABS(liquidation.OriginalNetAmount + liquidation.OriginalVatAmount - liquidation.OriginalTotalAmount) <= settings.PartialCreditNoteRoundingTolerance` y `ABS(SUM(Lines.Total) - liquidation.FiscalAmountToCredit) <= tolerance`.
   - Si falla, throw `ArgumentException` ANTES de mutar `Invoice.AnnulmentStatus`, ANTES de encolar Hangfire. Cero side-effects.
   - Incrementar counter `Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue` (counter separado del que cuenta validaciones fallidas en el job mismo, ver §F2.6).
   - Esta validacion es redundante con la del job (mismo `ABS(...) <= tolerance` en el calculo de IVA) pero **detecta upstream**. La validacion del job se mantiene como defense-in-depth.

**Criterio de aceptacion FC1.3.F2.2**:

- `dotnet build` verde.
- Los 12 tests unitarios del calculator de IVA pasan.
- Test integration `EnqueuePartialCreditNoteAsync_HappyPath_PersistsPendingAndEnqueuesJob`: setup factura B + liquidation con FiscalAmountToCredit=300 (de 1000 total). Verificar que `Invoice.AnnulmentStatus=Pending` post-llamada + 1 job en cola Hangfire mock.
- Test integration `ProcessPartialCreditNoteJob_BuildsCorrectInvoiceRequest`: mockear AfipService, capturar el `CreateInvoiceRequest` que arma el job. Verificar Items, OriginalInvoiceId, IsCreditNote=true, totales correctos.
- Test integration `ProcessPartialCreditNoteJob_SumMismatch_FailsBeforeArca`: liquidation con `FiscalAmountToCredit=100` pero suma de lineas `99.50` (gap > tolerancia). Job marca Failed sin llamar AFIP.
- Test integration `EnqueuePartialCreditNoteAsync_AlreadyPending_Rejects`: factura con AnnulmentStatus=Pending, segunda llamada throw.
- **Test integration `EnqueuePartialCreditNoteAsync_FacturaM_Rejects`** (cierra **RH-003**): factura origen tipo 51 (Factura M). `EnqueuePartialCreditNoteAsync` throw `InvalidOperationException` con mensaje "Factura M no soportada".
- **Test integration `EnqueuePartialCreditNoteAsync_SumMismatchAtEnqueue_DoesNotMutateInvoiceState`** (cierra **M4**): liquidation invalida (sum mismatch). El throw ocurre ANTES de mutar `Invoice.AnnulmentStatus`. Verificar que post-throw `Invoice.AnnulmentStatus` sigue en `NotRequested` (o el valor previo) y que counter `Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue` incremento.
- **Test integration `ProcessPartialCreditNoteJob_HangfireRetryAfterTimeout_DoesNotEmitDuplicate`** (cierra **RH-004**): simular escenario: job arranca, inserta IdempotencyKey, llama ARCA (mock que devuelve OK pero con delay > timeout Hangfire), Hangfire reintenta job. El reintento debe detectar `IdempotencyKey` ya existe con `ResolvedAt IS NULL`, consultar ARCA (mock `FECompUltimoAutorizado`) y derivar el CAE del comprobante ya emitido SIN re-POSTear. Verificar exactamente 1 invocacion a `CreatePendingInvoice` mock.
- **Test integration `ProcessPartialCreditNoteJob_IdempotencyKey_UniqueViolation_AbortsCleanly`** (cierra **RH-004**): dos jobs concurrentes con misma idemKey. Solo uno inserta + procesa. El otro detecta unique violation + abort + audit `Fc13.PartialCreditNote.IdempotencyDuplicate`.
- **Test integration `ProcessPartialCreditNoteJob_KeyOrphanedAfterCrash_AllowsRetryAfterStaleThreshold`** (cierra **RH2-004 round 3**): seed `ArcaIdempotencyKeys` con una row `CreatedAt = NOW() - 15 min` + `ResolvedAt = NULL` (simulando crash entre INSERT y POST de un job previo). Configurar `IdempotencyKeyStaleThresholdMinutes = 10`. Mock `QueryLastAuthorizedWithDetailsAsync` para devolver `Found = false` (POST nunca viajo). Correr `ProcessPartialCreditNoteJob` para la misma idemKey. Verificar: (a) la key huerfana fue borrada; (b) un INSERT fresh tuvo exito; (c) `CreatePendingInvoice` se llamo exactamente 1 vez; (d) log warning `Idempotency stale key removed` presente.
- **Test integration `ProcessPartialCreditNoteJob_KeyOrphanedPostNeverArrived_DeletesKeyAndRetries`** (cierra **RH3-001 round 4, sub-tarea A.6**): seed key huerfana + `LastSeenNumeroBeforePost = 1234`. Mock `QueryLastAuthorizedWithDetailsAsync` devuelve `Found = false, LastNumero = 1234` (numerador no avanzo). Verificar: key borrada + retry limpio + 1 sola invocacion a `CreatePendingInvoice`.
- **Test integration `ProcessPartialCreditNoteJob_KeyOrphanedPostArrived_RecoversFromArca`** (cierra **RH3-001 round 4, sub-tarea A.6**): seed key huerfana + `LastSeenNumeroBeforePost = 1234`. Mock devuelve `Found = true, LastNumero = 1235, CbteAsoc = originalInvoice.Id, ImporteTotal = liquidation.FiscalAmountToCredit, Cae = "12345678901234"`. Verificar: (a) `CAE` derivado y persistido en la NC; (b) `key.ResolvedAt` seteado; (c) counter `Fc13.PartialCreditNote.RecoveredFromStaleKey` incrementado; (d) `CreatePendingInvoice` NO invocado.
- **Test integration `ProcessPartialCreditNoteJob_KeyOrphanedMismatchAmount_TreatsAsPostNeverArrived`** (cierra **RH3-001 round 4, sub-tarea A.6**): seed key huerfana. Mock devuelve `Found = true, CbteAsoc = originalInvoice.Id, ImporteTotal != liquidation.FiscalAmountToCredit` (otro proceso ocupo el numerador). Verificar: tratado como caso B -> key borrada + retry limpio + log warning con `ArcaImporte` distinto del esperado.
- **Test integration `ProcessPartialCreditNoteJob_SnapshotCapturedAtJobStartNotAtEnqueueTime`** (cierra **RH4-001 round 4**): encolar `EnqueuePartialCreditNoteAsync` -> verificar que NO se invoco `GetLastAuthorizedNumeroAsync` ni se INSERTo en `ArcaIdempotencyKeys` (las dos cosas ocurren dentro del job). Avanzar el scheduler Hangfire mock para correr el job -> verificar que ahi si se invoco `GetLastAuthorizedNumeroAsync` exactamente una vez ANTES del INSERT de la key, y que el `LastSeenNumeroBeforePost` persistido es el que devolvio esa invocacion (no el snapshot que hubiera existido al momento del encolado).
- **Test integration `GetVoucherDetails_MultipleCbtesAsoc_LogsWarningAndReturnsNullCbteAsoc`** (cierra **RH4-002 round 4**): mock SOAP response de `FECompConsultar` con 2 nodos `<CbteAsoc>` en el array. Invocar `GetVoucherDetails` -> verificar: (a) `AfipVoucherDetails.CbteAsoc == null`; (b) log warning `"FECompConsultar devolvio multiples CbtesAsoc inesperado"` presente; (c) los demas campos (`ImporteTotal`, `Cae`, etc.) se parsean OK.
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

5. **NC parcial NO hace cascade de receipts** (cierra **RH-005** + alineado con **G-F2-D**).

   Contexto: hoy `AfipService.ApplyCreditNoteEconomicReversalAsync` (linea 1006-1114) busca un `Payment` cuyo `Amount == invoice.ImporteTotal` (matching exacto por monto, ver linea 1036) y si encuentra un `Receipt` atado, lo cascade-voida. Esta logica funciona correctamente para NC TOTAL (FC1.2) porque ahi `invoice.ImporteTotal` (de la NC) == `original.ImporteTotal`, y el `Receipt` original tiene ese monto exacto.

   Para NC PARCIAL la logica se rompe en dos escenarios:
   - **Monto no matchea**: `invoice.ImporteTotal` de la NC parcial es una fraccion. No hay un `Payment` con ese monto exacto. `matchedPayment == null`, no se cascade-voida ningun receipt. Pero ademas se crea un `Payment` reversal por la fraccion — el receipt queda colgado.
   - **G-F2-D (multi-recibos)**: una factura $1.000 pagada en 3 cuotas ($300 + $300 + $400) tiene 3 receipts vivos. NC parcial $250 no tiene un receipt unico a cascade-voidear. Cualquier seleccion automatica (por monto, por antiguedad) es arbitraria y riesgosa.

   **Politica Fase 2 (alineada G-F2-D)**:
   - Modificar `ApplyCreditNoteEconomicReversalAsync` para distinguir NC total vs NC parcial. **RH2-003 (round 3)**: la discriminacion **primaria** se hace leyendo el `BookingCancellation` asociado a la NC via `bc.CreditNoteInvoiceId == invoice.Id` y chequeando `bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal`. La comparacion por monto (`invoice.ImporteTotal < invoice.OriginalInvoice.ImporteTotal`) queda como **fallback historico** solo cuando el BC no existe (NCs pre-FC1.3 que no tienen BC asociado, o casos degradados).

     Justificacion del fallback: el campo `BookingCancellation.CreditNoteInvoiceId` (`BookingCancellation.cs:50`) existe pero NO esta poblado para todas las NCs historicas. NCs emitidas via FC1.2 antes del wiring de Fase 1 podrian no tener BC asociado. En esos casos la comparacion por monto sigue siendo correcta porque NC total real == ImporteTotal original.

     Pseudocodigo explicito de la discriminacion:
     ```csharp
     // ApplyCreditNoteEconomicReversalAsync (AfipService.cs:1006-1114, refactor F2.3)
     var bc = await _context.BookingCancellations
         .FirstOrDefaultAsync(b => b.CreditNoteInvoiceId == invoice.Id);

     bool isPartialNc;
     if (bc != null)
     {
         // Path canonico FC1.3+: el kind persistido es la fuente de verdad.
         isPartialNc = bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal;
     }
     else
     {
         // Fallback historico: NCs pre-FC1.3 sin BC asociado.
         // La comparacion por monto sigue siendo correcta porque las NCs
         // emitidas via FC1.2 son siempre totales y matchean el ImporteTotal original.
         isPartialNc = invoice.OriginalInvoice != null
                       && invoice.ImporteTotal < invoice.OriginalInvoice.ImporteTotal;
     }
     ```
   - **NC total**: comportamiento actual sin cambios (cascade receipt).
   - **NC parcial**: crear el `Payment` reversal con `OriginalPaymentId = null` por diseno (no hay un payment unico asociado). NO buscar payment exacto, NO cascade-voider receipts. Dejar todos los receipts vivos. Emitir audit nuevo `PartialCreditNoteEconomicReversalNoCascade` con detalle del monto reversal + cantidad de receipts vivos + IDs receipts vivos para que el admin pueda revisar manualmente.

     **RH2-006 (round 3) — query explicita de receipts vivos para el audit**:
     ```csharp
     // Verificado en codigo: PaymentReceipt entity en src/TravelApi.Domain/Entities/PaymentReceipt.cs.
     //  - Tabla: "PaymentReceipts".
     //  - Status: string con constantes PaymentReceiptStatuses.Issued / Voided (no enum).
     //  - FK a Payment via PaymentReceipt.PaymentId.
     //  - Payment.RelatedInvoiceId apunta a la Invoice (Payment.cs:50).
     // Estrategia: receipts vivos son los Issued cuyos Payments tienen
     // RelatedInvoiceId == invoice.OriginalInvoiceId.
     var liveReceipts = await _context.PaymentReceipts
         .Include(r => r.Payment)
         .Where(r => r.Payment.RelatedInvoiceId == invoice.OriginalInvoiceId
                     && r.Status == PaymentReceiptStatuses.Issued)
         .Select(r => r.Id)
         .ToListAsync();
     // liveReceipts -> details del audit `PartialCreditNoteEconomicReversalNoCascade`,
     // serializado como JSON: { "reversalAmount": -250, "liveReceiptIds": [12, 14, 19] }.
     ```
   - UI Fase 3 (fuera de scope) permitira al admin marcar manualmente que receipt anular.

   **Tests integration explicitos** (cierra **RH-005** + **G-F2-D**):
   - `ApplyCreditNoteEconomicReversal_NcTotal_StillCascadesReceipt` (regression FC1.2): 1 factura + 1 payment + NC total -> cascade voider receipt funciona como hoy.
   - `ApplyCreditNoteEconomicReversal_NcParcial_SinglePayment_NoCascade` (RH-005): 1 factura $1.000 + 1 payment $1.000 + NC parcial $250 -> `Payment` reversal $-250 creado con `OriginalPaymentId == null`. Receipt original sigue `Issued` (NO Voided). Audit `PartialCreditNoteEconomicReversalNoCascade` presente.
   - `ApplyCreditNoteEconomicReversal_NcParcial_MultiPayments_NoCascade` (G-F2-D): **setup exacto**: 1 factura $1.000 + 3 payments ($300, $300, $400) cada uno con su Receipt vivo + NC parcial $250. Verificar: (a) 0 receipts cascade-voided (los 3 siguen `Issued`); (b) 1 `Payment` reversal $-250 con `OriginalPaymentId == null`; (c) audit `PartialCreditNoteEconomicReversalNoCascade` emitido con `ReceiptsAffected = [receiptId1, receiptId2, receiptId3]` para trazabilidad.

6. **Tests integration nuevos del path Fase 2** (~7):
   - `OnApprovedAsync_Fase2On_EmitsRealPartialCreditNote`: setup BC con liquidation persistida + flag F2 ON. Verificar que `_invoiceService.EnqueuePartialCreditNoteAsync` fue llamado (mock) con los `Lines` correctos.
   - `OnApprovedAsync_Fase2Off_FallsBackToFc12FlowWithWarning`: mismo setup + flag F2 OFF. Verificar comportamiento actual + log warning presente.
   - `OnApprovedAsync_NonRefundableItems_ExcludedFromLines`: factura con 3 items (2 refundable + 1 no). Verificar que los Lines del request al InvoiceService NO contienen el item no refundable.
   - `OnApprovedAsync_MultipleAlicuotas_PreservesAll`: factura con items 10.5% + 21%. Verificar que el request mantiene ambas alicuotas con prorrateo proporcional.
   - `OnApprovedAsync_LiquidationSumMismatch_AbortsEmission`: simular concurrent edit que rompio INV-FC1.3-005 (UPDATE raw que bypassa CHECK por algun motivo). Service detecta + throw + audit log + emit no se llama.
   - `OnApprovedAsync_IdempotenceTwoCallsSecondNoop`: invocar `OnApprovedAsync` dos veces — segunda no-op.
   - **`OnApprovedAsync_Fase2_PartialNc_MultiplePaymentsScenario_NoCascade_LeavesAuditTrail`** (cierra **RH-005** + **G-F2-D** end-to-end): setup BC + factura $1.000 + 3 payments ($300, $300, $400) con sus 3 receipts vivos + liquidation $250 + aprobar. Verificar al final: NC parcial pendiente o emitida (segun mock ARCA), `Payment` reversal $-250, 0 receipts cascade-voided, audit `PartialCreditNoteEconomicReversalNoCascade` con los 3 receipt IDs listados.

**Criterio de aceptacion FC1.3.F2.3**:

- Los 7 tests integration del path Fase 2 + los 3 tests de cascade (RH-005) pasan.
- Los 5 tests existentes del bridge (FC1.3.4) siguen verdes (sin regresion del fallback FC1.2).
- Re-correr los 66 unit tests Fase 1 — todos verdes.
- Re-correr los tests de cascade FC1.2 existentes (`ApplyCreditNoteEconomicReversal*` regresion) — todos verdes (el cambio solo agrega rama NC parcial; la rama NC total queda igual).

**Dependencias**: FC1.3.F2.2 (metodo nuevo en InvoiceService) + FC1.3.F2.1 (liquidacion en BD).

---

### FC1.3.F2.4 — Caso `TotalPlusNewInvoice` (casos 4 y 7) — flow dual [GATED por G-F2-A]

**STATUS**: **GATED por criterio cuantitativo G-F2-A**.

**Criterio de activacion (G-F2-A)**:

1. F2.4 NO entra en el PR Fase 2 base. El flag `EnableTotalPlusNewInvoiceAutoProcessing` se crea en F2.0 con default `false` y NO se puede activar (validacion startup rechaza la combinacion `F2.4 ON + F2.4 codigo no merged`).
2. Despues de prender Fase 2 en prod (F2.3) y dejarla correr al menos un ciclo de cancelaciones, correr la query SQL de medicion **§FC1.3.F2.4.0**.
3. Si `casos_4_y_7 / total_cancelaciones >= 5%` -> F2.4 entra como **sesion separada** con su propio plan tactico + reviewer + tests.
4. Si `casos_4_y_7 / total_cancelaciones < 5%` -> F2.4 queda en **backlog indefinidamente**. Los casos 4/7 siguen siendo rechazados con `InvalidOperationException` segun GR-001 vigente desde Fase 1.

**Justificacion del gate**:

- El flow dual TotalPlusNewInvoice tiene complejidad muy alta (estado intermedio nuevo, job de reconciliacion adicional, endpoint admin force, idempotencia entre dos eventos fiscales atomicos en ARCA).
- Si los casos 4/7 son raros, el costo de mantenimiento + riesgo de bugs supera el beneficio.
- El flag default OFF garantiza back-compat estricta con la regla GR-001 vigente desde Fase 1.

#### FC1.3.F2.4.0 — Query SQL para medir volumen casos 4 y 7

Correr en prod **al menos 30 dias despues** de prender `EnablePartialCreditNoteRealEmission` para tener muestra representativa:

```sql
-- FC1.3.F2.4.0 — Medicion de volumen de casos 4 y 7 post-Fase 2.
-- Correr en prod, no antes de 30 dias desde el prendido de EnablePartialCreditNoteRealEmission.
-- Si pct_casos_4_y_7 >= 5%, abrir sesion separada para implementar F2.4.
--
-- RH2-001 (round 3): la version v2 usaba bitflags sobre ReviewRequiredReason
-- (`(reason & 32) <> 0` para caso 4, `(reason & 16) <> 0` para caso 7).
-- Eso SOBRECUENTA porque el calculator clasifica con prioridad estricta
-- (FiscalLiquidationCalculator.cs:417-440): si CustomerIsRiOrFacturaA esta
-- prendido (1 << 0), gana sobre OriginalInvoiceUnclear (1 << 5) y
-- RetentionChangesNature (1 << 4). Una BC con Factura A + RetentionChangesNature
-- se clasifica como Case 8 pero la query bitflag la contaba como caso 7.
--
-- Solucion: discriminar por el kind persistido (BookingCancellation.CreditNoteKind),
-- que refleja la decision final del calculator. Casos 4 y 7 son exactamente los
-- que tienen kind = TotalPlusNewInvoice (= 2 en el enum, ver CreditNoteKind.cs:24).

WITH window_cancellations AS (
    SELECT
        bc."Id",
        bc."CreditNoteKind",
        bc."CreatedAt"
    FROM "BookingCancellations" bc
    WHERE bc."CreatedAt" >= NOW() - INTERVAL '30 days'
)
SELECT
    COUNT(*) FILTER (WHERE "CreditNoteKind" = 2) AS cnt_casos_4_y_7,
    COUNT(*)                                     AS cnt_total,
    ROUND(
      100.0 * COUNT(*) FILTER (WHERE "CreditNoteKind" = 2) / NULLIF(COUNT(*), 0),
      2
    ) AS pct_casos_4_y_7
FROM window_cancellations;
```

**Interpretacion**:
- `pct_casos_4_y_7 < 5%` -> F2.4 queda en backlog indefinidamente. Documentar resultado en `docs/operations/`.
- `pct_casos_4_y_7 >= 5%` -> abrir sesion separada `plan-tactico-fc1-3-fase2-4.md` con todo el contenido descrito abajo. Incluir output de la query como justificacion.

#### Contenido de F2.4 (si el gate se abre — para referencia, NO se implementa ahora)

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

**Criterio de aceptacion FC1.3.F2.4 (si el gate G-F2-A se abre)**:

- Los 8 tests pasan.
- Tests Fase 1 del rechazo `InvalidOperationException` (GR-001) **siguen verdes** cuando el flag F2.4 esta OFF (preserva back-compat).
- Endpoint force-step2 documentado en doc trainee Fase 2.4.
- Output de la query F2.4.0 documentado como justificacion para abrir el gate.

**Dependencias**: FC1.3.F2.3 (flow base de emision real) + signoff de Gaston sobre el resultado de la query F2.4.0.

**Decision actual round 2 (G-F2-A)**: F2.4 **NO entra al PR Fase 2 base**. La sub-fase queda documentada como spec pero NO se implementa ahora. El criterio de activacion es cuantitativo (5% del volumen) y se mide despues de tener Fase 2 corriendo en prod.

---

### FC1.3.F2.5 — Multimoneda en NC parcial (RH2-002 refactor completo)

**Contexto del problema RH2-002**: hoy `AfipService.cs:879-880` hardcodea `<MonId>PES</MonId><MonCotiz>1</MonCotiz>` literal en el XML SOAP del envelope FECAESolicitar. El metodo `ProcessInvoiceJob` (linea 729+) recibe solo `int invoiceId` y carga la `Invoice` desde BD — no tiene forma de saber moneda/cotizacion porque esos datos NO existen en la entidad `Invoice` hoy. La v2 del plan decia "extraer a `request.MonId` + `request.MonCotiz` y leer de la NC parcial" pero esa frase asume que el dato ya viaja por algun lado, lo cual NO es cierto. Hace falta un refactor explicito.

**Tareas atomicas (cierra RH2-002 + RH3-002)**:

0. **[RH3-002 round 4] Auditoria pre-refactor de `<MonId>/<MonCotiz>` en `AfipService.cs`**. Antes de tocar el XML SOAP, correr:
   ```bash
   grep -n "MonId\|MonCotiz" src/TravelApi.Infrastructure/Services/AfipService.cs
   ```
   Hoy se conoce que las ocurrencias aparecen en `AfipService.cs:879-880` (envelope FECAESolicitar dentro de `ProcessInvoiceJob`). Si el grep devuelve otras lineas, integrarlas explicitamente al refactor (no asumir que solo son esas dos). Si confirma solo las dos conocidas, documentar el resultado del grep como "cero divergencias adicionales" en el doc trainee F2.7.

   **Criterio aceptacion tarea 0**: post-refactor, correr:
   ```bash
   grep -c "PES\b\|MonCotiz>1" src/TravelApi.Infrastructure/Services/AfipService.cs
   ```
   Debe mostrar **valor cero** o solo en comentarios (no en strings ejecutables del SOAP). Si aparece alguna ocurrencia hardcoded fuera de comentarios, el refactor no esta completo.

1. **Agregar columnas a `Invoice` entity** (parte de la migracion `Fase2.M1`, NO migracion separada — un solo deploy):
   - `Invoice.MonId` (`nvarchar(3)`, NOT NULL, DEFAULT `'PES'`). Codigo de moneda ARCA: `"PES"` para pesos, `"DOL"` para dolar, etc.
   - `Invoice.MonCotiz` (`numeric(18,6)`, NOT NULL, DEFAULT `1`). Cotizacion al peso. Para PES siempre `1`. Para DOL = TC del comprobante origen (RH2-007 referencia).
   - Para back-compat: la migracion popula las filas existentes con `'PES'`/`1` (DEFAULT inline cubre INSERTs viejos sin reescribir).

2. **Extender `CreateInvoiceRequest` DTO** (`src/TravelApi.Application/DTOs/CreateInvoiceRequest.cs`):
   ```csharp
   public class CreateInvoiceRequest
   {
       // ... props existentes ...
       
       /// <summary>RH2-002 (round 3): codigo ARCA de moneda. Default "PES" para no romper callers FC1.2.</summary>
       public string MonId { get; set; } = "PES";
       
       /// <summary>RH2-002 (round 3): cotizacion al peso. Default 1 para no romper callers FC1.2.</summary>
       public decimal MonCotiz { get; set; } = 1m;
   }
   ```

3. **Modificar `AfipService.CreatePendingInvoice`** (`AfipService.cs:564+`):
   - Al construir la entidad `Invoice` desde el `request`, poblar `invoice.MonId = request.MonId` + `invoice.MonCotiz = request.MonCotiz`.
   - Los callers FC1.2 que NO setean estas props mandan los defaults `("PES", 1)`. Sin cambios de comportamiento para FC1.2.

4. **Modificar `AfipService.ProcessInvoiceJob`** (`AfipService.cs:729+`):
   - El metodo ya recarga `Invoice` desde BD. Sumar `invoice.MonId` + `invoice.MonCotiz` al cargar (estan en la misma fila, no requiere Include nuevo).
   - Reemplazar el XML hardcoded en linea 879-880 con interpolacion:
     ```xml
     <MonId>{invoice.MonId}</MonId>
     <MonCotiz>{invoice.MonCotiz.ToString("0.000000", CultureInfo.InvariantCulture)}</MonCotiz>
     ```
   - **No tocar otros campos del XML**. Solo dos lineas.

5. **Modificar `FiscalLiquidationCalculator`** (Fase 1): hoy `STEP 1` flagea `MultiCurrency` siempre que `Currency != "ARS"`. Fase 2 cambia el criterio:
   - Si `settings.EnablePartialCreditNoteRealEmission == true` -> `MultiCurrency` deja de ser disparador automatico de review manual. La NC se emite en la moneda del comprobante origen con `FiscalSnapshot.ExchangeRateAtOriginalInvoice` como `<MonCotiz>`.
   - Si `false` (Fase 1) -> sigue siendo disparador (comportamiento actual).

6. **Modificar `ProcessPartialCreditNoteJob`** (creado en F2.2):
   - Cuando arma el `CreateInvoiceRequest`, setear `MonId` y `MonCotiz` desde `liquidation`:
     ```csharp
     var request = new CreateInvoiceRequest
     {
         // ... otros campos ...
         MonId = liquidation.Currency == "ARS" ? "PES" : MapToArcaCurrencyCode(liquidation.Currency),
         MonCotiz = liquidation.Currency == "ARS" ? 1m : liquidation.ExchangeRateAtOriginalInvoice
     };
     ```
   - Helper `MapToArcaCurrencyCode("USD") => "DOL"` (mapeo conocido). Si llega otra moneda no soportada, throw + log + marcar `AnnulmentStatus = Failed`.

7. **Identificar callers a auditar** (que NO deben romperse con defaults):
   - `InvoiceService.EnqueueAnnulmentAsync` -> arma `CreateInvoiceRequest` para NC TOTAL FC1.2. No setea `MonId/MonCotiz` -> defaults `("PES", 1)`. **Sin cambios**.
   - Frontend (crear factura manual via endpoint REST) -> el DTO viaja desde JSON. Los clientes existentes no incluyen estos campos -> defaults aplican.
   - Tests integration FC1.2 -> arman el DTO sin esos campos -> defaults aplican.
   - Tests unit que mockean `CreatePendingInvoice` -> el mock no chequea props nuevas -> sin impacto.

8. **Tests unit** (~4):
   - `Calculator_MultiCurrencyWithFase2On_DoesNotFlagReason`.
   - `Calculator_MultiCurrencyWithFase2Off_StillFlagsReason` (back-compat Fase 1).
   - `PartialCreditNoteJob_UsdInvoice_UsesSnapshotExchangeRate`: verificar que el `CreateInvoiceRequest` armado tiene `MonId == "DOL"` + `MonCotiz == snapshot.TC`.
   - `PartialCreditNoteJob_ArsInvoice_KeepsDefaultPesoMapping`: factura ARS -> request con `("PES", 1)`.

9. **Tests integration** (4):
   - **`Fc12NormalInvoice_StillEmitsWithPesos`** (regresion FC1.2): emitir factura B normal via flow FC1.2 sin tocar `MonId/MonCotiz` en el caller. Verificar que la `Invoice` persistida tiene `MonId = "PES"` + `MonCotiz = 1`. Verificar que el SOAP enviado a ARCA (mock) tiene `<MonId>PES</MonId><MonCotiz>1.000000</MonCotiz>`.
   - **`Fc12Annulment_StillEmitsWithPesos`** (regresion FC1.2): anular factura via `EnqueueAnnulmentAsync` (NC TOTAL). Verificar que la NC resultante tiene `MonId = "PES"` + el SOAP mantiene `PES/1`.
   - **`PartialCreditNoteUsd_EmitsWithDolarAndSnapshotRate`** (nuevo): setup factura USD con `FiscalSnapshot.ExchangeRateAtOriginalInvoice = 1234.56`, Fase 2 ON, emitir NC parcial. Verificar que la NC parcial tiene `MonId = "DOL"` + `MonCotiz = 1234.56`. El SOAP mock recibe esos valores en el XML.
   - **`PartialCreditNoteArs_EmitsWithPesoAndOne`** (nuevo): factura ARS, NC parcial. `MonId = "PES"` + `MonCotiz = 1`.

**Criterio de aceptacion FC1.3.F2.5**:

- 4 unit + 4 integration tests pasan.
- Tests Fase 1 con factura ARS siguen verdes.
- Tests integration FC1.2 existentes siguen verdes (defaults activos).
- Asuncion §T1.4 punto 3 validada con contador (F5 del mensaje round 3) — si rechaza, ajustar logica.

**Dependencias**: FC1.3.F2.2 + FC1.3.F2.1 (la migracion M1 incluye `MonId/MonCotiz` ademas del owned VO `FiscalLiquidation`).

**Riesgo de regresion (RH2-002 implicacion §T2.2)**: F2.5 ya NO es solo "ajuste de logica" — toca el SOAP envelope (lineas hardcoded en `AfipService.cs:879-880`) + cambia firma del DTO + agrega columnas. El orden de merge sugerido en §T2.2 mueve F2.5 antes de F2.7 pero **se recomienda** que el subagente backend mergee F2.5 **despues** de F2.6/F2.6a en una sub-PR aparte para aislar regresiones FC1.2. Ver §T2.2 actualizada.

---

### FC1.3.F2.6 — Counters Serilog

**Tareas atomicas**:

1. **Counters Serilog** (mismo patron FC1.2.7b verificado en HEAD):
   - `Fc13.PartialCreditNote.Emitted` por currency/case/InvoicingMode.
   - `Fc13.PartialCreditNote.ArcaApproved`.
   - `Fc13.PartialCreditNote.ArcaRejected` con tag `RejectReason`.
   - `Fc13.PartialCreditNote.SumValidationFailedAtJob` (defensivo, no deberia incrementar).
   - **`Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue`** (minor del reviewer): incrementa cuando la validacion defensiva en `EnqueuePartialCreditNoteAsync` rechaza el llamado pre-encolado.
   - **`Fc13.PartialCreditNote.IdempotencyDuplicate`** (RH-004): incrementa cuando se detecta IdempotencyKey duplicada.
   - **`Fc13.PartialCreditNote.RecoveredFromStaleKey`** (RH3-001 round 4): incrementa cuando la capa 1.5 stale key recovery deriva el CAE de un comprobante ya emitido en ARCA (path A del recovery). Util para alertar al admin si crece (indicaria crashes recurrentes entre POST y persistencia de respuesta).
   - `Fc13.PartialCreditNote.NoCascadeReceiptsPreserved` (G-F2-D): incrementa cuando ApplyCreditNoteEconomicReversal preserva receipts (rama parcial).
   - (Si gate G-F2-A se abre en el futuro) `Fc13.PartialCreditNote.DualFlowStep1Succeeded`, `DualFlowStep2Succeeded`, `DualFlowStuck`.

**Criterio de aceptacion FC1.3.F2.6**:

- Test integration `CountersIncremented_OnEmissionAndOutcome`: simular ciclo completo (encolado OK -> ARCA Approved) y verificar counters esperados.
- Counters visibles en Serilog logs durante un test E2E manual contra ARCA homologacion (diferido a sesion QA).

**Dependencias**: FC1.3.F2.2.

---

### FC1.3.F2.6a — Job NUEVO `PartialCreditNotePostingReconciliationJob` (cierra RH-006)

**Justificacion del job NUEVO (no extension) — actualizada RH2-008 round 4**:

El plan v1 mencionaba "extender `ArcaAnnulmentReconciliationJob` existente FC1.2" basado en suposicion. Round 3 + round 4 verificaron el repo:

- `ArcaAnnulmentReconciliationJob` **NO existe como clase implementada**. Verificado con `Grep "class ArcaAnnulmentReconciliationJob"` -> 0 matches en `src/`. Aparece SOLO como:
  - Comentario aspiracional en `src/TravelApi.Domain/Entities/Invoice.cs:62` (TODO de deuda historica FC1.2: "Lo usa el job recurrente `ArcaAnnulmentReconciliationJob` para detectar facturas en `AnnulmentStatus.Pending`...").
  - Mencion en la migracion legacy `src/TravelApi.Infrastructure/Persistence/Migrations/App/20260514030142_FC1_AddCancellationModule.cs` que define el campo `Resultado` pero no implementa el job.

- El job que **SI existe y SI funciona** como patron de referencia es `PartialCreditNoteBridgeReconciliationJob` en `src/TravelApi.Infrastructure/Services/`. Es el que Fase 2 replica.

Por separation of concerns + ausencia del job hipotetico, Fase 2 crea un job nuevo `PartialCreditNotePostingReconciliationJob` **sin riesgo de acoplamiento con codigo inexistente**. Si en el futuro se implementara `ArcaAnnulmentReconciliationJob`, las responsabilidades quedan claramente separadas:

- `ArcaAnnulmentReconciliationJob` (hipotetico, deuda historica FC1.2) -> reconcilia `Invoice.AnnulmentStatus = Pending` (modelo de datos "factura origen + su AnnulmentStatus").
- `PartialCreditNotePostingReconciliationJob` (Fase 2) -> reconcilia `Invoice.Resultado = 'PENDING'` con `OriginalInvoiceId IS NOT NULL` (modelo de datos "NC nueva + su Resultado ARCA").

La deuda historica del comentario en `Invoice.cs:62` se documenta como **fuera de scope Fase 2** — si el contador o la operacion la requieren mas adelante, sub-fase opt-in F2.6b separada (no se asume).

**Tareas atomicas**:

1. Crear `src/TravelApi.Infrastructure/Services/PartialCreditNotePostingReconciliationJob.cs`. **RH2-005 (round 3)**: la convencion del repo es `Infrastructure/Services/` para jobs (verificado: `PartialCreditNoteBridgeReconciliationJob` esta en `Services/`, no en `Jobs/`). Patron: replicar la estructura de `PartialCreditNoteBridgeReconciliationJob` (que existe y se valido en Fase 1):
   - Cron (mismo intervalo que el job bridge, ej. cada 5 min).
   - Contador anti-spam (no notificar la misma `Invoice` cada vez).
   - Max retries antes de notify a operador.

2. Scope (query SQL del job):
   ```sql
   SELECT i."Id", i."ReservaId", i."PublicId"
   FROM "Invoices" i
   WHERE i."OriginalInvoiceId" IS NOT NULL                  -- Es NC
     AND i."Resultado" = 'PENDING'                           -- Esta colgada
     AND i."CreatedAt" < NOW() - INTERVAL '15 minutes'       -- Mas de N min
     AND i."Id" IN (
       -- Solo NCs parciales (no NC total FC1.2). Discriminador: importe.
       SELECT i2."Id" FROM "Invoices" i2
       INNER JOIN "Invoices" iorig ON iorig."Id" = i2."OriginalInvoiceId"
       WHERE i2."ImporteTotal" < iorig."ImporteTotal"
     )
   ```
   La discriminacion "monto NC < monto factura origen" identifica NC parcial vs NC total sin necesidad de columna extra.

3. Para cada `Invoice` colgada, consultar ARCA via `FECompConsultar` con el numerador del PV + tipo de NC + correlativo. Si ARCA dice:
   - `A` (Aprobado): actualizar `Invoice.Resultado = 'A'` + `CAE`, disparar el callback de InvoiceAnnulmentBcBridge (FC1.2 path que cascade actualiza BC).
   - `R` (Rechazado): actualizar a `R` + log + notify.
   - Sin respuesta o `PENDING`: contador de retries + dejar para el proximo ciclo.

4. **NO modificar `ArcaAnnulmentReconciliationJob`** (si existe). Mantener su scope estrictamente sobre `AnnulmentStatus` flow FC1.2.

5. **Sub-fase opt-in F2.6b (NO se asume)**: si despues de prender Fase 2 se descubre que existian NCs FC1.2 huerfanas (sin reconciliacion automatica desde antes de FC1.2.7), eso es un trabajo SEPARADO. NO se mete en F2.6a porque mezcla scopes y aumenta riesgo.

**Criterio de aceptacion FC1.3.F2.6a**:

- **RH2-008 cerrado round 4**: `ArcaAnnulmentReconciliationJob` confirmado NO existe en `src/` (solo comentario aspiracional en `Invoice.cs:62`). El job nuevo es la unica implementacion presente para reconciliar NCs FC1.3 — sin riesgo de coupling.
- Test integration `ReconciliationJob_PartialNcStuckInPending_QueriesArcaAndUpdatesStatus`.
- Test integration `ReconciliationJob_PartialNcSucceededInArca_TransitionsBCToAwaitingFiscal`.
- Test integration `ReconciliationJob_DoesNotTouchFc12NcTotal`: seed factura + NC total FC1.2 en Pending. Job F2.6a NO la toca (responsibility seria de un job FC1.2 separado si llegara a implementarse — fuera de scope Fase 2).
- Test integration `ReconciliationJob_RetryCounter_NotifiesAfterNAttempts`.

**Dependencias**: FC1.3.F2.2 (emite las NCs parciales que este job va a reconciliar).

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
| `src/TravelApi.Application/DTOs/AfipDtos.cs` (MODIFICA, sumar record `ArcaCompoundQueryResult`) | F2.2 | **RH3-001 (round 4) Camino A**: DTO nuevo para el resultado de `QueryLastAuthorizedWithDetailsAsync`. Record con `Found, LastNumero, Cae, CbteAsoc, IssuedAt, ImporteTotal, MonId, MonCotiz`. Usado por la capa 1.5 stale key recovery. |
| `src/TravelApi.Application/DTOs/Cancellation/PartialCreditNoteLineDto.cs` | F2.2 | Una linea individual de la NC parcial (Description, Quantity, Total, AlicuotaIvaId). |
| `src/TravelApi.Infrastructure/Services/PartialCreditNoteIvaCalculator.cs` | F2.2 | Helper puro para prorratear IVA segun mode. Testeable sin DB. |
| `src/TravelApi.Infrastructure/Services/PartialCreditNotePostingReconciliationJob.cs` | F2.6a | Job NUEVO de reconciliacion ARCA para NCs parciales en Pending (cierra RH-006). **RH2-005 (round 3)**: path corregido de `Jobs/` a `Services/` para alinear con la convencion existente (`PartialCreditNoteBridgeReconciliationJob` esta en `Services/`). |
| `src/TravelApi.Infrastructure/Services/FC13Phase2DualFlowJob.cs` | F2.4 [GATED] | Solo si gate G-F2-A se abre. Job step 2 para casos TotalPlusNewInvoice. **RH2-005 (round 3)**: path corregido. |
| `tools/sql/fase2-m1-prevalidation-metadata.sql` | F2.1.0 | Script standalone de validacion pre-backfill (cierra RH-001). NO migracion. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M0_AddFc13Phase2Settings.cs` | F2.0 | 4 cols settings + validacion + `ReviewRequiredReason.HasProvincialTributes` (no requiere col DB, es bitflag int). |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M1_AddFiscalLiquidationOwnedVoAndBackfill.cs` | F2.1 + F2.5 | 10 cols owned VO + CHECK suma + CHECK consistency (RH3-003 igualdad exacta sin tolerancia) + backfill SQL en DOS PASOS atomicos (RH-001, paso 5.B lee `bc.LiquidationComputedAt` directamente) + tabla `ArcaIdempotencyKeys` para idempotencia ARCA (RH-004) con columna `LastSeenNumeroBeforePost int NULL` (RH3-001 round 4 Camino A) + **RH2-002 (round 3)**: 2 cols `Invoice.MonId (nvarchar(3) DEFAULT 'PES')` + `Invoice.MonCotiz (numeric(18,6) DEFAULT 1)` para multimoneda en SOAP envelope. M1 NO se aplica hasta que F2.1.0 paso con count = 0. **Decision M1 (cierra M1 reviewer)**: 1 migracion fisica con todo (mismo patron Fase 1) — el snapshot EF queda monolitico, se acepta a cambio de tener atomicidad para el rollback. Justificacion en §T6.4. |
| `src/TravelApi.Infrastructure/Persistence/Migrations/App/{ts}_Fase2_M2_AddDualFlowSupport.cs` | F2.4 [GATED] | Solo si gate G-F2-A se abre. `PostCancellationInvoiceId` + enum status 12. |
| `src/TravelApi.Tests/Unit/PartialCreditNoteIvaCalculatorTests.cs` | F2.2 | 12 unit tests. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteRealEmissionTests.cs` | F2.2 + F2.3 | 12 integration tests del flow real. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteBackfillTests.cs` | F2.1 | Tests del backfill. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteDualFlowTests.cs` | F2.4 [GATED] | 8 tests del flow dual — solo si gate G-F2-A se abre. |
| `src/TravelApi.Tests/Cancellation/Integration/PartialCreditNoteReceiptCascadeTests.cs` | F2.3 | 3 tests del cascade NC parcial (RH-005 + G-F2-D multi-recibos). |
| `docs/explicaciones/YYYY-MM-DD-fc1-3-fase-2-implementacion.md` | F2.7 | Doc trainee cierre Fase 2. |

### T4.2 Archivos MODIFICADOS

| Archivo | Sub-fase | Que cambia |
|---|---|---|
| `src/TravelApi.Domain/Entities/OperationalFinanceSettings.cs` | F2.0 | + 5 settings nuevos (round 3 suma `IdempotencyKeyStaleThresholdMinutes` por RH2-004). |
| `src/TravelApi.Domain/Entities/Invoice.cs` | F2.5 | + `MonId (nvarchar(3) DEFAULT 'PES')` + `MonCotiz (numeric(18,6) DEFAULT 1)` (cierra RH2-002). Cambios DEFAULT garantizan back-compat FC1.2. |
| `src/TravelApi.Application/DTOs/CreateInvoiceRequest.cs` | F2.5 | + `MonId` (default `"PES"`) + `MonCotiz` (default `1m`) (cierra RH2-002). Props opcionales — callers FC1.2 mandan defaults. |
| `src/TravelApi.Domain/Entities/ReviewRequiredReason.cs` | F2.0.G-F2-C | + `HasProvincialTributes = 1 << 11` (cierra G-F2-C). |
| `src/TravelApi.Domain/Entities/BookingCancellation.cs` | F2.1 + F2.4 [gated] | + `FiscalLiquidation` owned VO + (gated) `PostCancellationInvoiceId`. |
| `src/TravelApi.Domain/Entities/BookingCancellationStatus.cs` | F2.4 [GATED] | + `PartialFiscalAwaitingNewInvoice = 12` SOLO si gate G-F2-A abierto. |
| `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` | F2.1 | + config owned VO `FiscalLiquidation` con prefix + tabla `ArcaIdempotencyKeys` (RH-004) con columna nueva `LastSeenNumeroBeforePost int NULL` (RH3-001 round 4 Camino A). |
| `src/TravelApi.Application/Interfaces/IInvoiceService.cs` | F2.2 | + `EnqueuePartialCreditNoteAsync`. |
| `src/TravelApi.Application/Interfaces/IAfipService.cs` | F2.2 | **RH3-001 (round 4) Camino A**: + `QueryLastAuthorizedWithDetailsAsync(int puntoVenta, int cbteTipo, int? lastSeenNumeroBeforePost, CancellationToken ct)`. Devuelve `ArcaCompoundQueryResult`. Usado por la capa 1.5 stale key recovery. |
| `src/TravelApi.Application/DTOs/AfipDtos.cs` | F2.2 | **RH3-001 (round 4) Camino A**: extender `AfipVoucherDetails` con campos OPCIONALES nullable `Cae`, `CbteAsoc`, `IssuedAt`, `MonId`, `MonCotiz` para soportar la respuesta enriquecida de `GetVoucherDetails`. **NO romper otros callers** — todos los campos nuevos son nullable y el parseo SOAP los deja `null` si no estan en el response. |
| `src/TravelApi.Infrastructure/Services/InvoiceService.cs` | F2.2 + F2.4 [gated] | + `EnqueuePartialCreditNoteAsync` + `ProcessPartialCreditNoteJob` + (gated) `EnqueueNewInvoiceForRemainder`. |
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | F2.3 + F2.4 [gated] | Reemplazar lineas 1431-1468 con flow nuevo (gated por flag F2) + persistir doble representacion FiscalLiquidation (RH-002) + (gated) handler dual flow casos 4/7. |
| `src/TravelApi.Infrastructure/Services/FiscalLiquidationCalculator.cs` | F2.0.G-F2-C + F2.5 | + deteccion `HasProvincialTributes` (G-F2-C). + quitar disparador `MultiCurrency` cuando F2 ON. |
| `src/TravelApi/Program.cs` | F2.0 + F2.6a | + registro `PartialCreditNotePostingReconciliationJob` + validacion startup pre-condicion F2 requiere F1. |
| `src/TravelApi.Application/DTOs/Cancellation/BookingCancellationDto.cs` | F2.1 | + exposicion del FiscalLiquidation. |
| `src/TravelApi.Application/Constants/AuditActions.cs` | F2.3 + F2.4 [gated] | + `BookingCancellationPartialCreditNoteEmitted`, **`PartialCreditNoteEconomicReversalNoCascade`** (G-F2-D + RH-005), + (gated) `BookingCancellationFiscalDualStep1Submitted`, `BookingCancellationFiscalDualStep2Submitted`, `BookingCancellationDualFlowForceStep2`. |
| `src/TravelApi.Infrastructure/Services/AfipService.cs` | F2.2 + F2.3 + F2.5 | **F2.2 (RH3-001 round 4 Camino A)**: sumar metodo nuevo `QueryLastAuthorizedWithDetailsAsync` que internamente reusa `GetNextVoucherNumber` (cambia visibilidad a internal o lo invoca via un helper) + `GetVoucherDetails`. Extender el parseo SOAP de `GetVoucherDetails` (linea 1195+) para mapear nodos `CodAutorizacion -> Cae`, primer item del array `CbtesAsoc -> CbteAsoc`, `CbteFch -> IssuedAt` (formato `yyyyMMdd`), `MonId`, `MonCotiz` cuando esten presentes en el response. Estos campos son OPCIONALES en `AfipVoucherDetails` y se dejan `null` si no aparecen. **F2.3 (RH-005)**: modificar `ApplyCreditNoteEconomicReversalAsync` (linea 1006-1114) para distinguir NC total vs parcial — discriminacion **primaria por `bc.CreditNoteKind`** (RH2-003 round 3), fallback por monto solo cuando BC no esta asociado. NC parcial NO cascade-voider receipts + audit `PartialCreditNoteEconomicReversalNoCascade` con `liveReceipts` cargados via `_context.PaymentReceipts.Where(r => r.Payment.RelatedInvoiceId == invoice.OriginalInvoiceId && r.Status == PaymentReceiptStatuses.Issued)` (cierra RH2-006). **F2.5 (M3 reviewer + RH2-002 round 3 + RH3-002 round 4)**: tarea 0 grep auditoria de `MonId/MonCotiz` (confirma solo lineas 879-880). `CreatePendingInvoice` (linea 564+) puebla `invoice.MonId` + `invoice.MonCotiz` desde `request.MonId` + `request.MonCotiz`. `ProcessInvoiceJob` (linea 729+) lee `invoice.MonId/MonCotiz` (ya disponibles en la entidad cargada) e interpola en el XML SOAP linea 879-880 (`<MonId>{invoice.MonId}</MonId><MonCotiz>{invoice.MonCotiz.ToString("0.000000", CultureInfo.InvariantCulture)}</MonCotiz>`). **Callers a auditar para M3**: (a) `InvoiceService.EnqueueAnnulmentAsync` -> arma `CreateInvoiceRequest` sin setear MonId/MonCotiz, usa defaults. (b) Frontend al crear factura nueva manual -> JSON no incluye campos -> defaults aplican. (c) Tests integration de FC1.2 -> usan defaults. Criterio aceptacion M3 (RH2-002): tests `Fc12NormalInvoice_StillEmitsWithPesos` + `Fc12Annulment_StillEmitsWithPesos` verdes confirmando defaults `MonId="PES"`, `MonCotiz=1`. Criterio aceptacion RH3-002: grep post-refactor sin ocurrencias hardcoded fuera de comentarios. |

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
| ON | ON | ON | OFF | **Estado objetivo Fase 2 base (sin F2.4)**: NC parcial real al ARCA para casos 1/2/3/5/6/8. Casos 4/7 throw segun GR-001. |
| ON | ON | ON | ON | Solo si gate G-F2-A se abre y F2.4 se implementa en sesion separada. Flow completo. |
| ON | ON | OFF | ON | **RECHAZO startup** (F2.4 requiere F2 emision). |
| cualquiera | OFF | ON | cualquiera | **RECHAZO startup** (F2 requiere F1). |

### T6.3 Secuencia de prendido recomendada

1. **Pre-condicion**: Fase 1 ya esta ON en staging y prod (post merge de Fase 1, sin incidentes durante N dias).
2. **Pre-step**: correr el script F2.1.0 contra dump staging Y prod. Documentar count = 0. Si > 0, limpiar antes.
3. **Mergeamos PR Fase 2** con TODOS los flags F2 en `false`. App arranca normal. Sin cambio de comportamiento.
4. **Staging**: admin prende `EnablePartialCreditNoteRealEmission=true`. Probar contra ARCA homologacion con casos 1, 2, 3, 5, 6, 8. Casos 4/7 siguen tirando — OK (esperado).
5. **Staging duracion**: 1-2 ciclos de cancelaciones reales (segun decida Gaston).
6. **Prod**: admin prende `EnablePartialCreditNoteRealEmission=true`. Validar primeras 5 NCs parciales reales manualmente con contador.
7. **30 dias despues**: correr query F2.4.0 (medicion volumen casos 4 y 7). Si >= 5%, abrir sesion separada para F2.4. Si < 5%, F2.4 queda en backlog.

### T6.4 Rollback granular

| Escenario | Accion | Impacto |
|---|---|---|
| NC parcial real explota (ARCA rechaza por XML invalido, prorrateo IVA mal, etc.) | Apagar `EnablePartialCreditNoteRealEmission`. | Vuelve al fallback FC1.2 con log warning. NCs ya emitidas en ARCA quedan validas (no revertibles desde nuestro lado). Los BC nuevos siguen pasando por manual review pero al aprobar emiten NC total. |
| Flow dual explota (solo si gate G-F2-A se abrio en el futuro) | Apagar `EnableTotalPlusNewInvoiceAutoProcessing`. | Casos 4/7 vuelven a tirar throw. BCs que ya estaban en `PartialFiscalAwaitingNewInvoice (12)` quedan en limbo — admin los maneja manualmente fuera del sistema o con endpoint force-step2 una vez que el bug este fixeado. |
| Settings F2 entran con valores invalidos por restore de backup | Validacion startup detecta + app no arranca. Operador debe ajustar manualmente la BD antes de re-arrancar. | Defense-in-depth, mismo patron GR-002. |
| Reverse migracion M1 (RH-002 critico) | **PRECONDICION OBLIGATORIA**: verificar que el doble-write Metadata + columnas sigue activo en el codigo desde F2.1. Si una version intermedia dejo de escribir Metadata, NO reversear M1 — los datos solo viven en columnas y el reverse los destruye. **Script previo**: contar BCs con `FiscalLiquidation_FiscalAmountToCredit IS NOT NULL` y validar que en `ApprovalRequest.Metadata` JSON existen las claves criticas (`fiscalAmountToCredit`, `originalInvoiceAmount`, `currency`). Si la validacion falla, **abortar reverse**. | Sin la precondicion, riesgo alto de perdida de datos fiscales. Con la precondicion, bajo riesgo. |
| Reverse migracion M0 | Aditivo (columnas nullable + nuevo flag bitwise sin DB col). Reverse seguro. | Bajo riesgo. |

**Decision M1 monolitica vs separada (cierra M1 reviewer)**:

El reviewer planteo si separar las migraciones de Fase 2 para que el snapshot EF no colapse. La decision es **mantener M1 monolitica** (10 cols owned VO + CHECK + tabla `ArcaIdempotencyKeys` + backfill en una migracion fisica), siguiendo el patron Fase 1 ya validado. Justificacion:

- La atomicidad del backfill + CHECK + tabla auxiliar es load-bearing: si M1 se aplica a medias, queda data inconsistente.
- Separar las migraciones implica regenerar snapshot EF varias veces y aumenta el riesgo de divergencias entre el snapshot y la realidad.
- El costo de tener una migracion EF monolitica grande es asumible — ya pasamos por eso en Fase 1.
- Si la herramienta EF colapsa al generar la migracion (problema reportado en el pasado), el remediation es generar las clases manualmente o splittear el archivo `.cs` resultante por bloques sin separar la migracion logica.

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

1. `§12.1` — Owned VO `FiscalLiquidation` (promueve §2.4): 10 columnas + CHECK suma + backfill. **Sub-seccion §12.1.a (minor reviewer round 1)**: documentar que la dualidad Metadata JSON + columnas dedicadas es decision deliberada (NO agujero a cerrar). Justificacion: doble-write soporta reverse seguro de M1 + permite queries SQL directas + preserva trazabilidad historica de FC1.2.
2. `§12.2` — `EnqueuePartialCreditNoteAsync` + `ProcessPartialCreditNoteJob` (aditivo a `IInvoiceService`) + idempotencia ARCA de dos capas (`ArcaIdempotencyKeys` + `FECompUltimoAutorizado` opcional).
3. `§12.3` — `IvaProrrateoMode` enum + default + comportamiento.
4. `§12.4` — Estado `PartialFiscalAwaitingNewInvoice (12)` + flow dual **GATED por criterio cuantitativo < 5%** (G-F2-A).
5. `§12.5` — Multimoneda: el flag `MultiCurrency` cambia comportamiento (review manual -> auto) cuando F2 ON.
6. `§12.6` — 4 settings nuevos + validacion startup.
7. `§12.7` — Decisiones cerradas Fase 2 (mismo formato que GR-001..GR-006). Incluye **G-F2-A** (F2.4 gated), **G-F2-C** (tributos provinciales -> manual review obligatorio + nuevo flag `HasProvincialTributes`), **G-F2-D** (multi-recibos -> NC parcial NO cascade).
8. `§12.8` — Open questions de Fase 2 (no las del contador, las arquitectonicas). OQ-3 y OQ-4 cerradas round 2. OQ-1, OQ-2, OQ-5 abiertas. OQ-E (TC multimoneda) sumada al mensaje contador round 3.
9. `§12.9` — Politica cascade receipts NC parcial vs NC total: distincion en `ApplyCreditNoteEconomicReversalAsync`. NC parcial preserva receipts vivos + audit `PartialCreditNoteEconomicReversalNoCascade`. UI Fase 3 (fuera de scope) habilita revocacion manual.

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
| **Compartir `Tributes` de la factura origen 1:1 en la NC parcial** | Tributos provinciales (IIBB, etc.) tienen reglas de proration complejas que dependen de la jurisdiccion. Fase 2 NO maneja. **Round 2 (G-F2-C)**: ademas, si la factura origen tiene `Tributes.Any()`, NUNCA se llega a emitir NC automatica — el calculator dispara manual review obligatorio. | NC parcial Fase 2 emite SIN tributos + manual review obligatorio cuando la factura origen tenia tributos. OQ-3 cerrada. |
| **Cascade-voider receipts automatico en NC parcial** | RH-005 + G-F2-D: el matching por `Amount == invoice.ImporteTotal` falla naturalmente para NC parcial (monto distinto) y multi-recibos (comun en MagnaTravel) hace que la seleccion automatica de "que receipt voider" sea arbitraria. | NC parcial NO hace cascade. Crea reversal con `OriginalPaymentId = null` + audit explicito. UI Fase 3 permitira marcar manualmente. |
| **Implementar F2.4 (flow dual TotalPlusNewInvoice) en el PR Fase 2 base** | G-F2-A: complejidad muy alta (estado intermedio, job de reconciliacion, endpoint admin force, idempotencia entre dos eventos atomicos). Si los casos 4/7 son raros, costo de mantenimiento > beneficio. | F2.4 GATED por criterio cuantitativo < 5%. Default OFF estricto. Casos 4/7 siguen rechazandose con `InvalidOperationException` (GR-001). |
| **Extender `ArcaAnnulmentReconciliationJob` (FC1.2) para incluir NCs parciales** | RH-006: el job FC1.2 reconcilia `AnnulmentStatus` (modelo de datos de NC total). NC parcial reconcilia `Resultado` de la NC nueva (modelo distinto). Acoplar dos casos de uso en un mismo job introduce riesgo de regresion FC1.2. | Job NUEVO `PartialCreditNotePostingReconciliationJob` con scope independiente. |

---

## T8. Open questions / dependencias del contador

**Importante**: estas son las open questions de **arquitectura tecnica**. Las del contador (5 preguntas fiscales F1..F5 + 8 confirmaciones profesionales + 1 confirmacion G4 = 14 puntos round 3) estan en `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md` y las fiscales se mapean a F1..F5 de §T1.3 (F5 se sumo en round 2 sobre TC en NC parcial multimoneda — ver OQ-E).

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

### OQ-3 [RESUELTA round 2 por G-F2-C]: Tributos provinciales (IIBB) en NC parcial

**Status round 1**: pendiente. **Status round 2**: **CERRADA por G-F2-C**.

**Decision Gaston (G-F2-C)**: tributos provinciales -> manual review obligatorio. El sistema NO prorratea tributos provinciales automaticamente. Si la factura origen tiene `Tributes.Any() == true`, el calculator (Fase 1 modificado por F2.0.G-F2-C) suma `ReviewRequiredReason.HasProvincialTributes` al flag acumulado. El admin revisa manualmente y, si decide emitir NC, lo hace fuera del sistema o con override scoped (Fase 3).

**Verificacion en codigo**: ver `FiscalLiquidationCalculator` (Fase 1) — debe sumar el flag en STEP 1 cuando `invoice.Tributes != null && invoice.Tributes.Any()`. Tests cubrieron en F2.0.G-F2-C.

### OQ-4 [RESUELTA round 2 por G-F2-D]: Que pasa con `Receipt` cuando emitimos NC parcial?

**Status round 1**: parcialmente planteada. **Status round 2**: **CERRADA por G-F2-D + RH-005**.

**Contexto verificado en codigo**: `AfipService.ApplyCreditNoteEconomicReversalAsync` (linea 1006-1114) hace matching por `Amount == invoice.ImporteTotal` (linea 1036). Para NC parcial este matching nunca encuentra payment exacto. Ademas Gaston confirmo (G-F2-D) que **multi-recibos es comun** en MagnaTravel (1 factura pagada con 3+ payments).

**Decision G-F2-D**: NC parcial NO hace cascade automatico de receipts. El reversal se crea con `OriginalPaymentId = null` por diseno. Los receipts quedan vivos. Audit `PartialCreditNoteEconomicReversalNoCascade` con la lista de receipt IDs para trazabilidad. UI Fase 3 (fuera de scope) permitira al admin marcar manualmente que receipt anular.

**Implementacion**: ver §FC1.3.F2.3 punto 5 — modifica `ApplyCreditNoteEconomicReversalAsync` para distinguir NC total vs parcial.

### OQ-5: Comision vendedor sobre `FinalNetInvoiced` (G6 cerrada Fase 1)

**Pregunta**: cuando la NC parcial real se emite, donde se dispara el ajuste de comision vendedor sobre `FinalNetInvoiced`?

**Mi propuesta**: NO en Fase 2. La comision es un calculo del modulo de comisiones (fuera de FC1.3). Esa parte lee `bc.FiscalLiquidation.FinalNetInvoiced` cuando le toca recalcular. Fase 2 garantiza que `FinalNetInvoiced` esta persistido y disponible — el resto es trabajo de otro modulo.

**Plan**: agregar nota en doc trainee.

### OQ-E [NUEVA round 2]: TC en NC parcial multimoneda

**Pregunta para el contador** (sumada al mensaje round 3, ver §T9 trazabilidad):

> Cuando emitimos NC parcial sobre una factura USD, el `<MonCotiz>` del XML va con el TC del comprobante original (TC del dia en que se emitio la factura) o con el TC del dia de la NC?
> Nuestra propuesta default: TC del comprobante original (regla fiscal estandar de coherencia).

**Bloqueante de**: F2.5 (multimoneda). Hoy `FiscalSnapshot.ExchangeRateAtOriginalInvoice` se carga al confirmar (verificado FC1.2). Nuestra implementacion default usa este snapshot. Si el contador rechaza, ajustar `ProcessPartialCreditNoteJob` para leer TC del dia de la NC en su lugar.

---

## T9. Trazabilidad

- **ADR base**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\adr\ADR-009-partial-credit-note.md` (Fase 2 sera extension `§12 Fase 2 Amendments`, decision validada por reviewer round 1).
- **Plan tactico Fase 1**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\architecture\plan-tactico-fc1-3.md`.
- **Doc cierre Fase 1**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-22-fc1-3-fase-1-implementacion-completa.md`.
- **Mensaje contador round 3 (incluye OQ-E TC multimoneda agregada round 2)**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\operations\2026-05-21-mensaje-contador-fc1-3-round-3.md` (F1..F5 son las preguntas que ajustan Fase 2).
- **Criterio contador NC parcial**: `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\docs\explicaciones\2026-05-21-criterio-contador-nc-parcial-y-nuevo-agente.md` (matriz 8 casos).
- **Patron `EnqueueAnnulmentAsync`** (FC1.2 vigente, base de la replica F2.2): `src\TravelApi.Infrastructure\Services\InvoiceService.cs:469-583`.
- **Patron `ProcessAnnulmentJob`** (FC1.2 vigente, base de `ProcessPartialCreditNoteJob`): `src\TravelApi.Infrastructure\Services\InvoiceService.cs:585+`.
- **Helper canonico mapeo NC**: `src\TravelApi.Domain\Entities\InvoiceComprobanteHelpers.cs:77-84` (`GetCreditNoteTypeForInvoice`). Verificado round 2: solo Factura A/B/C, Factura M (51) NO soportada.
- **Patron `CreatePendingInvoice` con `<CbtesAsoc>`**: `src\TravelApi.Infrastructure\Services\AfipService.cs:827-838` (XML del bloque CbtesAsoc).
- **`<MonId>/<MonCotiz>` actuales hardcoded**: `src\TravelApi.Infrastructure\Services\AfipService.cs:879-880`. F2.5 los parametriza.
- **`ApplyCreditNoteEconomicReversalAsync` (target de RH-005 + G-F2-D)**: `src\TravelApi.Infrastructure\Services\AfipService.cs:1006-1114`. Matching exacto por Amount linea 1036, cascade receipts linea 1067-1084.
- **Patron Owned VO con prefijo**: `src\TravelApi.Infrastructure\Persistence\AppDbContext.cs` (`FiscalSnapshot` config, leido durante Fase 1).
- **Patron job de reconciliacion**: `PartialCreditNoteBridgeReconciliationJob` (FC1.3 Fase 1) — base para el job NUEVO `PartialCreditNotePostingReconciliationJob` de F2.6a. **NO es extension** del job FC1.2.
- **Patron endpoint force con `InvariantOverride`**: ADR-009 §2.12 + `BookingCancellationService.cs:261-281`.
- **Patron counters Serilog**: FC1.2.7b en HEAD `5c60ce8`.
- **Patron validacion startup pre-condicion**: ADR-009 §2.10 GR-002 (validacion FC1.3 requiere FC1.2).
- **Enum `ReviewRequiredReason` (target G-F2-C)**: `src\TravelApi.Domain\Entities\ReviewRequiredReason.cs`. Ultimo valor actual: `Other = 1 << 10`. G-F2-C suma `HasProvincialTributes = 1 << 11`.

---

## T10. Tabla de cierre — bloqueantes + decisiones (round 2 + round 3)

### T10.1 Cierre de bloqueantes RH-001..RH-006

| ID | Titulo del bloqueante | Donde se cierra en el plan v2 | Test de evidencia |
|---|---|---|---|
| **RH-001** | Backfill SQL F2.1 sin validar schema Metadata | §FC1.3.F2.1.0 (script standalone) + §FC1.3.F2.1 punto 5.A (pre-check intra-migracion con `RAISE EXCEPTION` si count > 0). | `Backfill_MissingKey_RaisesAndAborts`, `Backfill_MalformedMetadata_RaisesAndAborts`. |
| **RH-002** | Rollback F2.1 inseguro si calculator solo escribe columnas | §FC1.3.F2.1 punto 6 (doble-write obligatorio Metadata + columnas) + §T6.4 (precondicion previa al reverse M1). | `Confirm_PostFase2_BothRepresentationsMatch`, `EditLiquidation_PostFase2_UpdatesBothRepresentations`. |
| **RH-003** | Error factual Factura M (tipo 51) | §FC1.3.F2.2 punto 4 mapeo NC (reusa `InvoiceComprobanteHelpers.GetCreditNoteTypeForInvoice` que ya rechaza 51) + nota explicita "Factura M fuera de scope Fase 2". | `EnqueuePartialCreditNoteAsync_FacturaM_Rejects`. |
| **RH-004** | Idempotencia ARCA pos-reintento Hangfire | §FC1.3.F2.2 punto 4 (capa 1: tabla `ArcaIdempotencyKeys` con UNIQUE constraint; capa 2: `FECompUltimoAutorizado` opcional). | `ProcessPartialCreditNoteJob_HangfireRetryAfterTimeout_DoesNotEmitDuplicate`, `ProcessPartialCreditNoteJob_IdempotencyKey_UniqueViolation_AbortsCleanly`. |
| **RH-005** | Cascade receipts roto en NC parcial | §FC1.3.F2.3 punto 5 (modificacion `ApplyCreditNoteEconomicReversalAsync` para distinguir NC total vs parcial; NC parcial NO cascade-voider). Alineado con G-F2-D. | `ApplyCreditNoteEconomicReversal_NcTotal_StillCascadesReceipt` (regression), `ApplyCreditNoteEconomicReversal_NcParcial_SinglePayment_NoCascade`, `ApplyCreditNoteEconomicReversal_NcParcial_MultiPayments_NoCascade`, `OnApprovedAsync_Fase2_PartialNc_MultiplePaymentsScenario_NoCascade_LeavesAuditTrail`. |
| **RH-006** | Job ARCA reconciliation NO existe | §FC1.3.F2.6a (job NUEVO `PartialCreditNotePostingReconciliationJob`, replica patron `PartialCreditNoteBridgeReconciliationJob`). NO extension. Removida la referencia incorrecta a `ArcaAnnulmentReconciliationJob` en §T9. | `ReconciliationJob_PartialNcStuckInPending_QueriesArcaAndUpdatesStatus`, `ReconciliationJob_DoesNotTouchFc12NcTotal`, `ReconciliationJob_RetryCounter_NotifiesAfterNAttempts`. |

### T10.2 Decisiones nuevas G-F2-A/C/D

| ID | Decision Gaston | Donde se aplica en el plan v2 |
|---|---|---|
| **G-F2-A** | F2.4 GATED por criterio < 5% post-prod | §FC1.3.F2.4 reescrita (status GATED, criterio cuantitativo, query SQL F2.4.0). §T2 (orden recomendado mueve F2.4 fuera del PR Fase 2 base). §T6.2 (combinaciones validas reformuladas). §T6.3 (secuencia de prendido sin F2.4 obligatoria). |
| **G-F2-C** | Tributos provinciales -> manual review obligatorio | §FC1.3.F2.0.G-F2-C nueva sub-fase. Nuevo flag `ReviewRequiredReason.HasProvincialTributes = 1 << 11`. Calculator modificado. OQ-3 cerrada. §T4.2 actualizado para incluir el modify de `ReviewRequiredReason.cs` y `FiscalLiquidationCalculator.cs`. |
| **G-F2-D** | Multi-recibos es comun, NC parcial NO cascade receipts | §FC1.3.F2.3 punto 5 (modificacion `ApplyCreditNoteEconomicReversalAsync`). Audit `PartialCreditNoteEconomicReversalNoCascade`. Test integration explicito con 1 factura $1.000 + 3 payments + NC $250 -> 0 receipts cascade. OQ-4 cerrada. |

### T10.3 Cierre de majors M1..M4 + minors

| ID | Descripcion | Cierre |
|---|---|---|
| **M1** | EF tooling puede colapsar al generar M1 monolitica | §T4.1 nota explicita: "1 migracion fisica con todo (mismo patron Fase 1)". §T6.4 decision documentada con justificacion. Remediation: si EF colapsa, generar clase manualmente o splittear `.cs` resultante por bloques sin separar la migracion logica. |
| **M2** | F2.4 "opcional" sin criterio | **RESUELTO por G-F2-A** (criterio cuantitativo < 5%). |
| **M3** | F2.5 cambia firma `CreateInvoiceRequest` | §T4.2 modificacion de `AfipService.cs` lista callers a auditar (`InvoiceService.EnqueueAnnulmentAsync`, frontend, tests FC1.2). Criterio aceptacion explicito: tests integration de callers existentes verdes con defaults `("PES", 1)`. |
| **M4** | Validacion defensiva pre-ARCA tambien pre-encolado | §FC1.3.F2.2 punto 7 (validacion en `EnqueuePartialCreditNoteAsync` antes de mutar `Invoice.AnnulmentStatus`). Counter separado `Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue`. Test `EnqueuePartialCreditNoteAsync_SumMismatchAtEnqueue_DoesNotMutateInvoiceState`. |
| **Minor doc F2.7** | Tabla "que setting cambiar segun respuesta del contador" | A incluir en §FC1.3.F2.7 doc trainee. |
| **Minor F2.0 nombre tolerance** | `PartialCreditNoteRoundingTolerance` clarificar moneda original | Documentar en §FC1.3.F2.0 doc inline: "expresado en la moneda original del comprobante (no necesariamente ARS)". |
| **Minor enum status 12** | Verificar CHECK enum BD existente | Solo aplica si gate G-F2-A se abre. Si no, no entra en M2. |
| **Minor counter Serilog adicional** | `Fc13.PartialCreditNote.LiquidationSumValidationFailedAtEnqueue` separado | §FC1.3.F2.6 lista de counters actualizada. |
| **Minor ADR-009 §12** | Documentar dualidad Metadata + columnas como deliberada | A incluir en `§12.1` de ADR-009 amendment (sub-task de F2.7). |

### T10.4 Cierre de hallazgos round 3 (RH2-001..RH2-007)

| ID | Severidad | Titulo del hallazgo | Donde se cierra en el plan v3 | Test / evidencia |
|---|---|---|---|---|
| **RH2-001** | BLOQUEANTE | Query SQL §FC1.3.F2.4.0 sobrecuenta casos 4 y 7 por bitflags | §FC1.3.F2.4.0 reescrita usando `bc."CreditNoteKind" = 2` (`CreditNoteKind.TotalPlusNewInvoice`, verificado en `CreditNoteKind.cs:24`). Comentario inline explica la prioridad estricta del calculator. | Manual: comparar output de la query vieja vs nueva sobre dump staging — la nueva da count <= vieja. Para QA: seed 3 BCs (Factura A + RetentionChangesNature, Factura B + OriginalInvoiceUnclear, Factura B sin flags) y verificar que la nueva cuenta solo el segundo. |
| **RH2-002** | BLOQUEANTE | F2.5 multimoneda no toca el XML SOAP hardcoded en `AfipService.cs:879-880` | §FC1.3.F2.5 expandida con 9 tareas atomicas: columnas `Invoice.MonId/MonCotiz` en M1, DTO `CreateInvoiceRequest` con props opcionales default `("PES", 1)`, refactor `CreatePendingInvoice` + `ProcessInvoiceJob` con interpolacion del XML, mapping en `ProcessPartialCreditNoteJob`. §T4.1 + §T4.2 actualizados con todos los archivos. §T2.2 advierte sobre el riesgo de regresion FC1.2. | `Fc12NormalInvoice_StillEmitsWithPesos`, `Fc12Annulment_StillEmitsWithPesos`, `PartialCreditNoteUsd_EmitsWithDolarAndSnapshotRate`, `PartialCreditNoteArs_EmitsWithPesoAndOne`. |
| **RH2-003** | MAJOR | Discriminacion NC total vs parcial fragil por monto | §FC1.3.F2.3 punto 5 reescrito: `bc.CreditNoteKind == PartialOnOriginal` es discriminador **primario**. Comparacion por monto solo como fallback historico para NCs sin BC asociado. Pseudocodigo explicito agregado. Verificado en codigo: `BookingCancellation.CreditNoteInvoiceId` existe (`BookingCancellation.cs:50`). | Tests integration de §FC1.3.F2.3 punto 5 cubren ambas ramas (path canonico + fallback). |
| **RH2-004** | MAJOR | Idempotencia ARCA con key huerfana entre INSERT y POST | §FC1.3.F2.2 punto 4 expandido con "capa 1.5 — stale key recovery". Setting nuevo `IdempotencyKeyStaleThresholdMinutes` (default 10) sumado a `OperationalFinanceSettings` (§FC1.3.F2.0). Logica de recovery con dos ramas (POST viajo -> derivar CAE; POST nunca viajo -> borrar key + retry limpio). | `ProcessPartialCreditNoteJob_KeyOrphanedAfterCrash_AllowsRetryAfterStaleThreshold`. |
| **RH2-005** | MINOR | Ruta del job nuevo inconsistente (`Jobs/` vs `Services/`) | §FC1.3.F2.6a + §T4.1 + §T4.2 actualizados: todas las ocurrencias de `Infrastructure/Jobs/` migradas a `Infrastructure/Services/` (convencion verificada: `PartialCreditNoteBridgeReconciliationJob` esta en `Services/`). | Manual: review de §T4.1 muestra paths corregidos. |
| **RH2-006** | MINOR | Audit `PartialCreditNoteEconomicReversalNoCascade` con `ReceiptsAffected` sin query especificada | §FC1.3.F2.3 punto 5 ahora incluye query EF Core explicita: `_context.PaymentReceipts.Include(r => r.Payment).Where(r => r.Payment.RelatedInvoiceId == invoice.OriginalInvoiceId && r.Status == PaymentReceiptStatuses.Issued)`. Verificado en codigo: `PaymentReceipt.cs`, `Payment.RelatedInvoiceId` (`Payment.cs:50`), `PaymentReceiptStatuses.Issued` (constante string, no enum). | Test integration `ApplyCreditNoteEconomicReversal_NcParcial_MultiPayments_NoCascade` (ya existia en v2) confirma que `liveReceipts` tiene los 3 IDs esperados. |
| **RH2-007** | INFO | "12 puntos round 3" inconsistente con doc real | §T8 corregido a "5 preguntas fiscales (F1..F5) + 8 confirmaciones profesionales + 1 confirmacion G4 = 14 puntos round 3". §T1.4 punto 3 ajustado (la sub-pregunta TC multimoneda ya esta en el mensaje round 3 como F5, no "round 4 futuro"). | Manual: cross-check con `docs/operations/2026-05-21-mensaje-contador-fc1-3-round-3.md` linea 3 ("13 puntos: 4 fiscales nuevas + 8 confirmaciones profesionales + 1 confirmacion G4") + linea 5 (F5 sumada round 2) = 14 puntos efectivos. |

---

### T10.5 Cierre de hallazgos round 4 (RH3-001..RH3-003 + RH2-008)

| ID | Severidad | Titulo del hallazgo | Donde se cierra en el plan v4 | Test / evidencia |
|---|---|---|---|---|
| **RH3-001** | BLOQUEANTE | Capa 1.5 stale key recovery invocaba metodo + DTO que no existen | §FC1.3.F2.2 capa 1.5 reescrita con sub-tareas A.1..A.7 (Camino A robusto, Gaston). DTO nuevo `ArcaCompoundQueryResult` (§T4.1 + §T4.2 `AfipDtos.cs`). Metodo nuevo `QueryLastAuthorizedWithDetailsAsync` en `IAfipService` (§T4.2). Extension de `GetVoucherDetails` SOAP parsing para sumar `Cae`, `CbteAsoc`, `IssuedAt`, `MonId`, `MonCotiz` como nullable. Columna `LastSeenNumeroBeforePost int NULL` en `ArcaIdempotencyKeys` (M1 + §T4.2 `AppDbContext.cs`). Counter `Fc13.PartialCreditNote.RecoveredFromStaleKey` (§FC1.3.F2.6). | `ProcessPartialCreditNoteJob_KeyOrphanedPostNeverArrived_DeletesKeyAndRetries`, `ProcessPartialCreditNoteJob_KeyOrphanedPostArrived_RecoversFromArca`, `ProcessPartialCreditNoteJob_KeyOrphanedMismatchAmount_TreatsAsPostNeverArrived`. |
| **RH3-002** | MINOR | Auditoria `<MonId>/<MonCotiz>` faltante pre-refactor F2.5 | §FC1.3.F2.5 tarea atomica 0 (pre-refactor): correr `grep -n "MonId\|MonCotiz" src/TravelApi.Infrastructure/Services/AfipService.cs` antes de tocar. Hoy se conoce ocurrencias en linea 879-880; si grep muestra mas, integrarlas. Criterio aceptacion: post-refactor `grep -c "PES\b\|MonCotiz>1"` da cero o solo en comentarios. | Manual: ejecucion del grep documentada en doc trainee F2.7. |
| **RH3-003** | MINOR | CHECK consistency podria dispararse en backfill por divergencia JSON vs columna | Backfill paso 5.B reescrito: `"FiscalLiquidation_ComputedAt" = bc."LiquidationComputedAt"` (lee de columna summary Fase 1, NO del JSON). CHECK `chk_BookingCancellations_fiscalliquidation_consistency` queda como igualdad exacta sin tolerancia. SQL del CHECK explicitado en §FC1.3.F2.1 punto 4. | Test integration `Backfill_FromExistingApprovalMetadata_PopulatesAllColumns` (ya existia v2) ahora valida igualdad exacta entre las dos columnas. |
| **RH2-008** | INFO | Justificacion §FC1.3.F2.6a referenciaba job hipotetico | §FC1.3.F2.6a reescrita. Verificado con `Grep "class ArcaAnnulmentReconciliationJob"` -> 0 matches en `src/`. Solo existe como comentario aspiracional en `Invoice.cs:62` + mencion en migracion legacy `20260514030142_FC1_AddCancellationModule.cs`. Patron replicado: `PartialCreditNoteBridgeReconciliationJob` (si existe y funciona). Deuda historica fuera de scope Fase 2 — sub-fase opt-in F2.6b si llega a requerirse. | Manual: ejecucion del Grep documentada en el changelog v3 -> v4. |

---

**Fin del plan tactico Fase 2 v4 round 4.** Pendiente review de `software-architect-reviewer` round 4 para validar:

1. Cierre efectivo de RH3-001 (Camino A robusto: DTO + metodo + columna nuevos, sin invocaciones a APIs inexistentes).
2. Backfill paso 5.B leyendo `bc.LiquidationComputedAt` directamente — CHECK consistency con igualdad exacta sin riesgo de falso positivo (RH3-003).
3. Pre-refactor F2.5 tarea atomica 0 ejecutada y documentada (RH3-002).
4. Justificacion F2.6a alineada con la realidad del repo: `ArcaAnnulmentReconciliationJob` NO existe (RH2-008).
5. Mantenimiento de los cierres v2 + v3 (RH-001..RH-006 + G-F2-A/C/D + M1..M4 + RH2-001..RH2-007 + minors) sin regresion.
