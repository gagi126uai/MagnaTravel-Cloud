# 2026-06-26 — Auditoría ERP COMPLETA: Proveedores + Reservas (mirada de PRODUCTO multi-condición fiscal)

Pedido de Gastón: rediseño + análisis crítico, imparcial, cuestionador y EXHAUSTIVO ("indicá si falta algo")
de Proveedores y Reservas, comparado contra un ERP. Óptica corregida (Gastón 2026-06-26): el software es para
VENDER a muchas agencias de CUALQUIER condición fiscal (RI/Mono/Exento), NO para un monotributista → los huecos
que servían a una agencia RI son gaps REALES, no "futuros". Modelo dual confirmado: la agencia trabaja reseller
(compra y revende) Y comisión (intermediación) según el operador. Hecho por lectura de código real (4 auditorías:
ERP procure-to-pay, ERP order-to-cash, fiscal/contable, completitud). Complementa
`2026-06-26-auditoria-critica-proveedores-erp.md`.

Decisiones de Gastón ya tomadas: (Q1) primero los agujeros, después la "factura del operador". (Q2) soportar
ambos modelos (reseller + comisión) por operador.

---
## PROVEEDORES / Cuentas por Pagar

### Conservar (bien hecho)
Motor de deuda puro + un solo persister + saldo por moneda; pago al operador con caja atómica e imputación validada;
desglose por expediente; módulo de reembolso del operador (allocations N:M + deducciones tipificadas); UI de cuenta
del proveedor; derivación multi-condición del tipo de comprobante (A/B/C) y de la ND desde el original (ya sirve a RI).

### (a) Arreglos puntuales YA (no fiscales / correctitud)
1. **Respetar `InvoicingMode` en la deuda** (BUG de plata): operador `CommissionOnly` (intermediación) NO debe generar
   CxP por el costo total — hoy `SupplierDebtCalculator`/`Persister` lo ignoran. (Q2 lo vuelve obligatorio.)
2. **Cerrar el ciclo del reembolso del operador**: `AbandonedByOperator` es código MUERTO + no hay job sobre
   `OperatorRefundDueBy` → la plata que te debe el operador no caduca ni avisa. Job + transición + alerta.
3. **Endurecer el anticipo "a cuenta"**: hoy saltea topes por reserva/servicio y el tope global mezcla ARS+USD
   (`ToSurrogateBalance`) → permite pagar de más en una moneda. Tope por moneda real.
4. **Bug UI**: tarjetas de resumen del proveedor mezclan ARS+USD (`SupplierAccountPage.jsx:738,754`).
5. **Campos baratos de maestro**: datos bancarios (CBU/alias/SWIFT) + términos de pago por defecto en `Supplier`
   (habilitan pago real y vencimientos).

### (b) Etapa grande: documento "Factura del operador" (vendor bill)
Entidad `OperatorBill` por file/operador (nro, fecha, **vencimiento**, moneda, **IVA discriminado/percepciones**, CAE,
adjunto PDF, estados Received→Matched→PartiallyPaid→Paid→Closed +Disputed +Credited). De acá cuelgan casi gratis:
three-way match (facturado vs cargado), aging/vencimientos, conciliación open-item por factura, adjunto documental,
comprobante de pago al operador, prepago aplicable, y **crédito fiscal IVA + deducibilidad Ganancias para RI**.
- **Carga: MANUAL + adjuntar PDF** (lo esencial; es lo que un ERP necesita). Gastón (2026-06-26): arrancar así.
- **OCR (lectura automática del PDF que precarga los campos) = MEJORA A FUTURO, anotada, NO ahora** (Gastón:
  "dejalo anotado para futuro"). Es comodidad de carga, nunca 100% confiable (formatos varían, siempre se confirma),
  más costosa de construir/mantener (a veces servicio externo). Dejar la pantalla preparada para enchufarlo después.

