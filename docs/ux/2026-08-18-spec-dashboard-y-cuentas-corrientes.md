# Spec ejecutable — Dashboard "Inicio" + Cuentas corrientes (cliente y operador)

> **Estado:** dirección FIRMADA por Gastón el 18/08 (memoria de proyecto
> `decision-rediseno-dashboard-y-cuentas-corrientes.md`, canvas Claude Design
> `670357ad-68f5-4bf6-b496-006079714048`). Este documento es la LETRA CHICA para
> que `frontend-senior` implemente literal — no reabre la dirección.
> Reglas citadas: P-3, P-4, P-5, P-6, P-7, P-9 (+ enmienda 2026-08-11), P-10,
> P-13, P-14, P-15, P-16 (`docs/estandares/2026-07-22-constitucion-producto-v1.md`)
> · B.0-B.5 (`docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md`, enmienda
> título 700 del 2026-08-18) · moldes de `docs/ux/2026-08-16-guia-rollout-estandar-visual.md`.

## 0. Normalización de colores (defaults, no preguntas)

El brief que trajo esta obra usaba hex aproximados del canvas de Claude Design.
La paleta OFICIAL y única es B.1. Se normaliza así, citando la regla "no
inventar colores" (prohibido #2 de la guía de rollout):

| Hex del brief | Se usa en su lugar | Por qué |
|---|---|---|
| `#0F172A` (tinta) | **`#0B1220`** (Tinta, B.1) | B.1 es la fuente única de tinta |
| `#94A3B8` (labels) | **`#64748B`** (Gris dato, B.1) | B.1 no distingue dos grises; "etiquetas de columna: 11px gris dato" (B.2) ya cubre los labels |
| `#FAFBFC` (franja saldo) | **`#F4F6F9`** (Mesa, B.1) | Mesa es "fondo alrededor de las tarjetas"; sirve igual de bien como panel interno sin inventar un séptimo color |
| `#F59E0B` (línea "por pagar" del gráfico de caja) | **`#B45309`** (Ámbar, B.1) | Mismo ámbar semántico que usa toda la app para "te pide algo/atención"; una sola línea de código de color, cero excepción nueva |

El resto de los hex del brief (`#1D4ED8` primary, `#B91C1C` rojo, `#047857`
verde, `#E2E8F0` línea) coinciden exactamente con B.1 — se usan tal cual.

---

## 1. DASHBOARD "Inicio" — Opción C "Panorama ERP"

### 1.1 Realidad del código (verificada 18/08, hoy)

- Ruta: `src/TravelWeb/src/pages/DashboardPage.jsx` — hoy rutea por
  `user.isAdmin` (booleano) a `AdminDashboard.jsx` o `AgentDashboard.jsx`. **No
  hay ningún camino hoy para un "colaborador" (no admin, pero con
  `reservas.view_all`/`cobranzas.see_cost`) que vea piezas de a una según sus
  permisos reales — ver requisito R1 más abajo.**
- Endpoint: `GET /reports/dashboard` (`Permissions.ReportesView`, refresco cada
  5 min). El backend YA enmascara: sin `cobranzas.see_cost` los campos de costo
  vienen en 0/listas vacías; sin `reservas.view_all` `ReservasPendientes` y
  `ProximosViajes` vienen recortadas a lo propio. El front solo tiene que
  DECIDIR SI MUESTRA la pieza (no recalcular nada, P-13 aplica también a datos).
- `DashboardResponse` trae: `Presupuestos` (int), `Reservados`, `Operativos`,
  `CobrosDelMes`, `SaldoPendiente`, `VentasDelMes`, `CostosDelMes`,
  `MargenBruto`, `PagosProveedores`, `ReservasPendientes[]`, `ProximosViajes[]`,
  `TendenciaHistorica[]`, `DistribucionEstados`, `BnaUsdSellerRate`,
  `ActivePotentialCustomers` (int), y por moneda (`PorMoneda`, nunca mezclada,
  ADR-021 Capa 6): `CobrosDelMes`, `PagosProveedores`, `VentasDelMes`,
  `CostosDelMes`, `MargenBruto`, `SaldoPendiente`, `CuentasPorPagar` (listas
  `[{currency, amount}]`).
- `GET /reports/cashflow?days=90` existe (`AnalyticsPage.jsx` lo consume) pero
  **hoy es `[Authorize(Roles="Admin")]` duro** (no usa el sistema de permisos) Y
  **devuelve un escalar SIN moneda** (`CashFlowDayDto.CashIn/CashOut` suman
  TODOS los pagos sin filtrar por moneda — ver `ReportService.cs:2235-2246`).
  Esto mezclaría pesos y dólares en una sola curva: **viola P-3 tal cual está
  hoy.** Ver requisito R2.
- Huérfanas sin entrada de menú: `/reports` (`ReportsPage.jsx`) y `/analytics`
  (`AnalyticsPage.jsx`). **Default (ya venía en el brief, se adopta): "Ver
  informes" del dashboard es la ÚNICA puerta nueva; no se toca el Sidebar.**
  Motivo: agregar una entrada de menú es un cambio de navegación aparte, no
  pedido hoy — si Gastón lo quiere, se pregunta en otra tanda.

