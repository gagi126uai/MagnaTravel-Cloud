# 2026-08-19 (madrugada) — Dashboard ERP + cuentas corrientes unificadas

> Nivel: trainee. Continuación de la sesión del 18/08 (que ya había cerrado la
> obra post-rollout, la campanita, el chip "Anulada" y el rediseño de
> Configuración). Todo elegido por Gastón en Claude Design con multiple choice.

## Qué se firmó

1. **Dashboard "Inicio" = Opción C "Panorama ERP"**: el trabajo a la
   izquierda, la plata a la derecha. Un solo dashboard para todos, que se
   adapta por permisos — se terminó el "dashboard de admin" y el "dashboard
   de vendedor" como pantallas separadas.
2. **Cuentas corrientes de cliente y operador con el MISMO molde** (hoy eran
   dos pantallas que no se parecían entre sí).
3. **Caja proyectada = tendencia por ahora** ("Ritmo de cobros y pagos");
   el cronograma real por vencimientos quedó anotado como obra futura.

## Tanda 1a — backend (`c7419950`)

- La curva de caja de 90 días **mezclaba pesos con dólares** (sumaba todo
  junto). Ahora viene desglosada por moneda; los campos viejos quedan por
  compatibilidad pero ningún consumidor nuevo debe leerlos.
- El endpoint era "solo rol Admin" a lo bruto; pasó al sistema de permisos:
  con `reportes.view` ves los cobros; sin `cobranzas.see_cost` los pagos a
  operadores van VACÍOS y **todo lo derivado (saldos acumulados,
  proyecciones) se calcula con el dato ya enmascarado** — si no, cualquiera
  podía deducir los costos restando números. Sin `reservas.view_all`, la
  serie se acota a tu propia cartera.
- "Salidas próximas" ahora trae cuántos pasajeros van y el saldo pendiente
  por moneda (lista vacía = saldada), reusando el mismo cálculo de deuda de
  siempre.

## Tanda 1b — el dashboard nuevo (`57681723`)

- **Murieron `AdminDashboard` y `AgentDashboard`**: hay UNA pantalla, y cada
  pieza se muestra según el permiso real (sin permiso la pieza NO aparece —
  jamás un "$0" mentiroso).
- Columna trabajo: salidas de los próximos 7 días con chip "Debe US$ X" /
  "Saldada", cobros pendientes con botón "Cobrar", presupuestos abiertos y
  posibles clientes.
- Columna plata: 4 números por moneda (por cobrar / vendido / cobrado /
  margen — cada moneda en su renglón, jamás sumadas), el "Ritmo de cobros y
  pagos" a 90 días, y "Ver informes" (solo dueño) que **resucita las páginas
  de informes que existían sin link en el menú** (vendedores, destinos, año
  contra año).

## Tanda 2 — cuenta corriente del cliente (`98bcfc8c`)

- Cabecera al molde: "← Clientes", nombre grande, chip de condición fiscal
  (dato que ya existía, ahora a la vista) + email · teléfono · CUIT/DNI.
- **Foto de saldo por moneda**: el saldo grande con su palabra ("Te debe"
  rojo / "A favor" verde / "Al día") y al lado el desglose (facturado sin
  cobrar, multas solo si hay, crédito con lo no aplicado).
- "+ Nuevo presupuesto" pasó a ser el único botón azul de la pantalla;
  "Usar saldo a favor" subió a la cabecera (mismo flujo de siempre).
- Extracto con filtro por moneda en pills y cifras alineadas.

## Tanda 3 — cuenta corriente del operador (`60322309`)

- Mismo molde que la del cliente (antes eran primos lejanos). La palabra del
  saldo tiene la **semántica inversa** — saldo positivo = "Le debés" — y el
  review la verificó contra el dominio real, no de palabra.
- Desglose: Facturas por pagar / Te tiene que devolver (ámbar) / Saldo a
  favor tuyo. Sin permiso de costos, la tarjeta entera va gris "—" (igual
  que antes, testeado que no se filtra el número por ningún lado).
- Chau `animate-bounce` (el iconito que rebotaba en la solapa activa, un
  patrón que no existía en ninguna otra pantalla).
- Las 7 solapas y sus flujos de pago/reembolso quedaron intactos por dentro.

## Deuda anotada (no urgente)

- Cronograma real de caja por vencimientos (obra futura firmada).
- Unificar `FotoDeSaldoCuenta` (cliente) y `FotoDeSaldoOperador` en una base
  compartida si se vuelve a tocar cualquiera de las dos.
- Skeleton de carga del dashboard con el layout viejo.
- AnalyticsPage sigue leyendo los campos legacy del cashflow (mezcla
  monedas) — actualizarla cuando se le dé entrada de menú formal.
- Ícono del botón "Nuevo presupuesto" difiere entre dashboard (FileText) y
  Reservas (Plus).

## Commits de esta madrugada

- `5863669c` spec firmada · `c7419950` tanda 1a backend · `57681723`
  tanda 1b dashboard · `98bcfc8c` tanda 2 cliente · `60322309` tanda 3
  operador. Canvas de maquetas:
  https://claude.ai/code/artifact/670357ad-68f5-4bf6-b496-006079714048
