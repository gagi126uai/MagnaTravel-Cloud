# 2026-07-14 (parte 3) — El "Deshacer" estaba muerto al nacer (y lo destrabamos) + la pantalla ahora explica por qué la multa va en la moneda de la factura

## Qué pasó, contado simple

Gaston probó lo pendiente y trajo dos observaciones:

1. En la **1043**, el "Deshacer" de la multa emitida le mostró un cartel rojo: "La factura
   original ya está anulada por completo. Este caso lo tiene que revisar una persona."
2. En la **1025**, el sistema le decía que no se podía hacer la multa en dólares (servicios
   en dólares, factura y notas de crédito en pesos) y lo leyó como una incoherencia.

## Hallazgo 1 — la regla que mató al "Deshacer" (bug real, arreglado y deployado)

La obra de anoche traía una regla de seguridad (la "regla dura #11"): si la factura original
ya estaba anulada por completo, no se podía deshacer automáticamente. El detalle fatal: **en
toda anulación normal la factura SIEMPRE queda anulada por completo** (la nota de crédito
total sale al anular), y las multas de operador solo existen en anuladas. Conclusión: la
regla bloqueaba el **100% de los casos reales** — el botón nuevo no servía para nada.

¿Por qué nadie lo vio? Las pruebas automáticas armaban la factura "a mano" sin pasar por el
circuito completo de anular, así que la factura de los tests nunca estaba anulada. El caso
real de producción (F-2026-1043, factura en dólares, multa US$ 649,98 emitida) lo destapó a
la primera prueba de Gaston.

**El arreglo** (`b68a477`): se eliminó esa regla. Es fiscalmente seguro porque el comprobante
que deshace apunta **a la multa** (no a la factura), hereda su moneda y tipo de cambio
congelados, y la pieza que evita tocar los cobros de la factura de venta quedó intacta. Las
demás protecciones siguen: no deshacer dos veces, solo con comprobante aprobado, impuestos
asociados a revisión manual, multi-operador ambiguo a revisión manual.

- La prueba automática se invirtió: ahora el caso "factura anulada del todo" debe PERMITIR
  el deshacer, y quedó un test que arma el escenario real para que esto no regrese.
- Ronda de 3 revisores: funcional aprobado, seguridad/plata aprobado (el revisor intentó
  construir un camino de doble crédito y no pudo), exposición de internos aprobado.
- CI verde completo (incluidas las pruebas con Postgres real) y deploy verificado en el VPS.

## Hallazgo 2 — la 1025 no es un bug, pero destapó dos cosas

**Datos reales**: la factura de la 1025 se emitió **en pesos por $950** (época sin
multimoneda), con servicios en dólares; la multa quedó confirmada por "200" sin moneda
registrada (dato viejo).

1. **La regla es correcta pero era invisible**: factura, notas de crédito y multa hablan
   la moneda de LA FACTURA (ADR-012 §3.3). Por eso en la 1025 la multa va en pesos. Cuando
   la factura sale en dólares (como la 1043), la multa sale en dólares — todas las
   posibilidades ya están soportadas, la moneda la manda la factura, no los servicios.
2. **La trampa del tope**: USD 200 convertidos (~$300.000) superan el total de la factura
   ($950), y el tope de seguridad la manda a revisión manual siempre. Para la 1025 las
   salidas reales son cerrar sin multa o cargar la multa en pesos (≤ $950). Gaston
   respondió: "es de prueba, pero contemplar que esto puede pasar con datos reales" →
   quedó anotada la obra futura **"multa mayor que el total de la factura"** (tiene una
   pregunta fiscal adentro: si el comprobante de la multa puede superar a la factura).

**El arreglo de la confusión** (`0de1561`): dos líneas explicativas nuevas, diseñadas por el
gate de UX y aprobadas por Gaston (P0: excepción puntual a su regla anti-cartelitos; P1:
en ambos lugares; P2: completa + mínima; P3: solo cuando la moneda elegida difiere):

- En "Corregir monto y moneda", arriba del bloque de conversión: *"La factura de esta
  reserva salió en pesos. Todo lo que se le cobra o se le devuelve al cliente va en esa
  moneda, incluida la multa — aunque el operador la haya cobrado en dólares."* (espejo
  automático si la factura está en dólares).
- Al confirmar la multa, bajo el selector de moneda y solo si elegís una moneda distinta a
  la de la factura: *"La factura de esta reserva salió en pesos: el cargo al cliente va en
  pesos."*
- De paso se arregló que al panel de confirmar no le llegaba la moneda de la factura
  (sin eso la línea nueva no podía calcularse).
- Guía de UX actualizada con la excepción autorizada y fechada.

Ronda de revisores: frontend aprobado (fiel a la spec) y exposición aprobado. 133/133
pruebas del frontend verdes.

## Anotados que dejó la sesión (no bloquean)

- **Multa mayor que el total de la factura** (pedido de Gaston): obra propia con pregunta
  fiscal, en memoria.
- **Multimoneda y la línea nueva** (observación del revisor): en una reserva con facturas
  en dos monedas, la línea del confirmar puede aparecer al abrir el panel (no recién al
  cambiar la moneda) y el texto habla de "la factura" en singular. No es peligroso (la
  moneda del comprobante la fuerza el servidor); si molesta, se decide otro texto/gatillo
  con Gaston.
- Mejora de accesibilidad opcional: avisar al lector de pantalla cuando la línea cambia.
- El motivo guardado de la revisión manual de la 1025 quedó desactualizado ("penalidad no
  confirmada", de antes de que Gaston la confirmara) — se refresca solo al corregir.

## Cómo se investigó (regla de tokens)

Búsquedas puntuales en el código + 2 consultas de solo lectura a producción (ops-diagnostico)
para confirmar con datos reales antes de tocar nada. Implementación con modelo intermedio;
los modelos potentes solo para revisar y diseñar.

Commits: `b68a477` (fix Deshacer) + `0de1561` (líneas explicativas + guía UX).