### 1.2 Requisitos de backend (bloqueantes para partes puntuales, no para todo el dashboard)

- **R1 (frontend, no backend) — Reemplazar el switch `isAdmin` por piezas
  gateadas por permiso.** Un ÚNICO componente de dashboard arma su layout
  mirando `hasPermission("reservas.view_all")` y `hasPermission("cobranzas.see_cost")`
  (mismo hook `usePermissions.js` que ya usan `SupplierAccountPage.jsx` y
  `CustomerAccountPage.jsx`), no el booleano `isAdmin`. Así un colaborador con
  esos permisos ve las piezas completas aunque no sea Admin, y un Admin sin
  alguno de esos permisos (no debería pasar hoy, pero el componente no debe
  asumir isAdmin=todo) ve lo mismo que cualquier otro rol con esos permisos.
  Ver tabla de variantes en 1.5.
- **R2 (backend, bloqueante para la tarjeta "Caja proyectada") — `GET
  /reports/cashflow` tiene que separar por moneda.** `CashFlowDayDto` pasa a
  traer `CashIn`/`CashOut`/`RunningBalance` como listas `[{currency, amount}]`
  (mismo contrato que `DashboardByCurrencyDto`), nunca un escalar que sume
  ARS+USD. Regla dura, no es un "se pregunta": P-3.
- **R3 (backend, gate) — `GET /reports/cashflow` pasa de `[Authorize(Roles="Admin")]`
  a `[RequirePermission(Permissions.CobranzasSeeCost)]`** (mismo criterio que
  ya usa el resto del dashboard para ocultar costos/pagos a proveedores a quien
  no tiene `cobranzas.see_cost`), en vez de un candado de rol aparte que no
  sigue el resto del sistema de permisos.
- **R4 (backend, aditivo) — `UpcomingTripDto` (proximos 7 días) necesita 2
  campos nuevos para la maqueta firmada:** el saldo de la reserva (mismo patrón
  que `PendingReservaDto.Balance/Currency`, nunca un escalar suelto) y la
  cantidad de pasajeros. Sin esto, la fila de "Salidas de los próximos 7 días"
  no puede pintar el chip rojo "Debe US$ X" / verde "Saldada" que pide la
  maqueta — ver estado transitorio en 1.4.
- **Pregunta real para Gastón (única de esta spec, ver sección 4) — qué
  significa "Caja proyectada": la curva de tendencia histórica extrapolada
  (lo que YA calcula el endpoint) o un cronograma real de vencimientos de
  cuentas por cobrar/pagar** (habría que construirlo, no existe hoy).

