# 2026-08-04 — Tanda 1 del rediseño de Reservas: el listado nuevo

## Qué se hizo, en criollo

La pantalla de Reservas (el listado) ahora obedece las decisiones que Gaston firmó el 03/08.
Ejemplo cotidiano: antes el listado te decía "Vendido: $ 5.300.000" mezclando una venta de
$ 2.000.000 y otra de US$ 3.300 como si fueran la misma plata — como sumar kilos con litros.
Ahora dice "Vendido: $ 2.000.000 · US$ 3.300", cada moneda por su lado, siempre.

## Los cinco cambios que se VEN

1. **Tres números arriba (P2)**: Activas · Por cobrar · Vendido, en una tira fina. Pesos y
   dólares separados con "·", nunca sumados. Murió el número mezclado y también el KPI de
   rentabilidad del listado (la rentabilidad vive en la ficha).
2. **Solapas apagadas (P3)**: una solapa sin reservas (ej. "En viaje 0") se ve gris y no se
   puede clickear, pero no desaparece — así se aprende dónde va a aparecer cada cosa. "Todas"
   nunca se apaga.
3. **Destino bajo el número (P4)**: cada fila muestra a dónde es el viaje ("Cancún · Riviera
   Maya"), sacado de los servicios cargados. Si la reserva no tiene servicios con destino, no
   se muestra nada — no se inventa.
4. **Archivar con motivo (P5)**: la única acción de la fila es el botón "Archivar" con la
   palabra escrita (no un iconito misterioso). Si no se puede archivar, el botón queda apagado
   y ABAJO dice por qué, con el mismo motivo que daría el motor.
5. **Buscador global**: escribís un número de reserva o un cliente y busca en TODAS las
   reservas, sin importar el mes ni la solapa en la que estés. Mientras buscás, aparece el
   aviso "Buscando en todas las reservas, sin filtro de mes" y al borrar el texto todo vuelve
   solo a como estaba. De yapa se arregló el botón "siguiente" de la paginación, que estaba
   siempre apagado por un bug viejo.

## Qué se hizo por abajo (backend)

- El resumen del listado dejó de viajar como 4 números sueltos que mezclaban monedas. Ahora
  viaja una lista de líneas {moneda, monto}, calculada desde la MISMA tabla que ya usa el
  desglose de cada fila (una sola regla, todas las pantallas la obedecen).
- "Por cobrar" se evalúa por fila de moneda: una reserva puede deber en pesos y a la vez tener
  saldo a favor en dólares — cada moneda se mira sola, jamás se compensan.
- El destino se busca en 4 consultas por página (vuelos, hoteles, paquetes, tarifario), no una
  consulta por reserva.
- El buscador global es un aviso EXPLÍCITO al motor (`globalSearch=true`). ¿Por qué? Porque la
  pantalla de Cobranzas por reserva usa el mismo mostrador del API y ella SÍ quiere que el mes
  y la pestaña sigan filtrando. Sin el aviso explícito, esa pantalla habría perdido sus filtros
  en silencio.

## Cómo se verificó

- Tests: backend 4.735 en verde (15 nuevos de esta tanda) · front 3.110 en verde (39 nuevos).
- Reviewers: backend APROBADO · frontend APROBADO · gate de exposición de datos PASA (el cartel
  de error nuevo jamás muestra el error técnico, siempre un mensaje amable en español).
- CI verde y deploy al VPS hecho (commit `9d8a057a`).
- **Falta**: el humo visual de Gaston en su navegador contra PROD (regla de siempre).

## Seguimientos anotados (no bloquean)

- El cálculo del resumen trae a memoria las reservas viejas sin desglose de moneda (sin
  paginar); si algún día hay muchísimas, conviene sumar en la base.
- Los tests del front de esta tanda copian lógica a mano en vez de importarla del componente
  (mismo patrón que ya dio un falso verde el 03/08) — a mejorar.
- Falta un test que asegure que un error 500 del listado pinta el cartel amable.
