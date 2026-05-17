# ADR-001 — Sistema de invariantes del dominio (separar permisos de reglas de negocio)

- **Status**: Changes Required (see [ADR-001-review-2026-05-12.md](ADR-001-review-2026-05-12.md))
- **Date**: 2026-05-12
- **Author(s)**: software-architect agent, validado con Gaston
- **Supersedes**: parcialmente el patron `MutationGuards`/`DeleteGuards` (no se borra; se subsume).
- **Related**: B1.15 Roadmap (`project_b115_roadmap.md`), Fase B' workflow aprobaciones, audit 30 invariantes (`project_b115_audit_invariantes.md`).
- **Review status**: REQUIERE_CAMBIOS — 4 bloqueantes B1..B4 + 6 mejoras R1..R6. Detalle en `ADR-001-review-2026-05-12.md`.

## 1. Contexto

MagnaTravel-Cloud arrastra una confusion estructural: el sistema **mezcla permisos de rol con reglas de negocio inviolables**. Casos concretos:

- "El Vendedor no puede anular factura" se presenta como **permiso**. La regla real es: "Nadie anula una factura con CAE viva sin emitir NC. Admin puede solicitar excepcion con motivo + audit". Aplica a TODOS los roles.
- "El Vendedor no edita pagos con factura" se presenta como **guard**. La regla real es: "Nadie edita un Payment ligado a Invoice con CAE viva, porque el monto esta reportado a ARCA".
- `TreasuryService.GetCashSummaryAsync` no incluye en `CashOut` las devoluciones efectivas derivadas de NC (`AfipService.cs:1050` crea `CreditNoteReversal` con `AffectsCash=false`). El dominio colapsa dos eventos: **reversion contable** (no caja) y **devolucion efectiva** (si caja). Bug fiscal critico (rompe INV-CONT-09 caja completa).

Tres expertos de dominio (travel-agency, accounting-argentina, arca-tax-argentina) produjeron **80 invariantes** (32 OPS + 23 CONT + 25 FISC). El catalogo cubre operativa, contabilidad y regimen fiscal argentino. Hoy esas reglas viven:

- Hardcodeadas en services (chequeos ad-hoc dentro de `PaymentService`, `InvoiceService`).
- En helpers parciales (`MutationGuards`, `DeleteGuards`, `EconomicRulesHelper`).
- En policies de autorizacion confundiendo permiso con regla.
- Algunas no existen (devolucion NC efectiva, cierre de periodo, withholdings).

El problema operativo: cuando una regla falla, el usuario ve "no tenes permiso", no "rompe la regla X". El operador no aprende el dominio, el auditor no puede rastrear, y el codigo no tiene una capa identificable de "reglas inviolables del negocio". Es **complacencia arquitectural**: cada bug se resuelve con un `if` puntual, sin contrato.

## 2. Decision

Adoptar un **sistema de invariantes del dominio** explicito, separado del sistema de permisos. Las invariantes son **reglas inviolables** del negocio. Los permisos son **autorizaciones por rol**. Los dos coexisten; nunca se solapan.

### 2.1 Modelo conceptual

- **`IBusinessInvariant`**: regla que evalua un `MutationRequest` y devuelve `Allow` | `Violation(reason)`. Se invoca **antes** de mutar.
- **`IBusinessCascade`**: regla que, ante un evento (NC aprobada, factura emitida, voucher revocado), dispara una **accion automatica** (revocar voucher, registrar `CommissionLedger.Reversed`, etc). Se invoca **despues** de mutar.
- **`InvariantEvaluator`**: orquesta el pipeline. Selecciona invariantes aplicables a la entidad+operacion, las evalua en orden de prioridad, agrega violaciones, decide reject/allow. Soporta `OverrideContext`.
- **`OverrideContext`**: encapsula la valvula de escape (Admin con razon + `ApprovalRequest.Approved` consumida). Hace bypass **solo** de invariantes con `AdmitsOverride = true`, y persiste log especial con `WasForced = true`.
- **`AuditService.LogInvariantEvent`**: registra cada violacion (rechazada o forzada) en `AuditLog` con `Action = "InvariantViolation" | "InvariantOverride"`, `Category = "Invariant"`, `InvariantId` (en `Changes` JSON).

### 2.2 Convencion de codigo

```csharp
[BusinessInvariant("INV-007", priority: Priority.High, admitsOverride: false)]
public sealed class PaymentMustNotBeMutatedWhileInvoiceHasLiveCae : IBusinessInvariant<UpdatePaymentRequest>
{
    private readonly AppDbContext _db;
    public PaymentMustNotBeMutatedWhileInvoiceHasLiveCae(AppDbContext db) { _db = db; }

    public async Task<InvariantResult> EvaluateAsync(UpdatePaymentRequest req, CancellationToken ct)
    {
        var invoiceLive = await _db.Invoices.AnyAsync(...);
        return invoiceLive
            ? InvariantResult.Violation("No se puede editar un pago ligado a factura con CAE vivo. Anular con NC primero.")
            : InvariantResult.Allow;
    }
}
```