### 1.3 Maqueta — Desktop (rol con todos los permisos: dueño/colaborador completo)

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│  Inicio                                          [Dólar BNA venta $1.234]  [+ Nuevo │
│  El trabajo a la izquierda, la plata a la derecha.                       presupuesto]│
├──────────────────────────────────────┬───────────────────────────────────────────────┤
│  TRABAJO                              │  PLATA                                        │
│                                        │                                               │
│  ┌ Salidas de los próximos 7 días ──┐ │  ┌─────────────┬─────────────┐                │
│  │ Lun 24/08                        │ │  │ POR COBRAR  │ VENDIDO DEL │                │
│  │ María Pérez · Bariloche          │ │  │  $ 450.000  │    MES      │                │
│  │ R-1042 · 4 pax    [Debe US$ 200] │ │  │  US$ 1.200  │  $ 2.100.000│                │
│  │ ─────────────────────────────────│ │  ├─────────────┼─────────────┤                │
│  │ Mar 25/08                        │ │  │ COBRADO DEL │ MARGEN      │                │
│  │ Juan Gómez · Cancún              │ │  │    MES      │  BRUTO      │                │
│  │ R-1050 · 2 pax      [Saldada]    │ │  │ $ 1.800.000 │  $ 380.000  │                │
│  │                                   │ │  └─────────────┴─────────────┘                │
│  │              Ver todas →         │ │                                               │
│  └───────────────────────────────────┘ │  ┌ Caja proyectada — próximos 90 días ──────┐ │
│                                        │  │                                            │ │
│  ┌ Cobros pendientes ────────────────┐ │  │   [gráfico de líneas: Por cobrar azul vs  │ │
│  │ María Pérez · R-1042              │ │  │    Por pagar a operadores ámbar]          │ │
│  │                    $ 450.000 [Cobrar]│ │   Hoy      +30      +60      +90          │ │
│  │ ────────────────────────────────  │ │  └────────────────────────────────────────────┘ │
│  │ Juan Gómez · R-1050                │ │                                               │
│  │                    US$ 200 [Cobrar]│ │  ┌ Informes completos: vendedores, destinos ┐ │
│  │              Ir a Cobranzas →      │ │  │ y año contra año         [Ver informes →]│ │
│  └────────────────────────────────────┘ │  └────────────────────────────────────────────┘ │
│                                        │                                               │
│  ┌ 3 presupuestos abiertos · 5 posibles clientes         [Ir al CRM →]  ┐            │
│  └──────────────────────────────────────────────────────────────────────┘            │
└──────────────────────────────────────┴───────────────────────────────────────────────┘
```

Notas de la maqueta (letra chica que un ASCII no puede mostrar):

- Cabecera: `h1` "Inicio" 24px/700 (B.2, enmienda 18/08) + bajada 14px gris
  dato "El trabajo a la izquierda, la plata a la derecha." + chip del dólar
  (blanco, borde 1px línea, texto "Dólar BNA venta $X") + botón primario
  (único relleno de la pantalla, B.3 regla de oro #2) "+ Nuevo presupuesto".
- **Chip del dólar = el `DolarBnaTira` de hoy, restilizado a formato chip
  compacto, NO uno nuevo.** Conserva sus funciones ya pedidas el 2026-08-05
  (botón actualizar, fecha/antigüedad del dato, desplegable "otras monedas"):
  se mueve de tira ancha debajo del título a chip en la cabecera, misma lógica
  y mismo componente por dentro. Esto es piel + reubicación, no se pierde nada
  ya firmado (si el chip resulta visualmente muy apretado con todo eso adentro,
  frontend-senior decide el detalle de cómo plegarlo — no es una decisión de
  fondo, es implementación).
- Columna TRABAJO (izquierda), de arriba a abajo:
  1. **Salidas de los próximos 7 días**: filas = día (agrupador, no repetir
     fecha si dos salen el mismo día — mismo patrón que separadores de fecha
     ya usados en el resto de la app) → cliente · destino (campo `Name` de
     `UpcomingTripDto`, que ya combina ambos) → n° de reserva · cantidad de
     pasajeros → chip a la derecha: rojo "Debe US$ X" (o "Debe $ X") si
     `balance > 0`, verde "Saldada" si `balance = 0`. Click en la fila navega a
     la ficha (mismo patrón que hoy). Link "Ver todas →" al pie navega al
     listado de Reservas filtrado por próximas salidas (si ese filtro no
     existe hoy en el listado, navega sin filtro — no se inventa un filtro
     nuevo sin pedirlo aparte).
  2. **Cobros pendientes**: filas = cliente · n° reserva → monto en rojo (una
     línea por moneda si la reserva tiene deuda en más de una, P-3) → botón
     secundario chico (`size="sm"`, 32px, outline) "Cobrar" que navega a la
     ficha de la reserva, solapa de cobros (mismo destino que el botón
     "Cobrar" que ya existe en otras pantallas — no se inventa un modal de
     cobro nuevo acá, P-5). Link "Ir a Cobranzas →" al pie.
  3. **Tarjeta chica combinada**: "N presupuesto(s) abierto(s) · N posibles
     clientes" en una sola línea + botón secundario "Ir al CRM →". Reemplaza
     el botón "Posibles clientes" que hoy vive suelto en la cabecera (cambio
     de ubicación AUTORIZADO por la firma de hoy 18/08, no es un huesito que
     yo mueva por mi cuenta).
- Columna PLATA (derecha), de arriba a abajo:
  1. **Grid 2×2 de KPIs**: Por cobrar (rojo) / Vendido del mes / Cobrado del
     mes / Margen bruto (verde). Cada tarjeta: rótulo 11px mayúsculas gris
     dato + número grande 22px/700 (B.2 "número grande de la ficha"). **Cada
     moneda en su propio renglón dentro de la misma tarjeta** (si hay ARS y
     USD, la tarjeta crece de alto, nunca suma) — mismo patrón que ya usa
     `KpiCard` hoy (`lineasPorMoneda`), no se inventa nada nuevo, solo se
     repinta con la tipografía/color de B.1-B.2.
  2. **Caja proyectada — próximos 90 días**: gráfico de líneas, eje X con 4
     marcas (Hoy / +30 / +60 / +90), dos series (azul primary = por cobrar,
     ámbar = por pagar a operadores). **Requiere R2 (moneda) resuelto antes de
     construirse** — si hay más de una moneda con movimiento, son DOS
     gráficos apilados (uno por moneda), nunca una sola curva que mezcle. Ver
     también la pregunta de semántica en 1.2/4.
  3. **Tarjeta-link "Informes completos"**: texto "Informes completos:
     vendedores, destinos y año contra año" + botón secundario "Ver informes
     →" que navega a `/analytics` (o a `/reports` según qué muestre primero
     — default: `/analytics`, porque ahí están vendedores/destinos/año contra
     año que es lo que dice el texto; `/reports` queda accesible desde adentro
     de esa pantalla si ya tiene su propia navegación interna, sin tocarla).

### 1.4 Estado transitorio del chip de deuda en "Salidas próximas" (mientras no está R4)

Hasta que el backend entregue `Balance`/`Currency`/`PassengerCount` en
`UpcomingTripDto` (R4), la fila de "Salidas de los próximos 7 días" se
construye **sin el chip de deuda ni el conteo de pasajeros** (nunca se
inventa un valor, P-13): día → cliente/destino → n° de reserva → el chip de
estado que ya existe hoy (`BadgeStatus`/`traducirEstadoReserva`) en vez del
chip de deuda. En cuanto R4 esté, se cambia el chip de estado por el de deuda
tal cual la maqueta — este es el ÚNICO campo de la spec con un paso
intermedio; todo lo demás se construye completo de una.

### 1.5 Variantes por rol (tabla — quién ve qué pieza)

| Pieza | Dueño / colaborador con `reservas.view_all` + `cobranzas.see_cost` | Vendedor (sin `view_all`, sin `see_cost`) | Colaborador con SOLO uno de los dos permisos |
|---|---|---|---|
| Salidas próximos 7 días | Todas las reservas | Solo las propias (backend ya filtra `ProximosViajes`) | Solo las propias si no tiene `view_all`; todas si sí lo tiene |
| Cobros pendientes | Todas | Solo las propias (backend ya filtra `ReservasPendientes`) | Igual criterio que arriba, por `view_all` |
| Presupuestos abiertos + posibles clientes | Cuenta global | Cuenta global (no depende de `view_all`, `ActivePotentialCustomers`/`Presupuestos` no vienen filtrados por vendedor hoy — se muestra tal cual manda el backend, sin inventar un recorte que el backend no hace) | Igual |
| Grid 2×2 KPIs | Los 4 (Por cobrar / Vendido / Cobrado / Margen) | **Solo 3**: Por cobrar, Vendido, Cobrado. **Margen NO aparece** (sin `see_cost` el backend manda lista vacía en `MargenBruto` — no se pinta una tarjeta con `$0`, se la saca del grid, que pasa a ser fila de 3, no de 4) | Con `see_cost`: los 4. Sin `see_cost`: los 3 |
| Caja proyectada | Sí (ambas series) | **No aparece** (sin `see_cost`, y además "por pagar a operadores" es información de costo) | Con `see_cost`: sí. Sin: no |
| Ver informes | Sí | **No aparece** (`/analytics`/`/reports` piden rol Admin hoy — si eso también pasa a permiso, gatear igual por `see_cost`; mientras siga Admin-only, gatear por `isAdmin`) | Depende de si ese permiso alcanza |

**Layout del grid de KPIs cuando son 3 (vendedor):** una sola fila de 3
tarjetas iguales (no 2+1 desparejo, no hueco vacío donde iba Margen) —
`grid-cols-3` en vez de `grid-cols-2` en desktop.

### 1.6 Estados vacíos

- **Sin salidas próximas:** dentro de la tarjeta, texto centrado 14px gris
  dato: "No hay salidas esta semana." Sin ícono grande, sin ilustración (B.4,
  densidad de mostrador, no pantalla de app de consumo).
- **Sin cobros pendientes:** "No hay cobros pendientes." (sin emoji — el
  molde de la guía visual ya sacó los emojis como íconos; tampoco se usan como
  decoración de texto, prohibido #8 de la guía de rollout).
- **Sin datos de caja proyectada** (30 días sin ningún movimiento real): la
  tarjeta se muestra igual con la leyenda "Todavía no hay movimientos para
  proyectar" en vez del gráfico vacío (un gráfico de líneas sin datos es
  confuso, mejor texto).
- **Presupuestos abiertos = 0 y posibles clientes = 0:** la tarjeta se muestra
  igual con "0 presupuestos abiertos · 0 posibles clientes" (no se esconde:
  es información real, no un error).

### 1.7 Mobile

Una columna. Orden: TRABAJO primero (Salidas → Cobros pendientes →
Presupuestos/CRM), PLATA después (grid de KPIs en 2 columnas, no 4 — sigue
siendo `grid-cols-2` como ya es hoy en `md:grid-cols-2`/`lg:grid-cols-4` — acá
queda en 2 columnas fijo; con 3 KPIs de vendedor, 2+1), Caja proyectada, Ver
informes. El chip del dólar y el botón "+ Nuevo presupuesto" pasan a una fila
propia arriba de todo (stack vertical del header, mismo patrón que ya usa
`flex-col ... lg:flex-row` en el código actual).

```
┌───────────────────────────┐
│ Inicio                    │
│ El trabajo a la izq...    │
│ [Dólar BNA venta $1.234]  │
│ [+ Nuevo presupuesto]     │
├───────────────────────────┤
│ Salidas próximos 7 días   │
│  (lista vertical)         │
│           Ver todas →     │
├───────────────────────────┤
│ Cobros pendientes         │
│  (lista vertical)         │
│         Ir a Cobranzas →  │
├───────────────────────────┤
│ 3 presup. · 5 posibles    │
│                Ir al CRM →│
├───────────────────────────┤
│ [Por cobrar] [Vendido]    │
│ [Cobrado]    [Margen]     │
├───────────────────────────┤
│ Caja proyectada 90 días   │
│  (gráfico, ancho completo)│
├───────────────────────────┤
│ Informes completos...     │
│              Ver informes │
└───────────────────────────┘
```

---

## 2. y 3. CUENTAS CORRIENTES — molde unificado

### 2.0 Molde común (aplica a cliente Y operador)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ← Clientes/Operadores                                                    │
│                                                                            │
│ Nombre del cliente u operador                    [Usar saldo a favor]    │
│ [Condición fiscal] · email · teléfono · CUIT     [+ Nuevo presupuesto]/  │
│                                                    [Registrar pago]       │
├──────────────────────────────────────────────────────────────────────────┤
│ ┌ FOTO DE SALDO — una tarjeta por moneda ────────────────────────────┐   │
│ │┌──────────┬──────────────────────────────────────────────────────┐│   │
│ ││ EN PESOS │  Facturado sin cobrar          $ 300.000             ││   │
│ ││          │  Multas abiertas                $ 50.000             ││   │
│ ││ $350.000 │  Crédito a favor                     $ 0             ││   │
│ ││ Te debe  │                                                      ││   │
│ │└──────────┴──────────────────────────────────────────────────────┘│   │
│ └──────────────────────────────────────────────────────────────────────┘ │
│ (una tarjeta más si hay USD, apilada debajo — nunca una columna al lado) │
├──────────────────────────────────────────────────────────────────────────┤
│  Estado de cuenta  Reservas  Facturación  Datos bancarios  Datos          │
│  ────────────────                                                        │
├──────────────────────────────────────────────────────────────────────────┤
│  [Todas] [Pesos] [Dólares]                                               │
│  FECHA      DETALLE                    DEBE      HABER      SALDO        │
│  15/08/26   Factura A 0001-00001234                                      │
│             Reserva R-1042             $300.000              $300.000    │
│  ...                                                                      │
└──────────────────────────────────────────────────────────────────────────┘
```

