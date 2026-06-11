# POSTERGADO (decisión Gaston 2026-06-10) — Pantalla 4 "Cuenta corriente del proveedor" multimoneda

Relacionado: ADR-021 (§15 eje proveedor), mockup `docs/ux/mockups/2026-06-10-multimoneda-v3-pantallas-reales.html` (PANTALLA 4).

## Qué se posterga

Al dar el OK del mockup v3, Gaston pidió construir **pantallas 1 (reserva), 5 (caja) y 6 (reportes)** y dejar la **pantalla 4 (cuenta corriente del proveedor)** para más adelante.

Queda fuera de este lote, para retomar después:

1. **Vista de cuenta corriente del proveedor por moneda** (las 3 tarjetas de plata —Total Compras / Total Pagado / Saldo Pendiente— desdobladas en ARS/USD; columna moneda en las tablas Servicios Comprados e Historial de Pagos). Es la PANTALLA 4 del mockup v3.
2. **Registro de pago saliente al proveedor con moneda + TC cruzado** (`SupplierPayment` cross-currency UI). El backend de datos ya existe (capas 1-3: `SupplierPayment.Currency` + bloque TC, `SupplierBalanceByCurrency`), falta la UI de captura y las validaciones de registro.
3. **Consumidores del eje proveedor por moneda en alertas/cobranzas** (top-N proveedores deudores por moneda, `AlertService` §6bis), salvo lo que ya consuma reportes.

## Qué SÍ entra ahora (no confundir)

- **Reportes (pantalla 6)** muestra la lista "Cuentas por Pagar" por moneda **leyendo la tabla `SupplierBalanceByCurrency` ya materializada** (capas 1-3). Eso NO requiere construir la pantalla 4: es solo lectura agregada en el reporte.
- **Caja (pantalla 5)** muestra los egresos "Pago a proveedor" con su moneda **leyendo `SupplierPayment.Currency`** (campo ya existente). Mientras no se construya el registro cross-currency del punto 2, los pagos a proveedor siguen entrando en su moneda actual (ARS por defecto); la caja solo los muestra.

## Por qué no rompe nada postergarlo

El lado datos del proveedor (capas 1-3) ya está construido, revisado y verde. Postergar la pantalla 4 solo deja sin construir la **UI de la cuenta del proveedor** y el **registro cross-currency del pago saliente**. Reportes y caja se alimentan de datos que ya existen.
