# 2026-06-16 — Auditoría de la lógica del ciclo de la reserva + cobro desacoplado del estado

## Contexto
Gastón pidió dejar de tapar incendios sueltos y atacar la lógica de fondo: la relación
entre **etapa de la reserva ↔ qué se puede tocar ↔ cuándo se puede cobrar**, donde él intuía
problemas que no podía enumerar solo. Caso semilla: una reserva **Finalizada con deuda** que
no se podía cobrar (por estar finalizada) ni reabrir (por tener factura) = **deuda atrapada**.

## Auditoría sistemática (5 miradas, read-only)
Se auditó TODO el ciclo con: arquitecto (esqueleto de estados/transiciones/candado), arquitecto
(plata ↔ estado), arquitecto (servicios/pasajeros/vouchers), `travel-agency-accountant-argentina`
(coherencia fiscal/contable) y mirada ERP (general-purpose con internet).

### Causa raíz (confirmada por las 5)
**La cobrabilidad estaba atada al ESTADO operativo de la reserva, no a la DEUDA real.** Patrón
ERP unánime (SAP/Odoo/Dynamics/NetSuite): el cobro va contra la cuenta por cobrar / factura,
independiente del ciclo de vida del documento. Acoplarlo al estado genera "receivables atrapados".

### Otros callejones/contradicciones encontrados (clase, no casos sueltos)
- `PendingOperatorRefund` sin salida clara; deuda en terminales invisible (no aparecía en cobranzas/alertas).
- `Cancelled` terminal sin revert (cancelar por error = rehacer todo).
- `InvoiceService` no bloqueaba facturar reservas Closed/Cancelled (lista negativa).
- `CancelServiceAsync` no validaba estado (cancelar servicios en reservas muertas); cancelación
  parcial de servicio pagado sin factura perdía el rastro del recupero (plata).
- Menores (backlog): pasajeros bajo candado, voucher por scope, HK-vs-emitido, des-confirmar por
  pago de proveedor, saldo a favor detrás de un feature flag.

## Decisión de Gastón
Desacoplar el cobro de la etapa: una reserva con deuda real se cobra siempre (incluida Finalizada),
queda en su etapa pero su deuda aparece en cobranzas; la factura no se toca. Primero el plan
diseñado y revisado, después construir.

## Diseño y construcción
- **ADR-032** (regla única de estado cobrable + anular-con-rastro): cerró un agujero previo (se podía
  cobrar en Canceladas/Perdidas por un endpoint sin gate). Implementado y revisado.
- **ADR-033** (cobro desacoplado del estado): reemplaza la regla de ADR-032. Diseño → architect-reviewer
  (Changes Required, atendidos) → Ready. Construido **E1-E7** (lo no-fiscal):
  - Regla nueva: `IsCollectable()` = venta firme + saldo > 0 (con Balance fresco, por moneda).
  - Dos listas separadas: `ActiveCollectionStatuses` (operativo vivo, sin Closed → lead "Ganado")
    vs `ReceivableDebtStatuses` (con Closed → "tiene cuenta por cobrar"). Cerrar una reserva NO gana el lead.
  - Visibilidad de la deuda de finalizadas en cobranzas/cuenta del cliente/dashboard/alertas (por moneda).
  - Editar/borrar pago por inmutabilidad fiscal (recibo/CAE/puente), no por estado; anular-con-rastro la salida.
  - `CancelServiceAsync` con gate de estado vivo; revert `Cancelled→InManagement` con gate duro
    (sin NC / sin saldo a favor / sin devolución) + autorización + sin CAE.
  - FC4 (saldo a favor) acepta destino Closed con deuda pero conserva INV-096 e INV-095 per-moneda.
  - Visibilidad del crédito de operador no consumido (alerta). Estado de cobro por moneda en DTOs.
  - Sin migración, sin feature flags. backend-reviewer + security: Approved with comments (0 bloqueantes). Unit 1891/1891.

## Pendiente
- **Deploy** acumulado (lo hace Gastón): pull + consulta read-only de control + `run-tests-all` + `deploy.sh`.
- **Contador** (no se cruzó): E8 (no facturar cerradas / factura tardía reabre a "A liquidar"),
  E9 (ancla refund-cap sin factura), Parte C (cobro vs NC vs incobrable; recibo en período posterior).
- **Gate UX** (con Gastón): pantallas de estado de cobro, alerta de crédito atascado, botón reabrir Cancelada.
- **⚠️ Advertencia de seguridad (pre-existente, amplificada):** la alerta "viajó/terminó y debe"
  muestra monto + nombre de clientes de reservas ajenas a cualquier usuario (sin scoping por dueño ni
  máscara de costos). Cerrar antes de tener vendedores.

## Commits del día (main)
`039b98f` (ADR-031 + pantalla negra), `e5f2bc2` (tests FC4), `4992057` (TDZ), `9ad13ac` (widget cantidad),
`a8820e1` (mensaje + color-scheme), `73a8b03` (ADR-032), `d7e0b40` (fix .env del script), `75bcaa5` (ADR-033 E1-E7).
