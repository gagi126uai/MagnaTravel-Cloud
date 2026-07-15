# Cierre Codex — T5 y cuentas corrientes

Fecha: 2026-07-15
Estado: implementación terminada y validada localmente.
Documento previo: `2026-07-15-auditoria-cuentas-t5-codex.md`.

## Decisiones de producto aplicadas

- La deuda del cliente nace solamente de comprobantes aprobados; presupuestos y cotizaciones no generan deuda.
- “Cobrado” representa únicamente caja real (`AffectsCash=true`). Las aplicaciones internas se muestran aparte.
- ARS y USD permanecen en libros separados; ningún encabezado, lista, alerta u ordenamiento los suma.
- Las multas se documentan con Nota de Débito, normalmente al cliente, y pueden cobrarse aun con la reserva anulada.
- La factura del operador puede abarcar un servicio, una reserva o varias reservas consolidadas.
- Las aplicaciones y reversiones de créditos/pagos son explícitas, inmutables y auditadas.
- La creación vigente es “Presupuesto”; la cotización vieja queda sólo como histórico.

## T5 terminado

- Panel completo en la reserva: listo/bloqueado/emitiendo/emitido/rechazado, doble confirmación, polling y reintento.
- Selección explícita de factura y monto en cancelaciones parciales o multifatura.
- Resolver seguro para registros legacy, con motivo obligatorio y auditoría.
- Emisión de NC parcial idempotente, con tope por remanente, herencia de moneda/TC y transacción.
- PDF y envío del comprobante fiscal mediante endpoint dedicado y ownership.
- Cobros concurrentes de una misma ND protegidos con transacción `Serializable`.

## Cuenta corriente cliente

- Extracto documental: factura/ND como cargos; NC, cobros y aplicaciones explícitas como créditos.
- Deuda abierta por reserva y moneda; un saldo a favor de una reserva no compensa otra automáticamente.
- “Documentado”, “Cobrado”, “Comprobantes” y saldos por moneda corregidos.
- Multas visibles como desglose incluido en “Debe” y cobrables contra su ND aprobada.
- El cobro de una multa anulada mueve caja pero no el saldo operativo de la venta (`AffectsReservaBalance=false`).
- Ownership aplicado a resumen, extracto, multas, pagos, facturas, reservas y bolsillos de crédito.
- Las mutaciones de cliente ya no devuelven información financiera sin permiso.

## Cuenta corriente operador

- Listas, alertas, exportación, aging y extracto separados por moneda.
- Facturas recibidas documentales con número único, emisión, vencimiento y estados.
- Alcances soportados: servicio, reserva y consolidada multirreserva, siempre mismo operador/moneda.
- Sólo se facturan servicios confirmados que integran la deuda oficial y operadores con compra a cargo de la agencia.
- Aplicación de pagos existentes, reversión con motivo y anulación con historial.
- Un pago imputado a servicio/reserva no puede liquidar líneas ajenas; un pago de cargo de cancelación no se reutiliza.
- Un pago aplicado queda inmutable hasta revertir la aplicación.
- UI con búsqueda paginada, remanente editable y separación entre comprometido no facturado y facturado pendiente.

## Presupuestos y cotizaciones legacy

- Todos los accesos de alta conducen a `/reservas?create=1` y crean `Budget`.
- `/quotes` queda histórico y no ofrece alta.
- CRM crea presupuestos por endpoint dedicado, con permisos apilados, ownership e idempotencia.
- La conversión legacy sólo acepta cotizaciones aceptadas, actualiza el lead y corre atómicamente.

## Migraciones nuevas

1. `20260715053314_AddSupplierInvoiceOpenItems`
2. `20260715055609_AddSupplierInvoiceApplicationReversals`
3. `20260715062503_AddPaymentOperationalBalanceFlag`

El modelo EF no tiene cambios pendientes.

## Validación ejecutada

- Solución .NET: build correcto, 0 errores.
- Unitarias backend: 3.581/3.581.
- Integración API/autorización: 95/95.
- Frontend: 2.167/2.167 y build Vite correcto.
- Revisiones independientes finales: T5/seguridad, cliente y proveedor sin bloqueantes.
- Las 231 integraciones PostgreSQL de cancelaciones no pudieron iniciarse localmente porque Docker/Testcontainers
  no está disponible. El fallo fue común al fixture `PostgresIntegrationFixture`, no una aserción del producto;
  deben ejecutarse en CI con Docker.

## Prueba manual recomendada

1. T5: cancelar parcialmente una reserva multifatura, elegir factura/monto, confirmar dos veces, emitir, abrir PDF y enviar.
2. Cliente: registrar una ND de multa en una reserva anulada y cobrarla; comprobar que “Debe” baja y no aparece un crédito ficticio.
3. Cliente multimoneda: tener deuda ARS y USD, y verificar dos importes separados en lista, cuenta y extracto.
4. Cliente ownership: un vendedor sin `cobranzas.view_all` sólo debe ver dinero de sus reservas.
5. Operador: registrar factura de un servicio, de una reserva y consolidada; aplicar pago, revertir y anular.
6. Operador multimoneda: verificar ARS/USD separados y que un pago de una moneda no se aplique a la otra.
7. Presupuesto: crear desde Dashboard, Cliente y CRM; todos deben entrar al circuito de Presupuesto, nunca a cotización nueva.
