# 2026-07-14 (parte 2) — "Deshacer una multa ya emitida": el camino del error, construido y deployado

## Por qué existe

Gaston preguntó: "¿y si hubo un error? deberíamos contemplar ese camino". Tenía razón:
un comprobante de multa aprobado por ARCA no se puede editar, y el sistema no tenía
NINGUNA salida — la anulación automática solo cubre facturas. Decidió construirlo ya,
antes de la tanda de emisión.

## Qué hace (en criollo)

En la ficha de una anulada con la multa ya cobrada al cliente, un administrador ve el
link "Deshacer: el operador cobró mal esta multa". Al confirmarlo (con motivo
obligatorio), el sistema emite **el comprobante que deja sin efecto la multa** (una
nota de crédito asociada a la nota de débito, espejo exacto: mismo importe, misma
moneda, mismo tipo de cambio congelado). La plata del cliente se acomoda sola: si no
había pagado, la deuda desaparece; si había pagado, le queda saldo a favor de verdad
(reutilizable). El paso de la multa vuelve a quedar abierto: corregir y volver a
cobrar, o cerrar sin multa. Todo queda auditado y con rastro visible en la ficha.
Aviso de plazo de ARCA (15 días): informa, no bloquea; pasado el plazo el tono sube
(respuesta de Gaston).

## Cómo se construyó (el circuito completo en una noche)

1. **Especificación fiscal** (15 reglas duras; la clave: la NC anula el comprobante
   ENTERO y apunta A LA ND, no a la factura).
2. **Diseño del arquitecto** + **desafío** (3 bloqueantes reales: el saldo a favor
   prometido no se acuñaba de verdad; el reset multi-operador podía borrar multas
   ajenas; el reconciliador necesitaba el mismo candado que el resto) + **revisión 2**
   + re-review "aprobado para construir" con una condición: el número que se acuña lo
   decide un TEST contra el código real, no una fórmula asumida.
3. **Gate UX con Gaston**: 2 preguntas → 1 la respondió la regla fiscal (comprobante
   entero), 1 la respondió él (tono fuerte pasado el plazo).
4. **Implementación** (backend + frontend con el modelo intermedio, regla de tokens).
5. **Ronda de 4 gates**: exposición aprobado; backend y seguridad aprobados con
   cambios; frontend RECHAZADO por un agujero real (el botón Reintentar dejaba pasar
   a no-administradores). Todo corregido + 2 re-reviews confirmadas.

## Los dos hallazgos que valieron la ronda

- **El crédito fantasma**: la primera fórmula del saldo a favor habría acuñado el
  TOTAL de la multa como crédito en el caso más común (anulada con saldo en cero) —
  plata regalada. Lo destapó la exigencia del gate de seguridad de probar el invariante
  contra la maquinaria real. La regla nueva es pura y segura: no se acuña nada salvo
  cobro parcial real. Hallazgo documentado con test: hoy NO existe camino para cobrar
  una multa de una anulada, así que en la práctica el acuñado es 0.
- **El candado de administrador**: la regla firmada "solo administradores" estaba en la
  puerta principal pero no en el "Reintentar", y el servidor tampoco la exigía. Cerrado
  en las dos capas con una sola función compartida.

## El gate de CI también trabajó

El primer push quedó FRENADO por el CI: una prueba de integración contra Postgres real
falló (el test usaba un usuario inventado que viola una referencia que la base en
memoria no controla). Producción nunca se enteró. Fix del test + "Include Error Detail"
en el entorno de pruebas para no diagnosticar a ciegas nunca más. Segundo push: verde
completo y deploy exitoso. Migración validada post-deploy (tabla nueva presente, 0
filas, columna del crédito creada).

## Anotados (no bloquean)

- Tramo residual: cancelación PARCIAL con deuda ajena en la misma moneda podría
  sobre-acuñar — escopar cuando exista un camino real de cobro de multa; número del
  saldo a favor a confirmar con matriculado antes de vender a un RI.
- Timestamp propio del CAE de la NC para el rastro (hoy usa la fecha del pedido).
- El edge "el admin se borra en la ventana async" (compartido con los puentes
  existentes, mismo patrón del repo).

Commits: `3c050a8` (obra completa, 38 archivos) + `7b59d01` (fix del test de CI).
Diseño: docs/architecture/2026-07-14-deshacer-multa-emitida-diseno.md.
Spec UX: docs/ux/2026-07-14-deshacer-multa-emitida.md.
