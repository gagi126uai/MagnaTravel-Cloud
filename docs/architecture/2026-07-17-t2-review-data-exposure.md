# Gate de exposición de internos — ADR-048 T2 (2026-07-17)

**Alcance revisado:** `git diff` sin commitear de
`src/TravelApi.Infrastructure/Services/SupplierService.cs` (222 líneas) +
`src/TravelApi.Tests/Unit/SupplierPaymentStatusCancelledServiceTests.cs` (nuevo, solo tests).

**Verdict: Approved**

## Pregunta crítica: ¿`ServiceRowId` (int interno de DB) viaja en la respuesta HTTP?

**No.** Trazado completo de la serialización:

1. `ReservaServicePaymentRow` (SupplierService.cs:3438) es un
   `private readonly record struct` — no sale de la clase `SupplierService`. Se le agregaron dos campos
   nuevos en este diff: `ServiceRowId` (int, el `Id` interno de la fila en su tabla tipada, ej.
   `HotelBookings.Id`) e `IsCancelled` (bool).
   Confirmado con grep: las únicas referencias a `ReservaServicePaymentRow` en todo `src/` están dentro de
   `SupplierService.cs` (líneas 3206, 3316, 3346, 3438, 3448, 3451, 3465, 3478, 3491, 3504, 3517, 3530). Ningún
   controller, DTO público ni otro servicio la toca.
2. `ServiceRowId` se usa exclusivamente como clave de join interna en
   `LoadCancelledServiceOperatorChargesAsync` (SupplierService.cs:3385) contra
   `BookingCancellationLine.ServiceId` (que también es un int interno, no un PublicId — es el vocabulario que ya
   usa esa tabla). `IsCancelled` se usa en `ResolveServiceDebtBasis` (SupplierService.cs:3319) para decidir si el
   servicio entra al reporte y con qué costo.
3. El DTO que realmente sale al cliente se construye en el loop de
   `GetReservaSupplierPaymentStatusAsync` (SupplierService.cs:3167-3179):
   ```csharp
   var line = new ServiceSupplierPaymentStatusDto
   {
       RecordKind = service.RecordKind,
       ServicePublicId = service.PublicId,   // Guid, no el int interno
       SupplierPublicId = service.SupplierPublicId,
       SupplierName = service.SupplierName,
       Currency = Monedas.Normalizar(service.Currency),
       NetCost = netCost,
       PaidToOperator = paid,
       CreditAppliedToOperator = creditApplied,
       OutstandingToOperator = outstanding,
       Status = status
   };
   ```
   `ServiceRowId` e `IsCancelled` **no están** en esta proyección. La clase `ServiceSupplierPaymentStatusDto`
   (`src/TravelApi.Application/DTOs/SupplierReadDtos.cs:322-363`) tampoco fue tocada por este diff — sigue
   teniendo exactamente los mismos 9 campos de siempre, todos con `Guid`/`string`/`decimal` orientados al
   usuario (`ServicePublicId`, nunca un id interno).
4. El controller (`src/TravelApi/Controllers/ReservasController.cs:145-146`) hace
   `return Ok(dto)` sobre ese mismo `ReservaSupplierPaymentStatusDto` — sin mapeo adicional, sin exponer
   propiedades extra.

**Conclusión:** el int interno `ServiceRowId` nunca cruza el borde de `SupplierService.cs`; es indispensable
para el join con `BookingCancellationLine` (que referencia servicios por `(ServiceTable, ServiceId)`, no por
`PublicId` — así está diseñada esa tabla desde antes de este cambio) pero se descarta antes de armar la
respuesta. No hay fuga de id interno hacia el navegador.

## Segunda pregunta: ¿algún string/label nuevo con jerga llega al front?

**No hay strings nuevos user-facing en este diff.** Todo el texto agregado es:

- Comentarios XML-doc y `//` (español técnico, para desarrolladores) — nunca se serializan ni se loguean.
- Nombres de tipos/métodos internos (`ResolveServiceDebtBasis`, `CancelledServiceOperatorCharge`,
  `LoadCancelledServiceOperatorChargesAsync`, `MapRecordKindToCancellableServiceTable`) — todos `private`,
  jamás aparecen en una respuesta ni en un mensaje.
