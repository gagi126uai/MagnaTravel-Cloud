# 2026-06-11 — Multimoneda visible: pantallas 1 (reserva), 5 (caja) y 6 (reportes)

## En una frase
Se construyó la parte VISIBLE de las dos monedas (pesos y dólares por separado) en la reserva, la caja y los reportes. La pantalla del proveedor quedó postergada por decisión de Gastón. Todo commiteado, **sin desplegar todavía**.

## Qué pidió Gastón
Del mockup v3 aprobado: construir **pantalla 1 (la reserva), 5 (la caja) y 6 (los reportes)** y **dejar la 4 (cuenta corriente del proveedor) para más adelante**.

## Qué se hizo

### Backend (invisible para el usuario, es el motor)
- **Capa 4 — registro de cobro con tipo de cambio.** Nuevo `PaymentCurrencyResolver`: valida la moneda del cobro y la del saldo, y si difieren (cobro cruzado) exige tipo de cambio + fuente + fecha y **recalcula el equivalente en el servidor** (no confía en el front). Convención §2.2bis (ARS por 1 USD). `UpdatePaymentAsync` bloquea editar monto/moneda/TC de un cobro cruzado (anular+recrear).
- **Capa 5 — `PorMoneda`/`EsMultimoneda` en `ReservaDto`/`ReservaListDto`** + mapeo, con **enmascarado `cobranzas.see_cost` por moneda** (el costo/inversión de cada moneda se oculta a quien no ve costos, en detalle, listado y dashboard).
- **Capa 6 — `TreasuryService` y `ReportService` por moneda** (caja real por moneda real del pago; cuentas por cobrar por moneda imputada; cuentas por pagar leyendo la tabla ya materializada `SupplierBalanceByCurrency`; top-N deudoras por moneda).
- **Fixes de contrato** (lo más importante de la sesión): el frontend consumía endpoints que NO traían el desglose por moneda (estaba en otros endpoints). Se expuso el desglose donde las pantallas realmente piden los datos:
  - `/treasury/cash-summary` → `cashByCurrency` (lista por moneda con in/out/neto del mes).
  - `/reports/detailed` → `summary.porMoneda` (6 listas) + `supplierDebts` ahora **una fila por proveedor+moneda** con `currency`.
  - `/reports/detailed-receivables` → **una fila por cliente+moneda** con `currency`.
  - `PaymentDto` + campos de moneda/TC; `ServicioReservaDto.currency`; `CreatePaymentRequest.paidAt`.

### Frontend (lo que el usuario ve)
- `formatCurrency(amount, currency)` parametrizado, **preservando el formato anterior** donde no se pasa moneda (para no alterar pantallas fuera de alcance).
- Reserva: franja de 3 números y lista de servicios desdoblan por moneda solo si la reserva tiene dos monedas (fila Total por moneda solo en ese caso).
- Cobro en línea (`RegistrarCobroInline`): selectores de moneda escondidos en reservas de una sola moneda; recuadro de tipo de cambio solo cuando cruza; cobro cruzado = una sola fila en el historial; editar cruzado solo nota/método.
- Caja: 3 métricas por moneda + columna moneda en movimientos + filtro moneda siempre visible.
- Reportes: 4 tarjetas por moneda + listas Cobrar/Pagar por moneda. Flujo Neto por moneda (unión de monedas de cobros y pagos, no pierde filas).

## Decisiones de Gastón (registradas en `docs/ux/guia-ux-gaston.md`, secc. 2026-06-11)
- Fila "Total" de servicios: solo si hay 2 monedas.
- Selectores del cobro: escondidos en reservas de una sola moneda.
- Editar cobro cruzado: solo nota y método.
- Filtro de moneda en la caja: siempre visible.

## Revisiones
- Backend reviewer: Approved con comentarios. Security reviewer: Approved con comentarios. Sin bloqueantes.
- Frontend reviewer: Changes Required → encontró **5 desajustes reales de contrato front↔back** (endpoints/DTOs/enum) que mostraban $0 en silencio y permitían editar un cobro cruzado como normal. **Todos corregidos y re-verificados.**
- Falsa alarma descartada: "Manual exige justificación" es del circuito fiscal de cancelaciones, no del cobro.

## Reglas respetadas
① Pesos y dólares siempre separados, nunca un total convertido. ② Nunca "diferencia de cambio". ③ Una sola moneda = idéntico a hoy.

## Estado / pendiente
- **Commiteado, NO desplegado.** Para verlo en el servidor: desplegar (corre la migración `Adr021_M1` + el **backfill** multimoneda en el arranque; si no corre, caja/reportes muestran deuda en cero falso → vigilar el log "ADR-021 backfill finished").
- QA automática de los flujos: no corrida (opcional, recomendada).
- No verificado: tests de integración Postgres (sin Docker local).
- Postergado: pantalla 4 (proveedor) — ver `docs/architecture/adr/ADR-021-POSTERGADO-pantalla-proveedor.md`.
- Pendiente fiscal aparte: factura del aéreo en dólares (confirmar con contador).

## Pruebas
- Backend: `dotnet test --filter Unit` = 1277/1277 verde.
- Frontend: 464/464 verde.
