# 2026-06-10 — Multimoneda: mockups rehechos sobre las pantallas reales + decisiones finas

## Qué pasó en una línea

Gastón frenó los dibujos de multimoneda del 2026-06-09 porque "mezclaban las pantallas" y no se parecían a su sistema. Se rehicieron dos veces hasta calcarlos sobre las pantallas reales del código. Quedaron 2 pantallas aprobadas, 4 corregidas a la espera de su OK final, y 4 decisiones finas cerradas. **Sin cambios de código.**

## El problema que reportó Gastón

1. Primero: "no entendí nada, mezclás las pantallas". El mockup `2026-06-09-multimoneda.html` era un rollo largo de 9 bloques blancos iguales apilados, con variantes/mejoras/preguntas intercaladas. No se distinguía dónde terminaba una pantalla y empezaba otra.
2. Se rehízo (`v2`) con una pantalla por vez, cada una en su "ventana" tipo navegador, numeradas, con las preguntas juntas al final. Gastón aprobó **la 2 (cobro)** y **la 3 (cuenta del cliente)**, pero rechazó **1, 4, 5 y 6**: "no me gusta cómo se ven".
3. La causa real: esas 4 las había dibujado de cabeza, inventando layouts. Las 2 que le gustaron coincidían con pantallas que él reconoce.

## La corrección (v3): calcar el código real

Se leyeron los componentes reales y se redibujó encima:

- **Reserva** (`ReservaSummaryStrip.jsx`): la franja real son **3 números limpios** (Saldo a Cobrar · Recaudado · Inversión, este último solo admin). El mockup viejo había agregado recuadros "antes/después" y una grilla apretada de 4 columnas → descartado.
- **Cuenta del proveedor** (`SupplierAccountPage.jsx`): la pantalla real son **5 tarjetitas** (Servicios, Pagos, Total Compras, Total Pagado, Saldo Pendiente) + dos tablas (Servicios Comprados, Historial de Pagos). El mockup viejo la había dibujado como dos cajas rojo/violeta (clonando la del cliente) → era falso.
- **Caja** (`PaymentsCashPage.jsx` + `FinanceMetricsGrid.jsx` + `MovementsTab.jsx`): la pantalla real son **3 métricas arriba** (Ingresos/Egresos/Resultado del mes) + lista de movimientos con flechas entra/sale. El mockup viejo decía "dos cajas separadas" → era falso.
- **Reportes** (`ReportsPage.jsx`): la solapa **Finanzas y Deudas** tiene 4 tarjetas (Cobros, Pagos, Flujo Neto, Deuda Clientes) + dos listas (Cuentas por Cobrar / Cuentas por Pagar). El mockup viejo inventó "dos columnas por tarjeta + dos rankings" → se ajusta al real.

Multimoneda sobre cada una: cada número de plata muestra las dos cifras (pesos y dólares) separadas, nunca sumadas; cada fila de tabla lleva su cartelito $/US$; filtro de moneda donde corresponde. Las **tres reglas duras** (monedas separadas, nunca "diferencia de cambio", una sola moneda = igual que hoy) se mantienen intactas.

Mockups resultantes:
- `docs/ux/mockups/2026-06-10-multimoneda-v3-pantallas-reales.html` → pantallas 1/4/5/6 (a confirmar).
- `docs/ux/mockups/2026-06-10-multimoneda-v2.html` → pantallas 2/3 (aprobadas).

## Decisiones finas que cerró Gastón

- **Palabra "cobro" en todos lados** (hoy se mezclan "cobranza" y "pago"). Botón "Registrar cobro" abre; botón "Confirmar" guarda.
- **Cobro cruzado en el historial: 1 sola fila** (importe real + detalle de imputación dentro de la fila).
- **Fila de cuenta corriente con dos monedas: un renglón por moneda** dentro de la misma fila.
- **Factura del aéreo en dólares** (no el equivalente en pesos). **Pendiente de confirmar con el contador** porque toca lo fiscal.

## Lección (otra vez la misma)

No inventar pantallas: cuando la decisión es sobre cómo se ve algo que YA existe, leer el componente real primero y dibujar encima. Es la segunda vez que Gastón frena un mockup por dibujarlo de cero ignorando lo existente.

## Qué queda pendiente

1. **OK final de Gastón** sobre el mockup v3 (las 4 pantallas: reserva, proveedor, caja, reportes).
2. Tras el OK: cerrar la spec y construir el **frontend de multimoneda (capa 7)** + las **capas backend 4/5/6** del ADR-021 (registro de pago con TC; exponer PorMoneda en la API con enmascarado see_cost; consumidores de reportes/tesorería/cuenta corriente por moneda).
3. Confirmar con el contador lo de la **factura en dólares** antes de tocar facturación.
4. Sigue pendiente el **deploy** de lo acumulado en `main` (migración Adr021_M1 + backfill multimoneda), que Gastón no llegó a hacer.