- El campo `Status` del DTO sigue devolviendo las mismas claves internas en inglés de siempre
  (`ServiceSupplierPaymentStatuses.Paid/Partial/Unpaid` = `"paid"/"partial"/"unpaid"`, definidas en
  `SupplierReadDtos.cs:366-371`) — **sin cambios en este diff**. Ese contrato ya existía; el front
  (`src/TravelWeb/src/features/reservas/components/OperadorPagoStatusBadge.jsx:38-101`, no tocado por este
  diff) ya las mapea 1:1 a español ("Operador pagado" / "Pago parcial al operador" / "Operador impago") antes de
  renderizar. Verificado que el mapeo cubre las tres claves y que ningún branch nuevo introduce una clave sin
  mapear.

No se detectó ningún `ex.Message`, `.ToString()` de excepción, GUID crudo, id interno, ni jerga en inglés nuevo
que llegue al usuario en este diff.

## Camino de error de este endpoint (no modificado por el diff, pero es el que corre si algo de lo nuevo falla)

`ReservasController.GetReservaSupplierPaymentStatus` (líneas 140-162):

- `KeyNotFoundException` (reserva no existe) → `NotFound()` sin body. Limpio.
- `DatabaseExceptionClassifier.IsDatabaseUnavailable(ex)` → 503 con
  `DatabaseExceptionClassifier.CreateProblemDetails()` (sin pasar `detail`) → título fijo
  "Base de datos no disponible." y detalle fijo "El servicio de base de datos no esta disponible en este
  momento." (`src/TravelApi/Errors/DatabaseExceptionClassifier.cs:55-66`). Sin `ex.Message`, sin stack. Limpio.
- Cualquier otra excepción (incluida una falla de la nueva consulta a `BookingCancellationLines` o del nuevo
  join) → `_logger.LogError(ex, ...)` (va al log del servidor, no a la respuesta) → `Problem(statusCode: 500,
  title: "No se pudo obtener el estado de pago al operador.")`. Fallback amistoso presente, sin detalle técnico.

Este pipeline no cambió en el diff, pero como la lógica nueva agrega una query y un join que antes no existían,
es el camino que efectivamente protege a un usuario si `LoadCancelledServiceOperatorChargesAsync` o
`MapRecordKindToCancellableServiceTable` lanzaran una excepción inesperada (por ejemplo, un `recordKind`
desconocido) — y se comprobó que sí sanitiza.

## Defensive coding verificado

`MapRecordKindToCancellableServiceTable` (SupplierService.cs:3411-3421) devuelve `null` para un `recordKind`
desconocido en lugar de tirar excepción; el caller (`LoadCancelledServiceOperatorChargesAsync`, línea 3378)
hace `continue` en ese caso. No hay ningún throw con el valor crudo de `recordKind` embebido en un mensaje.

## No verificado

- No se verificó en runtime (no se corrió la app); todo lo anterior es lectura estática del código +
  trazado de tipos/serialización, que es concluyente para C#/System.Text.Json (una propiedad que no existe en
  la clase del DTO no puede serializarse).
- El hook del front `useReservaSupplierPaymentStatus.js` y el badge `OperadorPagoStatusBadge.jsx` no fueron
  tocados por este diff — se leyeron solo para confirmar que el contrato de campos que consumen
  (`recordKind`, `servicePublicId`, `status`, `netCost`, `paidToOperator`, `outstandingToOperator`, `currency`)
  coincide con el DTO real y no hay lectura de un campo nuevo no serializado.

## Missing tests (sugerencia, no bloqueante)

El nuevo archivo de tests (`SupplierPaymentStatusCancelledServiceTests.cs`) cubre bien la lógica de negocio
(reglas 6/7), pero ninguno de los tests de este repo para este endpoint asegura explícitamente en el contrato
serializado que `ServiceRowId` no aparece en el JSON de respuesta (hoy es verdad "por construcción" porque el
DTO no tiene esa propiedad, pero un futuro refactor podría agregarla sin que ningún test lo capture). Sugerencia
no bloqueante: un test de contrato tipo "el JSON de `/supplier-payment-status` solo contiene las claves
esperadas" (allowlist de propiedades) sobre `ServiceSupplierPaymentStatusDto`, sirve como red de seguridad
futura para este mismo tipo de fuga.