- Cabecera: link "← Clientes" (cliente) / "← Operadores" (operador), 13px/600
  color primary (`#1D4ED8`). Nombre 24px/700 (B.2, **unifica**: hoy cliente
  usa `text-xl` (20px) y operador `text-2xl` (24px) — el cliente sube a 24px,
  el operador no cambia). Debajo, UNA línea: chip pill gris de la condición
  fiscal (RI/Monotributo/Exento — mismo texto que ya traduce
  `TAX_CONDITION_LABELS`, nunca el enum crudo) + resto de datos de contacto
  separados por punto medio (email · teléfono · CUIT/DNI), reemplaza las dos
  líneas sueltas de hoy (subtítulo "Operador · CUIT · condición" +  línea de
  contacto aparte en el operador; línea única ya así en el cliente salvo que
  ahí NO hay chip de condición fiscal hoy — se agrega). Acciones a la derecha,
  alineadas con el nombre.
- **Foto de saldo**: reemplaza (a) la tabla de conceptos-por-columna del
  cliente y (b) los 3 recuadros lado a lado del operador. Ahora es **una
  tarjeta por moneda** (si hay ARS y USD, dos tarjetas apiladas verticalmente,
  ARS primero — mismo orden que ya usa `ordenarBloquesPesosPrimero`). Cada
  tarjeta: **franja izquierda de 170px**, fondo Mesa (`#F4F6F9`, normalizado
  de 0.), con la etiqueta de la moneda (11px gris dato, "EN PESOS"/"EN
  DÓLARES") arriba y el número grande (22px/700) del saldo neto abajo, con su
  color y su palabra de estado:
  - **Cliente**, saldo > 0: rojo (`#B91C1C`), palabra **"Te debe"** (el
    cliente le debe a la agencia).
  - **Operador**, saldo > 0 (la agencia le debe al operador): rojo, palabra
    **"Le debés"**.
  - Cualquiera, saldo < 0 (a favor): verde (`#047857`), palabra **"A favor"**.
  - Cualquiera, saldo = 0: gris dato, palabra **"Al día"**.
  A la derecha de la franja, el desglose: líneas 13px, etiqueta en gris dato a
  la izquierda / monto en tabular a la derecha (B.5 "importes a la derecha").
- Solapas: molde subrayado con ícono 16px + palabra, línea azul primary debajo
  de la activa (igual que hoy en ambas). **Se elimina el `animate-bounce` de
  la solapa activa del operador** (`SupplierAccountPage.jsx:1777`) — ese
  patrón no existe en ninguna otra pantalla del sistema y no está en B.3/B.5;
  queda como el resto: solo la línea subrayada indica cuál está activa.
- Extracto: tabla con columnas Fecha / Detalle (línea principal 14px/600 +
  línea secundaria 12px gris dato debajo, ej. "Reserva R-1042") / Debe / Haber
  / Saldo, cifras tabulares (B.2). Filtro por moneda arriba de la tabla, como
  pills: "Todas" / "Pesos" / "Dólares" (si solo hay una moneda con
  movimientos, no se muestra el filtro — no hay nada que filtrar).

### 2.1 Cuenta corriente del CLIENTE — específico

`src/TravelWeb/src/features/customers/pages/CustomerAccountPage.jsx`

- **Desglose de la foto de saldo** (fuente: `GET /customers/{id}/account`,
  `summary.balanceCompositionByCurrency[]`, campos `FacturadoSinCobrar`,
  `MultasAbiertas` (nota chica ámbar "(incluye $X en trámite)" si
  `MultasEnTramite > 0`), `CreditoAFavor` (nota chica "(incluye $X sin
  aplicar)" desde `summary.unappliedCreditByCurrency`) — **Multas abiertas
  solo se muestra si `MultasAbiertas > 0`**, no una fila en $0.
- **Acciones de cabecera**: "Usar saldo a favor" (secundaria/outline, gate
  `cobranzas.edit`, **solo aparece si hay crédito a favor en alguna moneda** —
  mismo criterio que ya usa `debeMostrarBotonUsarSaldo`) + "+ Nuevo
  presupuesto" (**primaria, cambia de outline a default** — hoy el botón es
  `variant="outline"`, lo que deja la pantalla sin ningún relleno; B.3 regla
  de oro #2 pide una principal rellena por pantalla, y esta es la acción que
  el vendedor vino a hacer al entrar a la cuenta de un cliente).
- **Solapas**: mismo contenido interno, se reordenan para que el default
  quede primero visualmente: **Estado de cuenta (default) · Reservas ·
  Facturación · Datos bancarios · Datos**. (Hoy el array las define
  `[reservas, estadoDeCuenta, facturacion, datosBancarios, datos]` — cambia
  SOLO el orden de la tira, ninguna solapa se agrega/saca/renombra.) **Se
  agregan íconos de 16px** a cada solapa (hoy no tienen — mismo set
  `lucide-react` que ya usa el operador: sugerido `CreditCard` para Estado de
  cuenta, `Briefcase`/`Layers` para Reservas, `FileText` para Facturación,
  `Landmark` para Datos bancarios, `Settings` para Datos — mismos íconos que
  ya usa la página del operador para conceptos equivalentes, para no
  inventar un segundo lenguaje de íconos entre las dos cuentas corrientes).
- **Lo que NO cambia**: el bloque "Saldo a favor aplicado" (aplicaciones
  vivas con botón Revertir) sigue exactamente donde está, debajo de la foto
  de saldo; el banner ámbar de "faltan datos fiscales" sigue igual; el
  contenido interno de las 5 solapas no se toca.

### 2.2 Cuenta corriente del OPERADOR — específico

`src/TravelWeb/src/features/suppliers/pages/SupplierAccountPage.jsx`

- **Desglose de la foto de saldo** (fuente: `GET /suppliers/{id}/account/statement`,
  `Currencies[]`, campos `ITheyOwe`, `TheyOweMe`, `Prepayment`; saldo neto de
  la franja = `EconomicClosingBalance`/`ClosingBalance`) con **wording nuevo,
  firmado hoy 18/08 (supersede el de la Fase D 2026-07-01)**:
  - `ITheyOwe` — antes "Le debo" → ahora **"Facturas por pagar"**.
  - `TheyOweMe` — antes "Me tiene que devolver" → ahora **"Te tiene que
    devolver"** (texto ámbar, `#B45309` — reemplaza el "naranja" custom que
    usa hoy `PALETA_RECUADRO`, que no es ninguno de los 9 colores de B.1).
  - `Prepayment` — antes "Saldo a favor" → ahora **"Saldo a favor tuyo"**.
  - **Sin permiso `cobranzas.see_cost`: la franja entera (número grande +
    las 3 líneas) va en gris con "—"** — mismo comportamiento de hoy
    (`AmountsVisible=false`), se conserva tal cual.
