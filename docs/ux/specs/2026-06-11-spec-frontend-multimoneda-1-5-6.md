# Spec frontend — Multimoneda en 3 pantallas + el cobro

> **Para:** `frontend-senior`. **Autor:** `ux-ui-disenador`. **Fecha:** 2026-06-11.
> **Insumos aprobados que esta spec calca (no se desvía sin repreguntar a Gastón):**
> - Mockup v3: `docs/ux/mockups/2026-06-10-multimoneda-v3-pantallas-reales.html` (PANTALLA 1 reserva, PANTALLA 5 caja, PANTALLA 6 reportes). **La PANTALLA 4 proveedor está POSTERGADA** (`docs/architecture/adr/ADR-021-POSTERGADO-pantalla-proveedor.md`) — NO se construye acá.
> - Mockup v2: `docs/ux/mockups/2026-06-10-multimoneda-v2.html` (cobro, solapa Estado de Cuenta, casos normal y cruzado).
> - Guía del dueño: `docs/ux/guia-ux-gaston.md`, secciones "Multimoneda 2026-06-09" y "Multimoneda 2026-06-10".
>
> **El backend (ADR-021 capas 4/5/6) ya está construido y expone los datos por moneda.** Esta spec NO diseña pantallas nuevas: solo dice cómo cada número/columna existente pasa a mostrar dos monedas cuando de verdad hay dos.

---

## Las 3 reglas innegociables (valen en TODAS las pantallas de abajo)

Vienen del negocio/contador (guía 2026-06-09, "tres reglas duras"). No se discuten ni se optimizan:

1. **Pesos y dólares SIEMPRE separados. NUNCA un total convertido ni sumado** a una sola moneda en estas pantallas.
2. **NUNCA aparece la frase "diferencia de cambio"** en ninguna parte.
3. **Mono-moneda = idéntico a hoy.** La segunda cifra/columna aparece SOLO cuando la reserva (o la caja, o el reporte) tiene de verdad dos monedas. Una reserva toda en pesos se ve exactamente como ahora, sin ruido.

**Cómo se decide "hay dos monedas" en cada pantalla:** por la lista `porMoneda` que entrega el backend (en reserva: `reserva.esMultimoneda` / `reserva.porMoneda.length > 1`). Si hay una sola línea de moneda → render mono-moneda (igual que hoy). Si hay dos o más → render desdoblado. **Nunca** asumir dos monedas por defecto.

**Convención visual del cartelito de moneda (calcado del mockup, los dos mockups lo usan igual):**
- Pesos: etiqueta `$` con fondo verde-petróleo (teal), pegada al monto. En el mockup: `.mon.ars` (fondo `#0f766e`).
- Dólares: etiqueta `US$` con fondo índigo, pegada al monto. En el mockup: `.mon.usd` (fondo `#4338ca`).
- Cuando hay dos cifras en un mismo número, van **una arriba de la otra** (pesos arriba, dólares abajo), no en la misma línea — salvo donde el mockup explícitamente las pone con punto medio (líneas de TOTAL al pie).

---

## Pieza transversal — `formatCurrency(amount, currency)`

**Problema actual:** hay DOS `formatCurrency` y los dos hardcodean la moneda.
- `src/TravelWeb/src/lib/utils.js` → formatea SIEMPRE como `USD` (en-US). Lo usan reserva, reportes, cuenta cliente, buscador, etc.
- `src/TravelWeb/src/features/payments/lib/financeUtils.js` → formatea SIEMPRE como `ARS` (es-AR). Lo usan las pantallas de payments.
- Además, varias filas formatean inline (ej. `payment.amount?.toLocaleString("es-AR", { style:"currency", currency:"ARS" })` en ReservaDetailPage; `new Intl.NumberFormat(... "ARS")` en MovementsTab).

**Qué hay que hacer (sin diseñar nada visual nuevo):**
- Parametrizar ambos `formatCurrency` para que acepten un segundo argumento `currency` (`"ARS"` | `"USD"`), con default = el comportamiento de hoy de cada archivo para no romper los call sites que no pasan moneda. El símbolo y el locale se derivan de la moneda: ARS → `$` / es-AR; USD → `US$` / es-AR (mostrar `US$`, no `$`, para distinguir a ojo del peso — coincide con el cartelito del mockup).
- **Revisar los call sites** (lista completa abajo) y, en las pantallas de esta spec, pasar la moneda real de cada línea `porMoneda[i].currency` / `movement.currency` / etc. En el resto de los call sites (fuera de estas 3 pantallas + cobro) **no tocar nada** en esta tanda: mantienen su default y se ven igual que hoy.
- **No** crear un tercer helper ni unificar los dos archivos en esta tanda (sería refactor fuera de alcance; `frontend-senior` puede proponerlo aparte).

