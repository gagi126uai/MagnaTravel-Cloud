# 2026-06-26 — Reservas: Fase 3 (Anular unificado + datos sanos) y Fase 4 (la pantalla obedece al backend)

Cierre de la rehechura integral de Reservas en 4 fases. Las fases 1 y 2 ya estaban en `main`
(`076a642`). Esta sesión completó y commiteó las fases 3 y 4. **Commiteado sin pushear** (el deploy
lo hace Gastón). Sin migraciones nuevas; la cola pendiente (`Adr036_M2`, `Adr037_M1`, `Adr038_M1`,
`Adr039_M1`) viene de antes.

## Commits
- `a18be9a` — Fase 3: datos sanos + motor de "Anular unificado" (caso saldo a favor).
- `3c04767` — Fase 3: pantalla "Anular reserva" (3 casos) + motivo auditado en saldo a favor.
- `a6ec0fb` — Fase 4: la pantalla obedece al backend (capacidades, KPIs, aviso Factura A).

Backend unit **2497/2497**, frontend **1185/1185** + build. Todas las reviews Approved
(con re-review donde aparecieron bloqueantes de plata).

## Fase 3 — "Anular reserva" unificado
Un solo botón "Anular reserva" que SIEMPRE funciona, con el cartel correcto según el caso (derivado
por el backend, no deducido por el front):

- **DirectCancel** (en firme, sin factura, sin cobros): baja directa. Cartel verde.
- **PaymentsToCredit** (en firme, sin factura, con cobros): la plata cobrada queda como **saldo a
  favor** del cliente por moneda, **sin Nota de Crédito**. Cartel celeste con el monto por moneda.
  Endpoint nuevo `POST /api/reservas/{id}/annul-with-credit`.
- **CreditNote** (con factura con CAE vivo): anulación formal con NC. Cartel ámbar.

Mecanismo del caso saldo a favor: `CancellationToClientCreditConverter` crea un `ClientCreditEntry`
por moneda + un `Payment` puente negativo `AffectsCash=false` (mismo patrón que el de sobrepago).
Atómico (transacción Serializable, patrón FC4): cancela servicios → recalcula deuda de cada operador
(`SupplierDebtPersister.PersistForReservaSuppliersAsync`, helper compartido con el camino formal) →
convierte la plata → estado Cancelled → audit → saldo, todo en un commit.

Bloqueantes de plata encontrados en review y arreglados antes de mergear:
1. La deuda con el operador no se recalculaba tras cancelar servicios (quedaba inflada).
2. Doble clic / retry de la ExecutionStrategy podía crear saldo a favor duplicado → guard de
   idempotencia por `BridgeMethod` + precondiciones movidas dentro de la transacción + `ChangeTracker.Clear`.
3. Reserva sin pagador (`PayerId==null`) con cobros perdía la plata → ahora rechaza 409 antes de mutar.
4. Hueco de auditoría: el motivo se pedía pero no se guardaba → el endpoint ahora exige y persiste
   el motivo (≥10 chars) en el detalle del audit.

### Datos sanos (integridad)
- Fechas imposibles: regreso/llegada/fin (opcional) no anterior a salida/inicio en vuelo, traslado,
  paquete y servicio genérico (alta y edición).
- Reprogramar al pasado: aviso no bloqueante.
- Guard de último pasajero: no borrar el último pasajero de una reserva en firme.

## Fase 4 — La pantalla obedece al backend
El front dejaba de inventar reglas y pasa a leer la verdad que ya expone el backend. Casi todo fue
corrección invisible; 3 puntos visibles los decidió Gastón (eligió las 3 recomendadas):

- **Cobrado este mes**: el número grande muestra solo plata real que entró, **por moneda** (excluye
  los puentes de saldo a favor/sobrepago/anulación). El saldo a favor **aplicado** va aparte en una
  línea chica "+ $X aplicados de saldo a favor".
- **Botón de acción ya cumplida en reserva activa** (ej. "Emitir factura" en una ya facturada del
  todo): **desaparece** (el chip "Facturada total" ya lo explica). Regla unificada: ya-cumplido =
  esconder; bloqueado por estado/permiso = gris.
- **Factura A**: chequeo previo de solo lectura
  (`GET /api/invoices/reserva/{id}/emission-preflight`) que **frena antes** de emitir cuando el caso
  no corresponde. Hallazgo clave: el vendedor no elige la letra (la deriva el backend); el caso real
  que rebota en ARCA es cliente RI/Monotributo **sin CUIT**. Bloqueo duro único: letra A sin CUIT;
  el resto avisa (warn) o sigue (ok), conservador. La matriz fiscal de fondo está confirmada por
  dueño+contador en `InvoiceTypeResolver` (no se reabre); el caso Exterior/Factura E queda fuera de
  alcance, a confirmar con el contador.

Otros (invisibles): visibilidad de "Emitir factura"/vouchers desde `invoicingStatus`; "Anular reserva"
lee `canAnnul` (no `canCancel`) y se oculta en pre-venta; "Eliminar" lee `canDelete` real (capacidad
nueva que coincide con `DeleteGuards`, incluido el bloqueo por servicio confirmado por el operador);
una sola verdad de plata (todos leen `porMoneda`); "falta facturar" separado por moneda; validación
del pago al operador por servicio (moneda y tope).

## Pendientes menores (no bloquean)
- Confirmar con Gastón si la línea chica de saldo a favor va también en la home de pagos.
- Caso Exterior / Factura E del preflight: definición del contador.
- Residual técnico: `ReservaServiceCanceller` duplica el barrido de servicios de
  `BookingCancellationService` (sincronizar o test de paridad).
- Validación en Postgres de la atomicidad Serializable (annul-with-credit) y de los KPIs (solo
  probados en InMemory).
- QA: testids del botón anular renombrados a `btn-anular-reserva`.
