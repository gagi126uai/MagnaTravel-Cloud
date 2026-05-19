# ADR-008 — `IInvoiceAnnulmentBcBridge`: split de interface para romper dependencia circular

- **Status**: ACCEPTED
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2 commit `34d3c7e`)
- **Related**: [ADR-002 cancelacion/refund](ADR-002-cancellation-refund.md) §2.13
  (coordinacion inter-service), plan tactico FC1.2 v3 §2.1.bis + §6.1.bis,
  MR-V2-02 (decision DI explicita).
- **Trazabilidad**: commit `34d3c7e` (FC1.2.1 bridge + DI wiring).

## 1. Contexto

El modulo cancelacion tiene dos servicios que necesitan hablarse en
**ambas direcciones**:

1. `BookingCancellationService.ConfirmAsync` invoca a
   `InvoiceService.EnqueueAnnulmentAsync` para encolar la emision de la
   NC fiscal a ARCA.
2. Cuando ARCA responde (via Hangfire `ProcessAnnulmentJob` o
   `ArcaAnnulmentReconciliationJob`), `InvoiceService` necesita avisarle
   al BC que la NC se confirmo / fallo.

Si `InvoiceService` referencia a `IBookingCancellationService` y este
referencia a `IInvoiceService`, queda **dependencia circular**.

Opciones consideradas:

- **(a)** MediatR / mediator pattern: el InvoiceService publica un
  evento `InvoiceAnnulmentSucceeded`, un handler del BC lo procesa.
  **Descartado**: MediatR no esta en la stack del proyecto
  (verificado por architect). Agregarlo solo para este caso es overkill.
- **(b)** `INotificationDispatcher` propio: idem MediatR pero
  reinventado. **Descartado**: overengineering local.
- **(c)** MassTransit outbox: la outbox MassTransit existe en el repo
  pero es para messaging entre micro-services, no para dispatch
  in-process. **Descartado**.
- **(d)** Interface chica dedicada al lado del InvoiceService que el BC
  implemente, sin la superficie completa del `IBookingCancellationService`.
  **Aceptada**.

## 2. Decision

Crear una interface chica `IInvoiceAnnulmentBcBridge` con SOLO 2 metodos
que el `InvoiceService` necesita llamar:

```csharp
public interface IInvoiceAnnulmentBcBridge
{
    Task OnArcaSucceededAsync(int invoiceId, CancellationToken ct);
    Task OnArcaFailedAsync(int invoiceId, string failureReason, CancellationToken ct);
}
```

`BookingCancellationService` implementa **AMBAS** interfaces:

- `IBookingCancellationService` — la "publica" para UI / controllers /
  otros services.
- `IInvoiceAnnulmentBcBridge` — la "tecnica" para el InvoiceService.

**DI registration** (`Program.cs`, copiado del plan v3 §2.1.bis):

```csharp
// El service concreto se registra una sola vez (scoped).
services.AddScoped<BookingCancellationService>();

// Dos interfaces apuntando a la MISMA instancia (compartido AppDbContext
// + ChangeTracker dentro del scope).
services.AddScoped<IBookingCancellationService>(sp =>
    sp.GetRequiredService<BookingCancellationService>());
services.AddScoped<IInvoiceAnnulmentBcBridge>(sp =>
    sp.GetRequiredService<BookingCancellationService>());
```

**InvoiceService** consume `IInvoiceAnnulmentBcBridge` (no
`IBookingCancellationService`):

```csharp
public InvoiceService(..., IInvoiceAnnulmentBcBridge bcBridge, ...)
{
    _bcBridge = bcBridge;
}

// Tras commit del callback ARCA:
await _bcBridge.OnArcaSucceededAsync(invoice.Id, ct);
```

**Contrato implicito** (documentado inline en `InvoiceService`):

> El `InvoiceService` DEBE llamar al bridge tras `SaveChanges` del
> callback ARCA. Si no lo hace, el BC queda zombie (Invoice en
> `Succeeded`, BC en `AwaitingFiscalConfirmation`). Mitigacion:
> counter `metric:bc_bridge_failed` en el catch, alerta en Grafana.

## 3. Consecuencias

### Positivas

- **Sin MediatR ni outbox in-process**. Mantenemos la stack minima.
- **Interface chica de bridge**: el `InvoiceService` no ve la
  superficie completa del BC (DraftAsync, AbortAsync, ForceArca etc.).
  Reduccion de acoplamiento.
- **Misma instancia dentro del scope**: las dos interfaces resuelven
  a la misma instancia del `BookingCancellationService` (compartido
  AppDbContext + ChangeTracker). Esencial para que una operacion del
  bridge vea los cambios in-memory del scope, si los hay.