**Call sites de `formatCurrency` a tener en cuenta** (de `grep`): `ReservaSummaryStrip.jsx`, `ReservaKPIs.jsx`, `ReservaTable.jsx`, `ReservaMobileList.jsx`, `ReportsPage.jsx`, `CustomerAccountPage.jsx`, `SupplierAccountPage.jsx`, `FinanceMetricsGrid.jsx`, `financeUtils.js`, `CollectionsTab.jsx`, `InvoicingTab.jsx`, `PaymentsHistoryPage.jsx`, `PaymentsHomePage.jsx`, `PaymentsTrashPage.jsx`, `CustomerPaymentModal.jsx`, `SupplierPaymentModal.jsx`, `CreateInvoiceModal.jsx`, `SearchPalette.jsx`, customer/supplier tables y mobile lists, `lib/utils.js`.
- **De esta tanda se tocan de verdad:** `ReservaSummaryStrip.jsx`, `ServiceList.jsx`, la solapa Estado de Cuenta de `ReservaDetailPage.jsx` + ficha de cobro, `FinanceMetricsGrid.jsx` (vía caja), `MovementsTab.jsx`, `ReportsPage.jsx` (solapa Finanzas y Deudas). El resto queda para tandas siguientes (cuenta cliente P5 y proveedor P8 ya están aprobadas en v2 pero NO entran en este encargo de 3 pantallas).

**Mapa de campos del backend (ya existen, en camelCase tras `camelize`):**
- Reserva: `reserva.porMoneda` = lista de `{ currency, totalSale, confirmedSale, totalCost, totalPaid, balance }`; `reserva.esMultimoneda` (bool). `totalCost` ya viene enmascarado (0/oculto) si el usuario no tiene `cobranzas.see_cost`. Los escalares planos de hoy (`reserva.balance`, `reserva.totalSale`, `reserva.totalCost`) se conservan para el caso mono-moneda.
- Pago (request): `currency`, `imputedCurrency`, `exchangeRate`, `exchangeRateSource` (int del enum), `exchangeRateAt`, `imputedAmount`. Si la moneda del pago = moneda del saldo imputado, NO se mandan los de TC (`imputedCurrency` null/igual).
- Caja: `summary` con caja real por moneda + cada `movement.currency`.
- Reportes: dashboard con `porMoneda` (cobros/pagos/ventas/costos/saldo/cuentas por pagar por moneda) + deudoras por moneda (`currency` en cada item).

> **Aclaración para frontend-senior:** los nombres exactos de los sub-campos de `summary` (caja) y del dashboard (reportes) por moneda hay que tomarlos del DTO real que devuelve el endpoint — esta spec describe el QUÉ se muestra, no inventa nombres de campo de esos dos summaries. Si un campo por-moneda esperado no viene, es bug de backend, no se rellena con un cálculo en el front (regla: no duplicar reglas de negocio ni convertir monedas en el front).

---

## PANTALLA 1 — La reserva (franja de números + lista de servicios)

### 1.1 Componente/archivo real
- Franja de plata: `src/TravelWeb/src/features/reservas/components/ReservaSummaryStrip.jsx`.
- Lista de servicios: `src/TravelWeb/src/features/reservas/components/ServiceList.jsx` (tabla desktop + tarjetas mobile).

### 1.2 Qué cambia exactamente (calcado de PANTALLA 1 del v3)

**Franja de arriba (`ReservaSummaryStrip`).** Hoy son 3 números limpios: **Saldo a Cobrar** · **Recaudado** · **Inversión (Costo)** (este último solo admin / `see_cost`). El layout de 3 columnas **NO cambia**. Lo único: cuando `reserva.esMultimoneda`, **dentro de cada número aparecen las dos cifras, una arriba de la otra** (pesos arriba con `$`, dólares abajo con `US$`), cada una con su cartelito.

