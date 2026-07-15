# 2026-07-15 — Las cuentas corrientes dicen la verdad completa + T5 diseñada a prueba de balas

## Los dos pedidos de Gaston

1. Arrancar la tanda T5 (emisión de la nota de crédito de la anulación parcial).
2. "Poner en orden la cuenta corriente del cliente y la del operador: no aparecen las
   multas a cobrar al cliente ni lo que le tenemos que dar al operador."

## Cuentas corrientes — el diagnóstico (dos investigaciones, con evidencia)

**El ojo de Gaston estaba bien calibrado.** El dato existía y estaba bien calculado en
el sistema; lo que faltaba era que entrara a los números "a simple vista":

- **Cliente**: la cuenta del cliente filtraba AFUERA las reservas anuladas — y las multas
  solo existen en anuladas. Además la solapa "Reservas" tenía la pantalla lista pero el
  servidor nunca le mandaba el dato (hueco comentado en el propio código).
- **Operador**: el extracto de cada operador mostraba todo bien, pero el saldo del
  listado, el semáforo y el total global de "Cuentas por pagar" solo sumaban compras
  menos pagos — el cargo que el operador factura aparte quedaba afuera (limitación
  admitida por escrito en el código desde el 10/07: "si algún día se necesita, hay que
  sumarlo").

**El patrón de los ERP serios** (SAP, Dynamics, Odoo — con fuentes): lo devengado y el
documento fiscal van en columnas separadas, pero AMBOS entran al total que la persona
mira. Nunca un compromiso confirmado queda invisible porque el papel no salió.

## Lo construido y deployado (ambas obras verificadas en producción)

**Obra 1 — Multas en la cuenta del cliente** (`7ee17bf`): bloque "Multa pendiente de
cobro" arriba de la cuenta del cliente (decisiones de Gaston: la multa confirmada sin
comprobante YA es deuda visible y distinguida; el extracto no se toca; lo ven todos;
sin alertas nuevas). Recuadro por moneda: firme en rojo + "sin comprobante todavía" en
ámbar; lista pasiva con link a la ficha; nada si no hay multas. La solapa Reservas
ahora recibe el contexto. 4 gates verdes; los 3 ajustes del revisor de frontend se
aplicaron antes del push (el más importante: jamás mostrar un monto aproximado desde
el escalar viejo que "miente" en multimoneda).

**Obra 2 — Saldo del operador completo** (`c2fe5c1`): el saldo oficial ahora suma el
cargo facturado aparte (con eso el listado, el semáforo y el total global coinciden con
el extracto). De yapa, el análisis destapó y arregló un **doble conteo latente**: la
pieza que calcula el saldo a favor del operador leía el saldo persistido Y le volvía a
sumar el circuito — con el cambio habría contado el cargo dos veces, robándole crédito
al operador. Verificación contra producción ANTES del deploy: cero operadores cambiaban
de saldo (no hay cargos facturados-aparte vivos aún — rige hacia adelante); DESPUÉS:
columna presente, 3 filas en cero. 3 gates verdes.

## El tropiezo del día (lección operativa)

Dos constructores trabajando en paralelo sobre la misma copia del código se pisaron:
uno perdió toda su implementación (el diseño se salvó y se reaplicó en limpio después).
Regla nueva en memoria: **un solo constructor tocando código a la vez**; los análisis y
revisiones de solo lectura sí pueden convivir.

## T5 — diseñada a prueba de balas (y en construcción)

El diseño de la emisión de la NC parcial pasó por **tres rondas de desafío** del
arquitecto revisor que atraparon 5 problemas de diseño ANTES de escribir código:

1. El camino viejo de NC parcial marca la factura como anulada POR COMPLETO — reusarlo
   habría apagado el cobro y la facturación del resto de la factura.
2. Reusar la reconciliación existente habría transitado la anulación parcial como TOTAL
   y emitido una multa fantasma → reconciliador dedicado.
3. La fila hija debe nacer enganchada a su NC en la misma transacción (si no, el tope
   reserva el total completo de la factura).
4. El enganche del padre queda vacío a propósito (defensa por construcción contra el
   cosedor de huérfanos — la familia de bug del limbo del 14/07), y los 5 lectores de la
   pata de multa aprenden a mirar la hija.
5. Esos lectores deben esperar el CAE de ARCA, no solo la creación de la NC (si no,
   botones que rebotan).

Decisión de fondo (evidencia técnica y fiscal): **una NC por cada anulación parcial**
(no acumular por factura). Los temas fiscales ya estaban respondidos por investigación
(13/07); la emisión automática para Responsable Inscripto sigue bloqueada hasta firma
de matriculado (decisión vigente, no se reabrió).

**La pantalla** quedó firmada por Gaston (5 respuestas): aviso accionable ARRIBA de la
ficha (que sigue viva, con el servicio tachado), doble confirmación "¿Seguro?" (eligió
la doble a propósito), sin motivo nuevo, aviso de 15 días con el molde del Deshacer,
fila "DEVOLUCIÓN · SERVICIO CANCELADO" y "Dólar de la factura (no se cambia)".

Backend de T5 en construcción al cierre de este documento; sigue el frontend y la ronda
completa de gates.

Commits: `7ee17bf` (multas en cuenta del cliente) + `e0351cc` (docs diseño T5) +
`c2fe5c1` (saldo del operador). Diseño: docs/architecture/2026-07-15-t5-emision-nc-parcial-diseno.md.
Specs UX: docs/ux/2026-07-15-multas-en-cuenta-del-cliente.md y
docs/ux/2026-07-15-t5-pantalla-confirmar-nc-parcial.md.
