# 2026-06-24 — Fix: el botón "Confirmar multa del operador" no se activaba (pass-through)

## El problema (en simple)
Cuando se anula un viaje y el operador cobra una multa que se le traslada al cliente
(pass-through), la pantalla de la reserva mostraba el botón **"Confirmar multa del operador"**
(el que dispara la Nota de Débito). Pero al tocarlo decía *"no se encontró una multa
pendiente"* y no pasaba nada. El botón estaba muerto justo para el caso más común.

## Por qué pasaba
El botón buscaba la cancelación en la **bandeja de back-office de Notas de Débito pendientes**
(`GET /api/cancellations/debit-notes/pending`). Esa bandeja solo lista cancelaciones cuya ND
ya está en estado `Pending` o `Failed`. En el pass-through la multa todavía está **"Estimada"**
(la ND aún no aplica), así que la cancelación **nunca aparecía en esa bandeja** → el botón no
encontraba nada.

## La solución
Que el botón vaya **directo a la cancelación de esa reserva** en vez de buscar en la bandeja.

### Backend
- **Endpoint nuevo de lectura**: `GET /api/cancellations/by-reserva/{reservaPublicId}` —
  devuelve la cancelación vigente de la reserva (la más reciente no abortada). Permiso
  `reservas.view` + ownership sobre la reserva (un vendedor no puede leer la cancelación de una
  reserva ajena).
- **Dos campos nuevos en el DTO** (`BookingCancellationDto`):
  - `canConfirmPenalty` (sí/no se puede emitir la ND ahora).
  - `confirmPenaltyBlockedReason` (si no se puede, el motivo: NC sin CAE / ND ya en juego /
    función deshabilitada).
  - Son **pista de UI derivada del estado**. El backend revalida TODO al ejecutar
    `confirm-penalty` (permiso, doble firma, idempotencia, fecha): el bool no puede disparar
    una ND por sí solo.

### Frontend
- La API `getByReserva(reservaPublicId)` reemplaza a la vieja `getPendingDebitNoteByReservaNumero`.
- El botón consulta esa cancelación y decide: si `canConfirmPenalty` → abre el panel; si no →
  muestra el motivo concreto; si la reserva no tiene cancelación → 404 con aviso claro.

## Qué NO se tocó
- La **regla fiscal** de NC total + ND por multa (decisión del contador, cerrada). Intacta.
- La lógica de emisión de NC/ND. Intacta.
- El panel inline `ConfirmarMultaOperadorInline` (ya estaba bien).

## Verificación
- Backend: build 0 errores. Tests del controller 11/11 (incluye 4 nuevos: ownership 403,
  404 sin cancelación, pass-through `canConfirmPenalty=true` que ancla el bug, y flag off).
  248 unit de cancelación verdes. (Los Integration requieren Postgres real → se corren en el VPS.)
- Frontend: build de Vite OK. Lógica de decisión del botón cubierta con `node --test` (34/34).
- Revisado por backend-reviewer (Approved with comments), security-data-risk (Approved sin
  bloqueantes) y frontend-reviewer (Approved). Los "with comments" (tests del endpoint, limpieza
  de comentarios viejos, nota de doc) ya fueron atendidos en esta misma sesión.

## Archivos
- `src/TravelApi/Controllers/CancellationsController.cs` (endpoint by-reserva)
- `src/TravelApi.Application/Interfaces/IBookingCancellationService.cs` + `.../DTOs/CancellationDtos.cs`
- `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` (GetByReservaAsync + read-model en MapToDtoAsync)
- `src/TravelApi.Tests/Cancellation/Http/CancellationsControllerTests.cs` (4 tests nuevos)
- `src/TravelWeb/.../cancellations/api/cancellationsApi.js`, `.../reservas/pages/ReservaDetailPage.jsx`, test `.mjs`

## Pendiente (gate de Gastón)
- **Deploy** del lote (lo hace Gastón).
- Después: probar la receta de la ND end-to-end (anular → NC total → confirmar multa monto+fecha
  → ND; en homologación va al ARCA de prueba).