- **Saldo a Cobrar:** dos líneas (pesos / dólares). Debajo, el "de $X presupuestado" también por moneda: `de $ 205.000 / US$ 450 presupuestado` (una sola línea con las dos cifras, separadas por `/`, como el v3). Datos: por moneda, `balance` y `totalSale` de cada `porMoneda[i]`.
- **Recaudado:** dos líneas (pesos / dólares). Hoy se calcula sumando `reserva.payments`; en multimoneda se muestra **lo recaudado por moneda** (tomar de `porMoneda[i].totalPaid`, que es la fuente canónica por moneda; no sumar pagos de monedas distintas en un solo número).
- **Inversión (Costo) — solo admin/`see_cost`:** dos líneas (pesos / dólares), de `porMoneda[i].totalCost`. Si el usuario no ve costos, esta columna no aparece (igual que hoy) en ninguna de las dos monedas.

> **NOTA sobre la PREGUNTA 1 del v2:** en el v2 se preguntó si la franja pasaba de 3 a 4 cajitas. **Esa pregunta quedó RESUELTA por el v3 aprobado:** se queda en **3 columnas**, y el desdoble es de dos líneas DENTRO de cada columna (no una cuarta caja). Construir según v3.

**Lista de servicios (`ServiceList`).** Cada monto de la fila (Precio Venta, y Costo Neto si `see_cost`) lleva su cartelito `$`/`US$` según la moneda de ESE servicio. Hoy los montos se imprimen con `${valor.toLocaleString(...)}` hardcodeado a `$` — pasa a mostrar el cartelito de la moneda real del servicio (campo de moneda del servicio que ya entrega el backend; si el servicio no trae moneda explícita, usar la de la reserva mono-moneda). Aplica en desktop (celdas "Costo Neto" y "Precio Venta", líneas ~654-660) y en mobile (líneas ~793-805).

**Total al pie de la lista de servicios** (regla "Listados y tablas" 2026-06-09, P2): cuando la lista mezcla monedas, agregar UNA fila de TOTAL al pie con cada moneda separada por punto medio: `TOTAL: $ 205.000 · US$ 450`. Calcado del v2 (fila `.totalfila`). **OJO:** `ServiceList` HOY no tiene fila de total — hay que agregarla. Si todos los servicios son de una sola moneda, total = un solo número (o el comportamiento de hoy, que es sin fila de total; ver pregunta abierta P-A).

### 1.3 Comportamiento mono-moneda (regla ③)
- `reserva.esMultimoneda === false` (o `porMoneda.length <= 1`): la franja se ve **exactamente como hoy** — un solo "Saldo a Cobrar", un solo "Recaudado", una sola "Inversión", cada uno con una cifra. La lista de servicios muestra los montos con un solo cartelito de moneda (la de la reserva), sin segunda línea, sin fila de TOTAL por moneda (ver P-A).

### 1.4 Qué NO hacer
- No convertir dólares a pesos ni mostrar un "total general".
- No agregar una cuarta columna a la franja (el v2 lo insinuaba; el v3 lo descartó).
- No tocar el resto de la franja (estados, candado, KPIs de etapa): fuera de alcance de moneda.

---

## EL COBRO — solapa "Estado de Cuenta" + ficha de cobro en línea

### 2.1 Componente/archivo real
- Solapa "Estado de Cuenta": dentro de `src/TravelWeb/src/features/reservas/pages/ReservaDetailPage.jsx`, bloque `activeTab === "account"` (líneas ~837-940): botón "Registrar Cobranza" + historial de cobranzas (DataGrid).
- Form de cobro hoy: `src/TravelWeb/src/components/CustomerPaymentModal.jsx` (es un **modal**).

### 2.2 Qué cambia exactamente (calcado de PANTALLA 2 del v2, aprobada)

**(a) El cobro deja de ser modal y pasa a ficha EN LÍNEA** (guía 2026-06-09 P3: "el modal me parece horrible"; el cobro se carga en línea, debajo, igual que la carga de servicios). El botón "Registrar Cobranza" deja de abrir `CustomerPaymentModal`: en su lugar abre una **ficha que se despliega ahí mismo, debajo del botón / sobre el historial**, dentro de la solapa Estado de Cuenta. Al confirmar, la ficha se cierra y la nueva cobranza aparece en el historial. (El `CustomerPaymentModal` puede dejar de usarse desde la reserva; verificar si lo usa otra pantalla antes de borrarlo — lo usa también la cuenta del cliente, que NO entra en esta tanda, así que **no borrar el modal**, solo dejar de invocarlo desde la reserva.)