- Una clase por invariante en `TravelApi.Application.Invariants.*` (capa Application, no Domain — necesitan `AppDbContext`/services).
- Atributo `[BusinessInvariant(...)]` con el ID canonico, prioridad, override. Trazable por reflection.
- Mensaje al usuario en espanol, accionable, sin jerga tecnica. Catalogo opcional para i18n futura (`Resources/Invariants.{lang}.resx`).

### 2.3 Pipeline en el service

```csharp
public async Task<Result<PaymentDto>> UpdatePaymentAsync(UpdatePaymentRequest req, MutationContext ctx, CancellationToken ct)
{
    var evalResult = await _invariantEvaluator.EvaluateAsync(req, ctx, ct);
    if (evalResult.HasViolations)
    {
        await _audit.LogInvariantViolationsAsync(evalResult, ctx, ct);
        return Result.Conflict(evalResult.PrimaryMessage); // 409
    }

    if (evalResult.WasOverridden)
        await _audit.LogInvariantOverrideAsync(evalResult, ctx, ct); // log especial

    var dto = await ExecuteUpdateAsync(req, ct); // mutacion real

    await _cascadeRunner.RunCascadesAsync(MutationKind.PaymentUpdate, req, ct); // si aplica
    return Result.Ok(dto);
}
```

### 2.4 Performance

- **Bucketing por entidad**: cada invariante declara su `TargetEntity` (Payment, Invoice, Reserva, etc). El evaluator solo carga las relevantes.
- **DI scan al startup**: el contenedor descubre todos los `IBusinessInvariant<T>` y los indexa por entidad. No hay overhead por request mas alla del lookup en diccionario.
- **Lazy dependencias**: cada invariante hace su query solo si esta en el bucket. No se precarga estado.
- **No corremos 80 invariantes por mutacion**. Una mutacion de Payment toca 5-8 invariantes maximo.

### 2.5 Override

- `MutationContext { UserId, IsOverride: bool, OverrideReason?: string, ApprovalRequestId?: int }`.
- Si `IsOverride = true`, el evaluator chequea:
  1. La invariante violada admite override (`AdmitsOverride = true`).
  2. Existe `ApprovalRequest.Approved` no consumida que matchee `RequestType = FrozenEntityMutation`, `RequestedByUserId = ctx.UserId`, `EntityId/Type` correctos.
  3. `OverrideReason.Length >= 20`.
- Si todo OK: marca `evalResult.WasOverridden = true`, consume el `ApprovalRequest`, persiste `AuditLog` especial con `Changes = { invariantId, reason, originalViolation, wasForced: true }`.
- Si falla cualquier check: rechaza con 409 explicando que falta el approval o que la invariante no admite override.

### 2.6 Cascadas

- `IBusinessCascade<TEvent>` con metodo `RunAsync(TEvent evt, CancellationToken ct)`.
- Eventos: `InvoiceAnnulled`, `VoucherRevoked`, `ReservaCancelled`, `PaymentDeleted`, `CreditNoteApproved`.
- Cascadas conocidas hoy (extraidas del codigo y roadmap):
  - `InvoiceAnnulled` → `VoucherRevoker` (revoca vouchers asociados) + `CommissionReverser` (insert `CommissionLedger.Reversed`) + `ReceiptVoider` (set `PaymentReceipt.Voided`).
  - `CreditNoteApproved` con `RefundChoice = EffectiveCash` → `CashMovementCreator` (insert `ManualCashMovement` Expense con `AffectsCash=true`).
  - `CreditNoteApproved` con `RefundChoice = CustomerBalance` → `ReservaBalanceAdjuster` (Reserva.Balance -= amount, no caja).
- Cascadas son **idempotentes** (si ya corrieron, no duplican efecto). Tests de regresion verifican.

## 3. Consecuencias

### 3.1 Positivas

- **Reglas de negocio inviolables visibles en el codigo**: una carpeta `Invariants/` con todas las reglas catalogadas por ID.
- **Trazabilidad**: cada violacion (rechazada o forzada) tiene linea en `AuditLog` con `InvariantId`, motivo, user.
- **Mensajes al usuario explican el dominio**, no el rol ("No se puede X porque hay factura con CAE", no "No tenes permiso").
- **Auditor / contador puede leer las invariantes** sin entender C#: cada clase tiene comentario, ID, prioridad.
- **Testabilidad alta**: cada invariante es una clase aislada con dependencias inyectadas. Test unitario directo.
- **Override seguro**: nunca silencioso. Requiere `ApprovalRequest.Approved` + razon + log especial.
- **Sirve para certificacion fiscal/contable**: el contador puede mapear cada invariante a un control RG.

### 3.2 Negativas

