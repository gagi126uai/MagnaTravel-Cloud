# 2026-07-21 — Rediseño del pago al proveedor + arranque de la obra del circuito proveedor

**Para quién**: cualquiera que se sume y quiera entender qué pasó hoy sin haber estado.

## Lo que quedó EN PRODUCCIÓN hoy

1. **"Registrar pago al proveedor" rediseñado en 2 pasos** (aprobado por Gaston
   sobre el análisis de cómo lo hacen SAP/Odoo/Dynamics): primero elegís QUÉ
   pagás (grilla de deudas por reserva + botón explícito "Pago a cuenta"),
   después confirmás el monto ya precargado, y al final un cartel te dice
   exactamente a dónde impactó la plata ("Bajó la deuda de la reserva X en
   $N, quedan $M"). El extracto ahora nombra la reserva en cada pago.
   Verificado E2E real 12/12 con capturas. En el camino, los revisores
   frenaron dos ventanas reales de pago duplicado y el E2E cazó que la
   pantalla se recargaba entera y se tragaba el cartel — todo corregido.
2. **Saneo de datos legacy**: 27 servicios viejos sin moneda pasaron a "pesos"
   explícito (migración validada contra la base real antes y después: 27 → 0).
3. **Caracteres rotos** ("AÃ©reo"): 6 archivos reparados + un test guardián
   que barre todo el código en cada corrida — no puede volver a entrar.
4. **El misterio de la reserva 1051, resuelto**: no era bug del motor — el
   hotel estaba recién creado y sin confirmar cuando Gaston miró la cuenta
   corriente (sin confirmar = todavía no es deuda), pero "Nueva factura" sí lo
   listaba porque usaba otro criterio. Esa incoherencia se cerró hoy (abajo).

## La obra nueva: coherencia del circuito proveedor

De las pruebas a mano de Gaston salió el diagnóstico: la parte de proveedores
tenía guardias contradictorias y callejones. Se hizo el inventario completo
(`docs/architecture/2026-07-21-circuito-proveedor-inventario.md`), Gaston firmó
las 4 decisiones (regla única / aviso con confirmación para costo<pagado /
"Nueva factura" solo confirmados / orden de tandas), y arrancó la ejecución:

- **Tanda P1 (TERMINADA, en commit local, reviews verdes — se pushea mañana
  tras el E2E real)**: el freno de "bajar el estado" de un servicio pagado
  ahora es la misma regla que el candado bueno de anular (mismo motivo, mismo
  botón "Emitir factura", muerte de la jerga "degradar/des-confirmar");
  TODO el formato de plata del backend pasó a es-AR — incluido el cuerpo del
  PDF de la factura, que mezclaba formatos; "Nueva factura del operador" ya
  solo lista servicios confirmados, con un vacío que lo explica; y el aviso
  de bloqueo en la cuenta del operador trae un link que aterriza en la
  reserva con el panel de factura ABIERTO.
- **Tanda P2 (a medio)**: los endpoints para deshacer/reasociar un reembolso
  del operador **ya existían completos y testeados** — nadie les había puesto
  pantalla. Quedó limpio el único par de mensajes con jerga (commit wip local,
  sin reviews). Mañana: diseño UX de la vista de reembolsos con "Deshacer" y
  "Corregir reserva", implementación y reviews.
- **Tandas P3 y P4 (pendientes)**: aviso al bajar el costo por debajo de lo
  pagado (con el saldo a favor registrándose al instante), y los retoques
  (mostrar el "Eliminar" del cobro con recibo anulado que el motor ya permite,
  fusionar carteles apilados de anular reserva, cartel del candado para Admin).

## Estado exacto de los commits

- Pusheado y CI verde: rediseño pago (`365c42e5`), saneo (`c3c3d3eb`),
  mojibake (`08e9b856`), docs.
- **Local sin pushear**: inventario (`4306caa1`), P1 (`8381f09c`, reviews
  verdes, falta E2E), wip P2 backend (`f8f6ff2c`, sin reviews).

## Números al cierre

Backend unit 3941/3941 · frontend 2531/2531 · integración Postgres real local
verde en las suites tocadas · builds sin errores.