- **Acciones de cabecera**: "Usar saldo a favor" (outline) + "Registrar pago"
  (primaria/default), mismos gates de hoy (`tesoreria.supplier_payments`).
- **"Registrar reembolso recibido"**: vive DENTRO de la solapa "Cuenta
  corriente" como botón chico (`size="sm"`, outline) — confirmado: ya está
  ahí hoy (no es un componente nuevo, solo se revisa que quede con la piel
  nueva de B.3, mismos gates `caja.edit` + `tesoreria.supplier_payments`).
- **Solapas**: mismo contenido interno y MISMO orden de hoy: **Cuenta
  corriente (default) · Deuda por reserva · Servicios comprados · Facturas
  (solo `cobranzas.see_cost`) · Reembolsos (con globito de pendientes, solo
  `tesoreria.supplier_payments`) · Datos bancarios · Datos**. Ya tienen
  íconos 16px — se conservan tal cual, solo se saca el `animate-bounce`
  (2.0 más arriba).
- **Lo que NO cambia**: el banner ámbar de "faltan datos fiscales" sigue
  igual; el contenido interno de las 7 solapas no se toca; los gates de cada
  botón/solapa no se tocan.

### 2.3 Mobile (ambas cuentas corrientes)

Una columna: back-link → nombre → chips (wrap en varias líneas si no entra) →
acciones (stack vertical, ancho completo, principal primero) → tarjetas de
foto de saldo apiladas (ya lo son en desktop, en mobile igual) → solapas en
tira horizontal con scroll (mismo patrón que ya usa `scrollbar-hide
overflow-x-auto` en el operador hoy) → extracto con scroll horizontal de la
tabla (patrón ya usado en el resto de la app para tablas angostas en mobile).