**(b) Wording "cobro" en todos lados** (guía 2026-06-10): unificar a la palabra **cobro**.
- Botón que abre la ficha: **"Registrar cobro"** (hoy "Registrar Cobranza").
- Botón que guarda dentro de la ficha: **"Confirmar"** (hoy "Registrar Pago"/"Confirmar Pago").
- Título de la ficha: "Registrar cobro".

**(c) Historial de cobranzas gana columna "Moneda"** (entre Notas y Importe, o donde el grid lo permita sin romper). Cada fila muestra el cartelito `$ Pesos` / `US$ Dólares` de la moneda del cobro. El importe de cada fila se formatea con la moneda real de ESE cobro (hoy está hardcodeado a ARS en `payment.amount?.toLocaleString("es-AR", {currency:"ARS"})`, líneas ~886/904/938).

**(d) Subtotal del historial por moneda:** al pie del historial, una línea de subtotales por moneda: `$ 85.000 · US$ 100` (calcado del v2, fila `.totalfila`). Solo cuando hay cobros en más de una moneda.

**(e) La ficha de cobro en línea — campos (calcado del v2):**

Caso normal (pago en la MISMA moneda que el saldo):
```
Registrar cobro
[ Monto ]        [ Moneda del pago ▾ ]   [ Imputar a ▾ ]
[ Método ▾ ]     [ Nota (opcional) .................. ]
                                   [ Cancelar ]  [ Confirmar ]
```
- **Monto:** numérico, requerido.
- **Moneda del pago:** desplegable `$ Pesos` / `US$ Dólares`.
- **Imputar a:** desplegable con los saldos de la reserva (`Saldo en $` / `Saldo en US$`). Por defecto, el saldo de la misma moneda del pago.
- **Método:** igual que hoy (Efectivo/Transferencia/Tarjeta/Cheque/Depósito).
- **Nota (opcional):** igual que hoy.

### 2.3 El cobro cruzado (calcado del v2 + guía 2026-06-09 P4 y 2026-06-10)

**Cuándo aparece el recuadro de tipo de cambio:** SOLO cuando la "Moneda del pago" es DISTINTA del saldo elegido en "Imputar a". Si coinciden, no aparece nada (cobro normal). El recuadro aparece/desaparece en vivo al cambiar cualquiera de los dos desplegables.

**Cómo se ve la fila/recuadro único de TC** (recuadro punteado índigo, debajo de la primera fila de campos):
```
↕ Pagás en pesos para bajar deuda en dólares: decinos el tipo de cambio
[ 1 US$ = $  1.200 ]   [ Fuente ▾ ]   [ Fecha  23/06/2026 ]
→ Se cancelan US$ 100 de la deuda en dólares
```
- **Tipo de cambio** (`1 US$ = $___`): numérico, **obligatorio cuando cruza** → `exchangeRate`.
- **Fuente:** desplegable (Manual / la/s fuente/s que exponga el backend, enum `ExchangeRateSource`) → `exchangeRateSource` (int). **Obligatorio cuando cruza.**
- **Fecha:** date, default hoy → `exchangeRateAt`. **Obligatorio cuando cruza.**
- **Línea de resultado** (calculada en vivo, NO editable): `→ Se cancelan US$ 100 de la deuda en dólares`. El número es `imputedAmount` = monto del pago ÷ tipo de cambio (o ×, según dirección), redondeado. Texto exacto del v2: "Se cancelan US$ {X} de la deuda". (Guía 2026-06-09 P4: texto ajustable si Gastón lo pide; por ahora este.)

**Qué se manda al backend en el cobro cruzado:** `currency` (moneda del pago real que entró), `imputedCurrency` (moneda del saldo que baja), `exchangeRate`, `exchangeRateSource`, `exchangeRateAt`, `imputedAmount` (el equivalente que muestra la línea de resultado). En cobro normal: solo `currency` (= moneda del saldo), sin los campos de TC.

**Regla de un solo saldo por cobro** (guía 2026-06-09 P4): cada cobro es UNA moneda contra UN saldo. Si el cliente paga mitad pesos / mitad dólares, son **dos cobros separados**. La ficha NO permite imputar a dos saldos a la vez.