- **Mas codigo**: 60-80 clases para invariantes (despues de consolidacion). Mitigable: convencion estricta + plantilla.
- **Performance a cuidar**: cada mutacion corre N invariantes. Mitigado con bucketing por entidad y lazy queries. Hay que medir.
- **Curva de adopcion**: developers nuevos tienen que entender el patron antes de tocar un service.
- **Riesgo de duplicacion** entre `MutationGuards` viejo y nuevo sistema durante migracion. Mitigado migrando gradualmente y dejando deprecation TODOs.
- **El `InvariantEvaluator` puede convertirse en god-class** si no se cuida. Mitigado manteniendolo como orquestador delgado (solo recolecta y delega).

## 4. Alternativas consideradas

| Alternativa | Pros | Contras | Por que NO |
|---|---|---|---|
| **FluentValidation** | Maduro, conocido | No tiene concepto de override, no audita, no diferencia regla de negocio de validacion de DTO | Sirve para validacion de input, no para invariantes con consultas a BD ni audit/override |
| **DataAnnotations attributes** | Built-in | Solo validan formato, no reglas con estado | Insuficiente |
| **Specification pattern (Ardalis)** | Encapsula reglas reusables | No tiene override engine ni audit integrado | Util como complemento, no como solucion completa |
| **Domain events + handlers** | Reactivo, desacoplado | El evento se dispara DESPUES; las invariantes deben rechazar ANTES | Util para cascadas, no para invariantes preventivas. Lo combinamos. |
| **Reglas en BD (CHECK constraints/triggers)** | Inviolable a nivel storage | Mensajes opacos, dificil de testear, opaco a desarrolladores | Insuficiente para reglas con varias tablas y override |
| **Mantener `MutationGuards` + agregar mas helpers** | Continuidad | No escala a 80 reglas; sin estructura para override ni audit | Es lo que tenemos hoy y no alcanza |
| **CQRS + comandos con behaviors MediatR** | Cross-cutting limpio | Reescritura masiva de services | Sobreingenieria para esta etapa |

**Recomendacion**: `IBusinessInvariant` propio + DI scan + atributo de trazabilidad. Patron simple, especifico al dominio, compatible con la arquitectura modular monolitica actual.

## 5. Migracion / rollback plan

### 5.1 Migracion (incremental, 4 sub-fases)

1. **F3.1 — Infra base**: interfaces, `InvariantEvaluator`, `OverrideContext`, atributo, audit log especial. Sin tocar codigo existente. Tests del evaluator con invariantes ficticias.
2. **F3.2 — Bug central NC + caja**: implementar `INV-CASCADE-NC-RESPONSE` (modal pregunta refund mode al aprobar NC) + cascadas asociadas. Resuelve INV-CONT-09 y devuelve coherencia al Libro de Caja.
3. **F3.3 — Invariantes ALTA prioridad**: migrar los `MutationGuards` actuales (7 chequeos) al nuevo patron. Agregar las invariantes faltantes high-prio (factura unica por reserva, NC no anula NC, etc).
4. **F3.4 — Invariantes MEDIA/BAJA**: el resto del catalogo, en bloques por entidad.
5. **F3.5 — UI**: mensajes 409 dejan de decir "no tenes permiso", muestran el motivo de invariante. Modal NC. Modal override.

### 5.2 Rollback

- Si una invariante migrada rompe operativa: se desactiva con flag `[BusinessInvariant(disabled: true)]` y se hotfixea sin redeploy. La logica vieja (`MutationGuards`) se mantiene en codigo hasta F3.4 para fallback rapido.
- `OverrideContext` es opt-in: si rompe algo, se revierte al pipeline sin override.
- Cada sub-fase entra en su PR. Rollback granular.

## 6. Testing strategy

- **Unit tests por invariante**: setup minimo de BD in-memory, evaluar con/sin condicion, esperar Allow/Violation.
- **Integration tests por pipeline**: endpoint -> evaluator -> rechazo 409 + audit creado. Tests por flujo critico (anular factura, editar pago, etc).
- **Tests de override**: caso happy (con `ApprovalRequest.Approved`) + caso bloqueado (sin approval, con approval expirada, con approval mismatch).
- **Regression suite**: cada bug historico cubierto por su test. INV-CONT-09 (NC + caja) tiene test que verifica `CashOut` incluye refund efectivo.
- **Snapshot tests del catalogo**: lista de IDs registrados se compara contra snapshot. Si alguien borra una invariante sin advertir, falla.

## 7. Operational risks

- **Performance regression**: si una invariante hace query pesada en path caliente. Mitigacion: profiling antes de merge + cache de chequeos repetidos por request.
- **Override mal usado**: Admin forzando todo. Mitigacion: dashboard de overrides + alerta a contador si supera umbral mensual.
- **Cascada de NC + caja mal modelada**: si el operador elige mal el refund mode (saldo vs efectivo). Mitigacion: confirmacion en modal + auditoria + posibilidad de reversa contable supervisada.
- **Datos pre-migracion sin invariantes**: pueden haber registros viejos que violan reglas. Mitigacion: query auditora antes del deploy + plan de remediacion antes de habilitar la invariante.

## 8. Open questions / pre-requisitos

Ver `.claude/agent-memory/software-architect/project_invariantes_open_questions.md`.
