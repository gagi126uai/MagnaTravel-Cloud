# 2026-06-17 — Pasajeros bajo candado (completar sí) + vuelo de cotización nace "solicitado"

Dos arreglos chicos del cajón de pendientes de la auditoría (ADR-033 §12).

## 1. Pasajeros bajo candado: completar sí, cambiar/borrar pide permiso

**El problema:** cuando una reserva está confirmada y "con candado", el sistema te frenaba
**cargar o completar** el nombre/documento de un pasajero — pero esos son justo los datos que te
**exige** para poder **emitir** el aéreo o la asistencia. Era un callejón sin salida: te pide el dato
y no te deja cargarlo sin pedir una autorización.

**Lo que decidió el dueño (Gastón):**
- **Completar** un dato de identidad que estaba vacío (nombre/documento/fecha de nacimiento) → NO pide
  autorización (es completar lo que el sistema exige para emitir).
- **Cambiar** un dato de identidad ya cargado, o **borrar** un pasajero → SÍ pide autorización.
- El **candado fiscal** (voucher emitido / factura con CAE) se respeta **siempre**, en los dos casos.

**Qué cambió (en `ReservaService.cs`):**
- **Agregar** un pasajero ya no pide autorización por estado (es completar el roster). La cantidad
  declarada de la reserva acota cuántos se pueden cargar; agregar no toca comprobantes ya emitidos.
- **Editar** un pasajero pide autorización **solo si cambia un dato de identidad ya cargado** (incluye
  limpiarlo). Completar campos vacíos pasa sin autorización. Contacto (teléfono/email/notas) y
  vencimiento de pasaporte NO son identidad: se editan libres.
- **Borrar** un pasajero: sin cambios (sigue pidiendo autorización).
- El candado fiscal interno (bloquea si hay voucher emitido / CAE viva) sigue intacto: incluso
  "completar" un documento sobre una reserva con factura emitida queda bloqueado fiscalmente, que es
  lo correcto.

## 2. El vuelo que nace de convertir una cotización arranca "solicitado"

Al convertir una cotización en reserva, el vuelo nacía como **"reservado (HK)"** —mostrando un PNR
confirmado por la aerolínea que nadie confirmó, y marcándolo como no-borrable indebidamente—. Ahora
nace **"solicitado" (NN)**, igual que cualquier vuelo nuevo. No afectaba plata ni la confirmación de la
reserva (sin emisión real no resuelve), pero el estado inicial era engañoso.

## Tests
- `Adr020PassengerCompletionUnderLockTests.cs` (7): agregar bajo candado sin permiso OK; completar
  documento vacío OK; cambiar nombre / cambiar tipo de documento / limpiar documento ya cargado →
  pide permiso (409 sin autorización); editar solo contacto OK; cambiar con autorización viva OK.
- `QuoteServiceConvertCatalogTests.cs`: el vuelo convertido nace "NN", sin emisión.
- Suite Unit completa en verde: 1860/1860. Build limpio.

## Revisión
- `backend-dotnet-reviewer`: **Approved with comments** (0 bloqueantes).
- `security-data-risk-reviewer`: **Approved with comments** (0 bloqueantes). Confirmó que agregar un
  pasajero NO altera vouchers/facturas ya emitidos (el voucher congela su lista; la factura va al
  titular, no a la lista de pasajeros).

## Pendientes anotados (no urgentes, futuros)
- Cuando haya vendedores: dejar rastro de **quién completó** los datos de un pasajero (hoy "completar"
  no genera registro de autorización; con un solo usuario no importa).
- Coherencia opcional: que **agregar** un pasajero también respete el candado fiscal explícito (hoy lo
  cubre indirectamente la cantidad declarada; agregar no toca comprobantes, así que no hay riesgo).