**En el historial, el cobro cruzado se ve como UNA sola fila** (guía 2026-06-10): el importe REAL que entró (ej. `$ 120.000`), con el detalle de a qué saldo imputó dentro de la misma fila (ej. en Notas o un renglón chico: "imputado a US$ 100"). **NO** dos filas. (La caja también registra solo lo real que entró — ver Pantalla 5.)

### 2.4 Comportamiento mono-moneda
- Reserva de una sola moneda: la ficha se ve casi como hoy. "Moneda del pago" e "Imputar a" pueden quedar fijos/ocultos a esa única moneda (ver pregunta abierta P-B), nunca aparece el recuadro de TC, el historial no muestra columna Moneda ni subtotales por moneda (un solo importe como hoy).

### 2.5 Estados (flujo importante — pago)
- **Cargando/guardando:** botón "Confirmar" deshabilitado + "Guardando…", sin doble submit.
- **Error al guardar:** la ficha queda abierta con todo lo cargado intacto + mensaje de error claro arriba de los botones (consistente con la regla de la ficha de servicios, guía Ronda 2). Nunca se pierde lo cargado.
- **Validación cruzada:** si cruza de moneda y falta TC, fuente o fecha → bloquear "Confirmar" con mensaje claro ("Falta el tipo de cambio / la fuente / la fecha").
- **Éxito:** ficha se cierra, toast de éxito, historial y saldos de la franja se refrescan.

### 2.6 Qué NO hacer
- No mostrar "diferencia de cambio" en ningún texto del recuadro ni del historial.
- No permitir editar plata/moneda/TC de un cobro cruzado ya registrado: se anula y se recrea (sí se pueden editar notas/método/referencia). Límite del ADR-021 — si el grid de edición hoy permite cambiar el monto, ver pregunta abierta P-C.
- No abrir ventana encima para cobrar.

---

## PANTALLA 5 — Caja

### 3.1 Componente/archivo real
- Página: `src/TravelWeb/src/features/payments/pages/PaymentsCashPage.jsx` (toolbar + 3 métricas + movimientos).
- Métricas de arriba: `src/TravelWeb/src/features/payments/components/FinanceMetricsGrid.jsx`.
- Lista de movimientos: `src/TravelWeb/src/features/payments/components/MovementsTab.jsx`.

### 3.2 Qué cambia exactamente (calcado de PANTALLA 5 del v3)

La pantalla real son **3 números arriba** (Ingresos del mes · Egresos del mes · Resultado de caja del mes) + **lista de movimientos**. El layout NO cambia (no son "dos cajas"; eso fue invento descartado en la guía 2026-06-10). Lo que cambia:

**(a) Los 3 números muestran pesos y dólares por separado** (dos líneas dentro de cada tarjeta, pesos arriba con `$`, dólares abajo con `US$`), tomados del `summary` por moneda. `FinanceMetricsGrid` hoy recibe `items: [{label, value}]` y formatea un solo número. Para multimoneda, cada tarjeta pasa a poder mostrar dos cifras (ej. `value` por moneda, o un nuevo shape `{ label, valuesByCurrency: [{currency, value}] }`). Mantener el caso de una cifra para mono-moneda y para los `items` con `isCount`.

**(b) Cada movimiento de la lista lleva su moneda.** En `MovementsTab`, agregar una **columna "Moneda"** (cartelito `$ Pesos` / `US$ Dólares`) y formatear el monto con `movement.currency` real (hoy hardcodeado a ARS con `new Intl.NumberFormat(... "ARS")`, líneas ~25-29 y ~355/426). Vale para desktop (DataGrid) y mobile (MobileRecordCard).

**(c) Filtro nuevo "Moneda" en la barra** (calcado del v3): en `PaymentsCashPage`, junto a los filtros "Ingresos y egresos" y "Todos los orígenes", agregar un tercer desplegable **"Todas las monedas ▾"** con opciones Todas / Pesos / Dólares, que filtra la lista de movimientos por moneda. (El backend ya entrega `currency` por movimiento; confirmar si el filtro se hace client-side o con un parámetro del endpoint — ver pregunta abierta P-D.)

### 3.3 Comportamiento mono-moneda
- Si en el período la caja movió una sola moneda: los 3 números muestran una sola cifra (como hoy), la lista no necesita la segunda línea, y el filtro "Moneda" puede quedar oculto o con una sola opción. (Confirmar si el filtro se muestra siempre o solo con dos monedas — ver P-D.)

