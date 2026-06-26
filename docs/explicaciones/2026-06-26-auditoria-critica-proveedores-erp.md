# 2026-06-26 — Auditoría crítica de la pata de PROVEEDORES (¿está bien como ERP? qué modificar)

Pedido de Gastón: rediseño completo de proveedores + análisis crítico, imparcial y cuestionador
de lo ya construido, benchmarkeado contra un ERP. Dos auditorías independientes leyendo el CÓDIGO REAL:
una de sistemas ERP (procure-to-pay / Accounts Payable) y otra fiscal+contable de agencia de viajes AR.

## Lo que está BIEN (conservar, no tocar)
- **Motor de deuda con el operador**: cálculo puro y único (`SupplierDebtCalculator`), un solo punto de
  escritura (`SupplierDebtPersister`), saldo separado por moneda (`SupplierBalanceByCurrency`). Sólido.
- **Pago al operador**: asiento de caja atómico en la misma transacción, moneda real del egreso, soft-delete
  con auditoría, imputación validada por reserva/servicio/moneda con tope. Bien.
- **Desglose por expediente/file**: `GetSupplierDebtByReservaAsync` reparte compras y pagos por reserva y
  moneda, con invariante de reconciliación (Σ por reserva + anticipos == total). (Corrige el supuesto
  "todo es global": el por-file YA existe.)
- **Módulo de reembolso del operador** (`OperatorRefundReceived` + allocations N:M + deducciones tipificadas
  AdministrativeFee/Withholding/Tax/Penalty, multimoneda, soft-void, reasociación): lo más maduro de la pata.
- **UI de cuenta del proveedor**: `src/TravelWeb/src/features/suppliers/` (cuenta corriente, listado, alta,
  modal de pago, desglose por expediente). (Corrige el supuesto "solo un picklist".)
- **Reglas fiscales cerradas YA respetadas en código**: NC total + ND por multa pass-through (con guard
  anti-doble-cobro), cliente acreditado con el NETO recién al entrar la plata, tipo de comprobante de la ND
  derivado de la factura original (RG 4540), tipificación de deducciones del operador.

## La pieza GORDA que FALTA: la "factura del operador" como documento
Hoy la deuda con el operador es **un número calculado** a partir de tus propios servicios, NO un **documento**
(la factura que el operador te manda). Un ERP de Cuentas por Pagar trabaja contra el comprobante recibido.
Sin ese documento, faltan en cascada: vencimiento de pago, aging, y el "three-way match" (cotejar lo que el
operador facturó vs lo que tenés cargado). Bajo RI además: sin la factura no hay crédito fiscal IVA ni respaldo
de deducibilidad en Ganancias.

## Agujeros concretos (priorizados)
1. **Reembolso del operador sin cierre por vencimiento**: `AbandonedByOperator` es código MUERTO (no se asigna
   nunca) y no hay job sobre `OperatorRefundDueBy`. La plata que te debe el operador puede quedar colgada sin
   alerta. (BLOQUEANTE operativo.)
2. **Falta la factura del operador / documento de CxP** (ver arriba). (BLOQUEANTE de modelo ERP.)
3. **Anticipo "a cuenta" saltea topes y mezcla monedas** en el tope global (`ToSurrogateBalance` suma ARS+USD)
   → se puede pagar de más en una moneda; el sobrepago queda flotando sin entidad de prepago aplicable.
4. **Sin vencimiento de pago al operador ni aging** (corriente/30/60/90). El reporte solo lista deuda por moneda.
5. **Diferencia de cambio NO se reconoce** en pagos cruzados (compra USD, pago ARS): no se guarda TC de compra,
   la diferencia se pierde contablemente (resultado por Ganancias no registrado, margen distorsionado).
6. **Operadores `CommissionOnly` (intermediación) igual acumulan CxP completa**: `Supplier.InvoicingMode` no se
   consulta en el cálculo de deuda → deuda inventada cuando el operador factura directo al cliente.
7. **Bomba fiscal para cuando pases a Responsable Inscripto** (hoy emitís Monotributo, latente):
   - Las **retenciones que te hace el operador reducen el saldo del cliente** (`OperatorRefundService` resta
     TODAS las deducciones al `NetAmount` del crédito del cliente). Contradice la regla cerrada "las retenciones
     NO se pasan al cliente, son crédito fiscal de la agencia". Hoy bloqueado porque Mono no admite esas
     retenciones; mal cableado para RI.
   - La agencia no modela **retener al pagar** al operador (agente de retención Ganancias/IVA/IIBB).
8. Menores: tarjetas de resumen de `SupplierAccountPage.jsx` mezclan ARS+USD en un número; `ProviderRefundRequest=8`
   es enum dormido (sin endpoint); `ServicePublicId` polimórfico sin limpieza al borrar el servicio (verificar);
   comentario stale en `CancellationConceptKind.OperatorPenaltyPassThrough`.

## Modelo objetivo (ERP, reusando lo construido)
- **`OperatorBill`** (factura/deuda del operador): cabecera (SupplierId, ReservaId/file, Currency, BillNumber,
  BillDate, DueDate, Status) + líneas por servicio (NetCostBilled vs NetCost cargado = base del three-way match).
  Estados Draft→Received→Approved/Matched→PartiallyPaid→Paid→Closed (+Disputed, +Credited). `CurrentBalance`
  pasa a derivar de los bills. El cálculo puro de hoy convive como vista hasta migrar.
- **`SupplierPayment`** (existe) se imputa a bills; el anticipo se vuelve **`SupplierPrepayment`** aplicable.
- **`SupplierCreditMemo/DebitMemo`** (NC/ND del operador). El `OperatorRefundReceived` actual es el COBRO de la CxC.
- **Refund receivable** = lo que ya hay, pero **cerrado por vencimiento** (job sobre `OperatorRefundDueBy`).
- **Aging** por DueBy y moneda, extendiendo el reporte actual.
- Respetar `InvoicingMode` (CommissionOnly no acumula CxP).
- (Post-RI / contador) Separar retenciones del saldo del cliente; retención al pagar; diferencia de cambio.

## Decisiones pendientes de Gastón / contador
- ¿Construir la "factura del operador" como base ahora (arreglo de fondo) o primero los arreglos puntuales?
- ¿Usás intermediación (operador factura directo al cliente, vos solo comisión) o siempre comprás y revendés?
- Días de caducidad del reembolso del operador (default a proponer) + a quién avisa.
- (Contador, futuro RI) retenciones, agente de retención, diferencia de cambio, costo sin factura del operador.

## Archivos clave
Domain: `SupplierDebtCalculator.cs`, `Supplier.cs`, `SupplierPayment.cs`, `SupplierBalanceByCurrency.cs`,
`OperatorRefundReceived.cs`/`OperatorRefundAllocation.cs`, `DeductionKind.cs`/`DeductionLine.cs`,
`BookingCancellationStatus.cs` (AbandonedByOperator muerto), `CancellationConceptKind.cs`.
Infra: `SupplierDebtPersister.cs`, `SupplierService.cs`, `OperatorRefundService.cs`, `BookingCancellationService.cs`,
`ReportService.cs`, `CashLedgerEntryFactory.cs`. Controllers: `SuppliersController.cs`, `OperatorRefundsController.cs`.
Front: `src/TravelWeb/src/features/suppliers/` (incluye bug monedas mezcladas en `SupplierAccountPage.jsx`).
