# 2026-06-28 — Blindaje de seguridad + rediseño ERP de 5 pantallas (desplegado)

## Qué se hizo y por qué

Gastón revisó la UI de Proveedores/Cuentas por Pagar y pidió tres cosas: arreglar un error feo, rediseñar varias pantallas "como un ERP de verdad", y crear un control que impida que se filtre información interna al usuario. Todo se hizo, se revisó y se **desplegó a producción** (HEAD `main` `9e097a1`, pipeline success, migración `Adr041_M5` aplicada, servicios healthy).

## 1) Seguridad / exposición de datos

- **Bug original:** la pantalla de "Datos bancarios" del operador/cliente mostraba un código largo (GUID) + un mensaje interno de .NET ("...is not valid"). Causa: el frontend mandaba el identificador público (GUID) donde el backend esperaba un número interno. Fix: el listado/alta de cuentas bancarias ahora aceptan el GUID igual que el resto de la API y lo resuelven internamente.
- **Blindaje GLOBAL de errores:** se interceptan TODOS los errores de validación/binding y los inesperados para que el usuario reciba siempre un mensaje amable en español, nunca inglés, tipo .NET, stack ni detalle interno, en todos los entornos. El discriminador es estructural (no una lista de frases), así que cubre todo el set de validaciones. Los mensajes propios que filtraban nombres internos se reescribieron a español de negocio. Hay un test por reflexión que rompe si algún mensaje vuelve a filtrar un nombre interno.
- **Agente nuevo `data-exposure-reviewer`** (`.claude/agents/`): revisor durísimo que corre en cada cambio importante (gate obligatorio en `CLAUDE.md`) y bloquea si algo técnico/interno puede llegar al usuario. Complementa al de seguridad (que cuida PII/fiscal/plata). Ya demostró su valor: cazó fugas que las otras revisiones dejaban pasar.

## 2) Rediseño ERP (5 pantallas)

Investigado con `erp-systems-expert` (SAP/NetSuite/Dynamics/Odoo) y diseñado con `ux-ui-disenador` (gate UX con Gastón). Spec: `docs/ux/2026-06-28-rediseno-erp-proveedor-cliente-menu.md`.

- **Menú** por módulos colapsables (VENTAS / COMPRAS / CAJA Y BANCOS / RESERVAS / CATÁLOGO / GESTIÓN); las bandejas sueltas se mudaron adentro; "Proveedores" → "Operadores". Sin cambiar rutas ni permisos.
- **Ficha del operador:** dejó de ser una página apilada → encabezado (identidad + saldos en vivo por moneda) + 5 solapas (Cuenta corriente / Servicios comprados / Reembolsos / Datos bancarios / Datos).
- **Cuenta del cliente:** 3 solapas limpias + Datos bancarios; se eliminó "Pagos" (los cobros viven en el extracto). El extracto separa pesos y dólares de verdad; un cobro en otra moneda cae en la moneda de la **deuda que cancela** (imputado), con el detalle "pagó US$ X" — así el saldo cuadra con lo que el cliente debe.
- **Facturación:** se quitó "AFIP" del nombre (queda como chip de estado por comprobante) + filtros (fecha/tipo/estado/moneda/número); y una **pantalla general** con todos los comprobantes de la agencia (gate `cobranzas.view_all`, filtros server-side). El estado "pagada/pendiente" no existe por comprobante (vive a nivel reserva), así que el filtro ofrece el estado fiscal + anulación.
- **Alta de operador:** se abre dentro de la página (no más ventana); pide lo justo (razón social, moneda por defecto, CUIT, condición fiscal) con el interruptor "datos fiscales pendientes", y "Más detalles" cerrado. Backend: nuevo campo `Supplier.DefaultCurrency` (migración `Adr041_M5`, aditiva). La moneda se puede cambiar luego y la edición ya no borra el plazo de pago.

## Reglas que se respetaron
Nada de ventanas que se abren encima, multimoneda dura (jamás sumar pesos con dólares), montos de costo tapados sin permiso, todo en español de negocio sin jerga.

## Follow-ups anotados (no urgentes)
1. Misma minimización de `ForcedByUserId` falta en `InvoiceDto` (detalle) + visor de auditoría.
2. La edición de operador todavía usa el modal viejo (migrar a la solapa "Datos").
3. El extracto del cliente se arma en pantalla con tope de 500 movimientos → conviene un endpoint server-side de estado de cuenta.
4. No hay freno de pago a un operador fiscalmente incompleto (la pata de retención sí está bloqueada por INV-118).
5. Las tarjetas de resumen Ventas/Cobrado siguen rotuladas en pesos (escalares legacy).