### (c) Fiscal / RI — requiere CONTADOR
- **Retenciones del operador NO deben bajar el saldo del cliente** (`OperatorRefundService.cs:423-424` resta TODAS las
  deducciones): para una agencia RI el cliente cobra de menos y la agencia no registra su crédito fiscal. CRÍTICO de
  producto. (Respeta la regla cerrada "retenciones no se pasan al cliente" — hay que cablearlo bien.)
- **Retenciones que practica la agencia al pagar** al operador (agente de retención Ganancias/IVA/IIBB) + certificados.
- **Diferencia de cambio realizada** en pagos cruzados (guardar TC de compra).

### (d) Descartar (gold-plating para una agencia)
Órdenes de compra/commitment, corridas de pago/batch, conciliación bancaria, límite de crédito al proveedor,
revaluación de saldos, aprobación 4-eyes sobre el pago (el control es log inmutable + contador, no un gate sobre uno mismo).

---
## RESERVAS / circuito de venta (order-to-cash)

### Conservar (muy bien hecho — nivel ERP)
Máquina de estados con fuente única (back+front no divergen); matriz de capacidades pura y testeada con cross-check
a los guards; facturación desacoplada del estado (ADR-037, eje `InvoicingStatus`); cálculo de plata por moneda sin
mezclar; anular vs cancelar bien distinguidos; aviso de descuadre vendido-vs-facturado; NC parcial con ítems no
reintegrables.

### (a) Arreglos / decisiones YA
1. **Intermediación vs reseller NO cambia la FACTURA DE VENTA** (`SupplierInvoicingMode` solo se mira en la cancelación,
   no en `InvoiceSuggestedItemsBuilder`): una agencia que trabaja por comisión facturaría el BRUTO en vez de su comisión
   → ingreso/IVA/Ganancias mal. Es el otro lado del modelo dual (Q2). **Correctitud + fiscal → contador para la fórmula,
   pero el motor de venta debe ramificar por modo.**
2. **Descuento con autorización: cableado a medias y MUERTO** (permiso + umbral + tipo de aprobación + seed existen, pero
   ningún servicio crea la aprobación y NO hay campo "descuento" — el precio se baja a mano). El dueño cree que tiene un
   tope y no lo tiene. → DECISIÓN: construirlo de verdad (campo + tope + autorización) o sacar la promesa muerta.
3. **Límite de crédito del cliente: campo MUERTO** (`Customer.CreditLimit` solo se copia a DTO, nunca valida). → DECISIÓN:
   construir (cuenta corriente a empresas) o sacarlo.

### (b) Deberían (mejoran el producto)
4. **Reporte de conversión presupuesto→venta** (la data está: estados Quotation/Budget/Lost + SourceQuoteId/SourceLeadId).
5. **Lista de precios / markup configurable + política de redondeo** (hoy el precio de venta es libre, todo manual).

### (c) Fiscal / contable — CONTADOR
6. **Reconocimiento de ingreso** (hoy es base caja; la comisión del vendedor devenga solo al 100% cobrado): definir devengo.
7. **Diferencia de cambio en la venta** (cobrar ARS una venta USD no registra resultado por TC).

### (d) Descartar
Control de cupos/allotment/stock (el inventario lo tiene el operador, no la agencia).

---
## Hilo común entre los dos módulos
- **Modelo dual reseller/comisión** mal respetado en AMBOS lados (deuda del proveedor Y factura de venta): es el cambio
  transversal #1 tras la decisión Q2.
- **Diferencia de cambio** ausente en compra Y venta.
- Varias **promesas muertas** (descuento, límite de crédito, AbandonedByOperator, ProviderRefundRequest): o se construyen
  o se limpian — un producto para vender no debe mostrar controles que no controlan.

## Estado / próximos pasos
Análisis cerrado. Plan acordado: Stage 1 = agujeros puntuales (no fiscales primero); Stage 2 = factura del operador;
fiscal/RI con contador. Las pantallas nuevas pasan por gate UX con Gastón. Pendiente decisión de Gastón: descuento y
límite de crédito (construir vs limpiar).