```
┌───────────────────────────┐
│ ← Clientes                │
│ María Pérez                │
│ [RI] · maria@x.com · 11-.. │
│ [Usar saldo a favor]       │
│ [+ Nuevo presupuesto]      │
├───────────────────────────┤
│ EN PESOS                   │
│ $350.000                   │
│ Te debe                    │
│ ───────────────────────    │
│ Facturado sin cobrar $300k │
│ Multas abiertas       $50k │
│ Crédito a favor          0 │
├───────────────────────────┤
│ ◂ Estado de cta Reservas.. │
├───────────────────────────┤
│ [Todas][Pesos][Dólares]    │
│ (tabla con scroll horiz.)  │
└───────────────────────────┘
```

---

## 3. Tabla dato → tarjeta → endpoint → gate

### Dashboard

| Tarjeta | Campo(s) del backend | Endpoint | Gate |
|---|---|---|---|
| Salidas próximos 7 días | `ProximosViajes[]` (+ R4: Balance/Currency/PassengerCount) | `GET /reports/dashboard` | `reportes.view`; recortado a lo propio sin `reservas.view_all` |
| Cobros pendientes | `ReservasPendientes[]` | `GET /reports/dashboard` | ídem |
| Presupuestos abiertos + posibles clientes | `Presupuestos`, `ActivePotentialCustomers` | `GET /reports/dashboard` | `reportes.view` (no depende de `see_cost`/`view_all`) |
| KPI Por cobrar | `PorMoneda.SaldoPendiente[]` | `GET /reports/dashboard` | `reportes.view`; vacío sin `cobranzas.see_cost`... **ojo**: hoy `SaldoPendiente` NO se enmascara por `see_cost` en el backend (es deuda del cliente, no costo) — verificar con backend-dotnet-senior si corresponde mostrarlo a un vendedor sin `see_cost`; **default: sí se muestra** (es plata que hay que cobrar, no un costo del operador) |
| KPI Vendido del mes | `PorMoneda.VentasDelMes[]` | `GET /reports/dashboard` | `reportes.view` |
| KPI Cobrado del mes | `PorMoneda.CobrosDelMes[]` | `GET /reports/dashboard` | `reportes.view` |
| KPI Margen bruto | `PorMoneda.MargenBruto[]` | `GET /reports/dashboard` | vacío sin `cobranzas.see_cost` → tarjeta se oculta |
| Caja proyectada | `CashFlowDayDto[]` (post-R2, por moneda) | `GET /reports/cashflow?days=90` | post-R3: `cobranzas.see_cost` |
| Chip dólar BNA | `BnaUsdSellerRate` | `GET /reports/dashboard` | `reportes.view` |
| Ver informes | — (solo navega) | — | mientras sea Admin-only: `isAdmin`; si migra a permiso, `cobranzas.see_cost` |

