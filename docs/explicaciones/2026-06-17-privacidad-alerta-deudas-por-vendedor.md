# 2026-06-17 — Privacidad de la alerta de deudas (por vendedor + costo con permiso)

## El problema
La campanita de avisos tiene dos partes de plata:
- **"Viajó / terminó y todavía debe"**: nombre del cliente + lo que el cliente debe.
- **"Le debemos al operador"**: proveedor + cuánta plata le debemos (eso es un *costo*).

Hasta hoy las dos funcionaban "todo o nada": **solo el admin las veía, completas, de toda la agencia**.
No había un camino por-vendedor, y la deuda al operador (un costo) estaba protegida solo por "ser admin",
no por el permiso de ver costos. Como Gastón es hoy el único usuario (admin), no había ninguna fuga real;
el riesgo aparecía el día que sumara vendedores: o no veían nada, o si se abría, verían clientes y plata
de todos los demás vendedores. La auditoría de junio (ADR-033) lo dejó marcado como "cerrar antes de tener
vendedores".

## Lo que se decidió (Gastón, 2026-06-17)
- **"Viajó y debe"** → cada vendedor ve **solo SUS clientes** (los de sus reservas); el admin sigue viendo
  todo. Lo que el cliente debe se muestra igual (no es un costo tapado — regla de la casa 2026-06-09).
- **"Le debemos al operador"** → como es un **costo**, la ve **solo quien tiene permiso de ver costos**
  (el admin siempre lo tiene). Es deuda agregada de la agencia, no se separa por vendedor.

## Lo que cambió en el código
Un solo archivo de producción: `src/TravelApi.Infrastructure/Services/AlertService.cs`
(`ComputeFinancialBucketsAsync`).
- **UrgentTrips**: filtro por dueño (`ResponsibleUserId == caller.UserId`) + candado fail-closed (un
  no-admin sin identidad clara no ve nada — si no, "sin dueño" matchearía todas las reservas viejas).
  Mismo patrón que ya usaban las demás alarmas del archivo.
- **SupplierDebts**: se calcula solo si el que llama tiene `cobranzas.see_cost`.
- `GetAlertsAsync` dejó de gatear todo con `if (caller.IsAdmin)`; ahora delega el borde a cada bucket.
- Comentario del front `AlertsContext.jsx` actualizado (ya no dice "solo admin").

Sin migración, sin feature flag (es un fix de privacidad, igual que el enmascarado de costos de ADR-017).
**Invisible hoy** (Gastón es admin → ve todo igual); deja el modelo correcto para cuando haya vendedores.

## Tests
- Nuevo: `src/TravelApi.Tests/Unit/AlertFinancialBucketScopingTests.cs` (9 tests): vendedor ve solo lo
  suyo; no ve reservas sin dueño; fail-closed con UserId null y con UserId vacío; admin ve todo; deuda de
  cliente visible sin permiso de costo; deuda al operador oculta sin permiso y visible con permiso sin
  scope por dueño; el permiso de costo NO anula el scope por dueño en "viajó y debe".
- Ajustados 2 tests en `AlertServiceCallerGatingTests.cs` (admin con `CanSeeCost: true`, que es lo que el
  controlador arma en la realidad).
- Suite Unit completa en verde (1850/1850). Build limpio.

## Revisión
- `backend-dotnet-reviewer`: **Approved with comments** (0 bloqueantes).
- `security-data-risk-reviewer`: **Approved with comments** (0 bloqueantes). Verificó que no hay otro
  endpoint que sirva los mismos datos sin gate (Reports es admin-only / enmascara costos).

## Pendiente / a tener en cuenta
- **Integración Postgres en el VPS** (la corre Gastón): valida la semántica real de "sin dueño" (NULL) que
  InMemory no replica al 100%.
- **Permiso de costos cuando haya vendedores**: cualquiera con `cobranzas.see_cost` verá la deuda total a
  operadores de toda la agencia (por diseño). Hoy el vendedor por defecto NO tiene ese permiso → queda como
  back-office. Confirmar al definir roles.
- Deuda técnica pre-existente (no de este cambio): `ComputeFinancialBucketsAsync` usa `UtcNow.Date` como
  "hoy" mientras el resto usa la hora de pared de Argentina (~3h de desfase entre 21:00 y 24:00 ART).