- **Smoke test obligatorio** (BR-V2-04):
  `BuildServiceProvider_ResolvesAllServices` verifica que las 2
  interfaces resuelven sin error de DI.
- **Test extra** (commit `34d3c7e`): valida que las 2 interfaces
  resuelven a la MISMA instancia.

### Negativas

- **Contrato implicito**: el `InvoiceService` puede olvidarse de llamar
  al bridge en algun path raro (nueva variante de callback, fix futuro
  que se salta el dispatch). Mitigacion:
  - Counter Serilog `metric:bc_bridge_failed` en el catch del bridge
    (commit `81b8332` F8) — si dispara muchas veces, hay un BC zombie
    sin transicionar.
  - Counter Serilog `metric:cancellation_arca_succeeded` /
    `metric:cancellation_arca_failed` — comparable con
    `metric:cancellation_drafted` para detectar funnel roto.
- **`BookingCancellationService` ahora tiene una "doble vida"**:
  publica + bridge. Quien lee el codigo en 6 meses debe leer el
  comentario en `Program.cs` (MR-V2-02) para entender por que estan
  las 2 interfaces.

### Riesgos

- **Si alguien usa `BookingCancellationService` directamente** (sin
  pasar por la interface), pierde el split. Mitigacion: el `internal`
  / `sealed` no se aplica (necesitamos resolver desde DI), pero el
  consumer normal son los controllers que toman `IBookingCancellationService`.
- **Si en el futuro la superficie del bridge crece** (mas metodos),
  el costo de mantener 2 interfaces sobre el mismo concreto crece. Si
  llega a 5+ metodos, reconsiderar (puede ser señal de que el BC
  deberia partirse en 2 services).

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **MediatR** | No esta en la stack. Agregar el paquete + configurar pipeline solo para este caso es overkill. |
| **INotificationDispatcher propio** | Reinventar MediatR. Mayor superficie de bugs (test del dispatcher + handlers + ordering). |
| **MassTransit outbox** | Latencia (eventos asincronicos), complejidad (publish + subscribe + retry). El BC y el InvoiceService viven en el mismo proceso, no necesitamos messaging. |
| **InvoiceService llama a `IBookingCancellationService` directo** | Dependencia circular. La compilacion fallaria. |
| **BC y Invoice fusionados en un solo service** | Cohesion equivocada. El InvoiceService maneja varios tipos de Invoice (no solo NCs de cancelacion). El BC maneja varios tipos de NC source (no solo cancelacion). |

## 5. Migration plan / rollback

**Migration**: ninguna de schema. Solo cambio DI + interfaces nuevas.

**Rollback**: revertir commit `34d3c7e` deja la dependencia circular
sin resolver — la compilacion fallaria si quedaron callers de
`OnArcaSucceededAsync`. En la practica, rollback requiere planear el
fallback (probablemente reemplazar la llamada del InvoiceService con un
NoOp temporal + log).

## 6. Testing strategy

Tests existentes:

- Smoke test `BuildServiceProvider_ResolvesAllServices` (BR-V2-04) —
  verifica que las 2 interfaces resuelven sin error.
- Test "BC y Bridge resuelven a la MISMA instancia" (commit `34d3c7e`).
- E2E `HappyPath_FlujoCompletoConTransferAlCliente_CierraBcYCancelaReserva`
  (commit `3640ba9`) — invoca el bridge manualmente post-Confirm para
  simular AFIP respondiendo, sin pasar por Hangfire.
- `OnArcaSucceeded_BcNoEncontrado_NoTira_LogeaWarning` (commit `81b8332` T7)
  — robustez del bridge ante BC eliminado.

Tests pendientes:

- E2E con Hangfire real procesando el callback (diferido a FC2 cuando
  se implemente `ProcessAnnulmentJob` completo).

## 7. Auto-critica

- **Verificado en repo**: si — `IInvoiceAnnulmentBcBridge.cs`
  interface, `BookingCancellationService.cs` implementa ambas,
  `Program.cs` registra las 2 con `GetRequiredService<BookingCancellationService>()`.
- **Acoplamiento residual**: el InvoiceService aun depende del BC, pero
  via interface chica. Mejor que el escenario opuesto.
- **Si el BC crece a 5+ metodos en el bridge**, considerar refactor.
  Hoy 2 metodos es manejable.
- **Comentario didactico en `Program.cs`**: el implementador debe
  copiar el bloque del plan v3 §2.1.bis al `Program.cs` real para que
  quien lea en 6 meses entienda.