### Cuenta corriente cliente

| Pieza | Campo(s) | Endpoint | Gate |
|---|---|---|---|
| Foto de saldo | `balanceCompositionByCurrency[]`, `unappliedCreditByCurrency[]` | `GET /customers/{id}/account` | vista siempre (es deuda de cliente, no costo) |
| Usar saldo a favor | `creditBalanceByCurrency[]` | acción propia | `cobranzas.edit` |
| Solapas (contenido) | sin cambios | sin cambios | sin cambios |

### Cuenta corriente operador

| Pieza | Campo(s) | Endpoint | Gate |
|---|---|---|---|
| Foto de saldo | `Currencies[].{ITheyOwe,TheyOweMe,Prepayment,EconomicClosingBalance}` | `GET /suppliers/{id}/account/statement` | `cobranzas.see_cost` (gris "—" sin él) |
| Registrar pago / Usar saldo | — | acciones propias | `tesoreria.supplier_payments` |
| Registrar reembolso recibido | — | acción propia | `caja.edit` + `tesoreria.supplier_payments` |
| Solapa Facturas | — | — | `cobranzas.see_cost` |
| Solapa Reembolsos | — | — | `tesoreria.supplier_payments` |

---

## 4. Preguntas para Gastón (multiple choice, una sola pregunta real)

Todo lo demás de esta spec se resolvió con default + cita de regla (sección 0
y a lo largo del documento). Queda UNA decisión que cambia qué número se
muestra, no solo la piel — no la puedo resolver con un default porque las dos
opciones tienen costo de construcción distinto:

**P1. La tarjeta "Caja proyectada — próximos 90 días" ¿qué línea querés ver?**

Hoy el sistema ya calcula una cosa (A) pero la maqueta usa palabras que
suenan a otra cosa distinta (B), que no existe todavía.