### 3.4 La caja registra lo REAL que entró (regla dura, guía 2026-06-09 P6)
En un cobro cruzado, la caja muestra lo que el cliente pagó **de verdad** en su moneda (ej. entran `$ 120.000` en la caja de pesos). La caja NO inventa dólares que no entraron. Esto ya lo resuelve el backend; el front solo muestra `movement.currency` + `movement.amount` tal cual vienen.

### 3.5 Qué NO hacer
- No sumar pesos + dólares en "Resultado de caja". Son dos cifras separadas.
- No convertir a una moneda base.
- No rediseñar la caja como "dos cajas lado a lado" (eso es del v2 viejo, corregido por v3).

---

## PANTALLA 6 — Reportes (solapa "Finanzas y Deudas")

### 4.1 Componente/archivo real
- `src/TravelWeb/src/pages/ReportsPage.jsx`, `TabsContent value="finance"` (líneas ~418-533): 4 KpiCard + dos Card (Cuentas por Cobrar / Cuentas por Pagar).

### 4.2 Qué cambia exactamente (calcado de PANTALLA 6 del v3)

La pantalla real son **4 tarjetas** (Cobros · Pagos · Flujo Neto · Deuda Clientes) + **dos listas** (Cuentas por Cobrar / Cuentas por Pagar). El layout NO cambia. Lo que cambia:

**(a) Cada una de las 4 tarjetas muestra las dos monedas, una sobre otra** (pesos arriba con `$`, dólares abajo con `US$`). Hoy `KpiCard` recibe un `value` ya formateado con `formatCurrency(...)`; pasa a poder mostrar dos cifras por moneda, tomadas del `porMoneda` del dashboard (cobros/pagos/flujo/deuda por moneda). El cálculo de cada cifra viene del backend por moneda — **no** sumar/restar across monedas en el front (hoy `cashFlow` y los `reduce(...)` de deuda se calculan en el front sumando todo; en multimoneda eso pasa a venir por moneda del dashboard, o se agrupa por `currency` sin mezclar).

**(b) En cada lista (Cobrar / Pagar), el monto de cada fila lleva su `$`/`US$`** según la moneda de esa cuenta (`debtor.currency` / `sup.currency`). El "Total a Cobrar" / "Total a Pagar" al pie pasa a ser por moneda: `$ 1.240.000 · US$ 3.150` (calcado del v3, fila `.lrow.tot`), NO un único total sumado.

> **Las listas Cobrar/Pagar ya existen separadas** (una de clientes, otra de proveedores). La moneda es una dimensión MÁS dentro de cada lista — **una misma lista puede tener filas en pesos y filas en dólares**, cada una con su cartelito. NO se parten en cuatro listas. (Calcado del v3: "la moneda es una dimensión más dentro de cada una".)

### 4.3 Comportamiento mono-moneda
- Si toda la agencia operó en una sola moneda en el período: cada tarjeta muestra una sola cifra (como hoy), cada fila un solo cartelito, y el total al pie un solo número.

### 4.4 Permisos
- La solapa Finanzas y Deudas ya está sujeta a sus permisos actuales. La regla de enmascarado de costos (guía 2026-06-05/09) sigue valiendo: quien no ve costos no ve deuda al proveedor, en ninguna de las dos monedas. No cambiar quién ve esta solapa en esta tanda.

### 4.5 Qué NO hacer
- No sumar monedas en ningún KPI ni total.
- No mezclar las dos monedas en una sola lista ordenada por monto sin distinguir (cada fila DEBE decir su moneda).
- No partir Cobrar/Pagar en dos listas por moneda (eso fue el v2 viejo de reportes, corregido por v3: la moneda es una dimensión dentro de cada lista).

---

## Checklist de estados (las 3 pantallas + cobro)

- **Cargando:** spinners/skeletons como hoy; no romper.
- **Vacío:** los empty states actuales se conservan (sin servicios, caja sin movimientos, sin deuda).
- **Permiso denegado / sin `see_cost`:** Inversión y costos tapados en ambas monedas (no aparece la segunda línea de costo tampoco).
- **Error de carga:** mensajes claros, no tragar errores.
- **Mono-moneda:** en las 4, fallback exacto a "como hoy" (regla ③).

---

## PREGUNTAS PARA GASTON

> Son detalles que NO están cerrados ni en los mockups ni en la guía. No los resuelvo solo. Respondé con la letra (ej. "P-A: 1").

### Tema: el total al pie de la lista de servicios cuando hay UNA sola moneda
Contexto: hoy la lista de servicios de la reserva NO tiene una fila de "TOTAL" al pie. Con dos monedas vamos a agregar `TOTAL: $ … · US$ …` (eso ya lo aprobaste). La duda es qué pasa cuando la reserva es de una sola moneda.

**P-A. Con una sola moneda, ¿querés que igual aparezca la fila de TOTAL al pie de los servicios?**
  A) NO — que quede como hoy (sin fila de total cuando hay una sola moneda; el total aparece solo cuando hay dos).
```
   Hotel ......... $ 205.000
   Aéreo ......... $ 120.000
   (sin fila de total)
```
  B) SÍ — mostrar siempre el total al pie, aunque haya una sola moneda.
```
   Hotel ......... $ 205.000
   Aéreo ......... $ 120.000
   TOTAL ......... $ 325.000
```

### Tema: la ficha de cobro cuando la reserva es de una sola moneda
Contexto: la ficha de cobro nueva trae dos desplegables nuevos ("Moneda del pago" e "Imputar a"). En una reserva toda en pesos (lo de todos los días) esos dos desplegables tendrían una sola opción.

**P-B. En una reserva de una sola moneda, ¿qué hago con "Moneda del pago" e "Imputar a"?**
  A) Ocultarlos — que la ficha se vea casi igual que hoy (Monto, Método, Nota), sin desplegables de moneda.
```
   Registrar cobro
   [ Monto ]   [ Método ▾ ]   [ Nota ............ ]
```
  B) Mostrarlos igual, pero ya fijados en la única moneda (no se pueden cambiar).
```
   Registrar cobro
   [ Monto ]  [ Moneda: $ Pesos ]  [ Imputar a: Saldo $ ]
   [ Método ▾ ]   [ Nota ............ ]
```

### Tema: editar un cobro ya registrado
Contexto: hoy en el historial se puede editar un cobro (lápiz) y cambiar el monto. El criterio del sistema dice que un cobro cruzado (pagó en una moneda contra deuda en otra) NO se puede editar en plata/moneda/tipo de cambio: se anula y se hace de nuevo (sí se pueden tocar notas/método). Quiero confirmar cómo lo ves vos.

**P-C. Para un cobro que cruzó de moneda, ¿qué dejamos editar desde el lápiz?**
  A) Solo notas / método / referencia. Para cambiar la plata o el tipo de cambio, se anula y se carga de nuevo.
```
   Editar cobro (cruzado)
   Monto:  US$ 100  (no editable)
   T.C.:   1.200    (no editable)
   [ Método ▾ ]  [ Nota ............ ]  ← lo único que se cambia
```
  B) Otra cosa (contame): por ejemplo, que un cobro cruzado no tenga lápiz y solo se pueda anular.

### Tema: el filtro "Moneda" en la caja
Contexto: en la caja agregamos un filtro nuevo "Moneda" (Todas / Pesos / Dólares). En un día/mes donde solo se movieron pesos, ese filtro tendría una sola opción útil.

**P-D. El filtro "Moneda" de la caja, ¿lo muestro siempre o solo cuando hay dos monedas?**
  A) Mostrarlo SIEMPRE (aunque haya una sola moneda), como los otros filtros.
```
   [ Buscar... ]  [ Ingresos y egresos ▾ ]  [ Orígenes ▾ ]  [ Todas las monedas ▾ ]
```
  B) Mostrarlo SOLO cuando en el período hubo de verdad dos monedas; si hubo una sola, no aparece (menos ruido).
```
   [ Buscar... ]  [ Ingresos y egresos ▾ ]  [ Orígenes ▾ ]
```

### Tema: la factura del aéreo en dólares (toca lo fiscal)
Contexto: ya dijiste que la factura del aéreo se emite y se muestra en dólares (US$), no el equivalente en pesos. Esto NO entra en estas 3 pantallas, pero lo dejo anotado porque toca lo fiscal.

**P-E. (recordatorio, no bloquea estas 3 pantallas) La factura en dólares hay que confirmarla con el contador antes de construir la pantalla de facturación.** ¿Querés que lo dejemos agendado para cuando toquemos facturación, o querés avanzarlo ahora con el contador en paralelo?
```
   (sin dibujo — es una decisión de cuándo, no de cómo se ve)
```