- **A) La tendencia de lo que YA entró y salió de caja, estirada hacia
  adelante** (lo que el motor ya calcula hoy: promedio de cobros y pagos
  reales de los últimos 30 días, proyectado 90 días para adelante). Se puede
  tener funcionando apenas se arregle que separe pesos de dólares (R2).
  Etiquetas de la maqueta: "Cobros (tendencia)" / "Pagos a operadores
  (tendencia)".
  ```
  Caja proyectada (tendencia de cobros y pagos)
  ┌────────────────────────────────────┐
  │      ╱‾‾╲___                       │
  │  ___╱     ╲___╱‾‾╲___  ← cobros    │
  │ ╱‾╲___          ╲___   ← pagos     │
  │Hoy   +30    +60    +90              │
  └────────────────────────────────────┘
  ```
- **B) El cronograma real de lo que falta cobrar y falta pagar, con sus
  fechas de vencimiento** (cuentas por cobrar de facturas ya emitidas +
  cuentas por pagar a operadores ya confirmadas, ordenadas por cuándo
  vencen). Es lo que las palabras "Por cobrar" / "Por pagar a operadores" de
  la maqueta prometen literalmente, pero **hay que construirlo de cero**
  (no existe ningún cálculo así hoy).
  ```
  Caja proyectada (lo que falta cobrar y pagar, por fecha)
  ┌────────────────────────────────────┐
  │           ╱‾‾‾‾‾‾╲                 │
  │      ╱‾‾‾╱        ╲___ ← por cobrar│
  │  ___╱          ___╱‾‾╲ ← por pagar │
  │Hoy   +30    +60    +90              │
  └────────────────────────────────────┘
  ```

**Mi recomendación: A.** Se puede tener ya, sin depender de una obra nueva de
backend, y para "cómo viene la agencia de plata en general" (que es el
espíritu de la tarjeta, al lado del resto del dashboard) alcanza igual de
bien. B queda anotado como mejora futura si con el tiempo A resulta
insuficiente.

---

## 5. Qué NO hacer (para frontend-senior y frontend-reviewer)

1. **No tocar el contenido interno de ninguna solapa** de las cuentas
   corrientes (Reservas, Facturación, Datos bancarios, Datos, Deuda por
   reserva, Servicios comprados, Facturas, Reembolsos). Esta obra es piel +
   jerarquía del contenedor (cabecera, foto de saldo, tira de solapas), no
   las pantallas de adentro.
2. **Jamás sumar pesos y dólares en un solo número**, en ningún lugar de
   las tres pantallas — ni en el saldo neto de la foto, ni en los KPIs, ni en
   la caja proyectada (P-3). Si un cálculo hoy lo hace (caso R2), no se pinta
   hasta que el backend lo separe — no se "arregla" sumando en el front.
3. **No inventar métricas ni campos que el backend no manda.** Si un dato de
   la maqueta no existe todavía (ej. pax/deuda en "Salidas próximas" antes de
   R4), la pieza se construye sin ese dato (ver 1.4), nunca con un número
   inventado o un placeholder tipo "0" que parezca real.
4. **Nada de ventanas emergentes (modals) nuevas.** Todo lo de esta spec es
   fichas en línea, navegación entre pantallas, o texto — ni un solo `Dialog`
   nuevo (P-5).
5. **No agregar entrada al Sidebar** para `/reports`/`/analytics` — el único
   acceso nuevo es el botón "Ver informes" del dashboard (default adoptado,
   sección 1.1).
6. **No recrear el `animate-bounce`** ni ningún otro efecto de movimiento
   nuevo en solapas, chips o botones — no está en B.3/B.5 y ya se marcó para
   sacar, no para imitar en el lado que no lo tenía.
7. **No mezclar el color ámbar de "Te tiene que devolver" con un naranja
   custom** — es el mismo `#B45309`/`#FFFBEB` que usa toda la app, no un
   tono nuevo "porque combina mejor" con nada.
8. **No cambiar el orden de las solapas del operador** (ya está bien, no se
   toca) — solo las del cliente se reordenan para que "Estado de cuenta"
   quede primero (2.1).
9. **El botón "+ Nuevo presupuesto" de la cuenta del cliente es el ÚNICO
   relleno de esa pantalla** — si "Usar saldo a favor" también apareciera
   relleno en algún estado, es un bug de la implementación, no algo a
   replicar (B.3 regla de oro #2).

---

## RESPUESTA DE GASTÓN (18/08 noche — FIRMADA, multiple choice)

**Pregunta de la sección 4 (Caja proyectada) = opción C "Las dos"**:
- AHORA: se usa la tendencia que ya existe (`/reports/cashflow`, arreglando
  R2 —separación por moneda— y R3 —permiso en vez de rol duro—), con el
  título honesto **"Ritmo de cobros y pagos"** (no promete cronograma).
- DESPUÉS: el cronograma real por vencimientos comprometidos queda anotado
  como obra futura (no bloquea esta implementación).

Con esto la spec queda completa y ejecutable sin más preguntas.
