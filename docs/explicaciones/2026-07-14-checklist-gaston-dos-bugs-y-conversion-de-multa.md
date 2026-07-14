# 2026-07-14 — El checklist de Gaston destapó dos errores reales (y los arreglamos en la misma noche)

## Qué pasó, contado simple

Gaston hizo su lista de pruebas pendientes y encontró dos cosas raras:

1. **Un cartel mentiroso**: la ficha de una reserva anulada decía "se está emitiendo la
   multa al cliente" durante días, aunque el comprobante ya había salido aprobado por ARCA.
2. **Un botón que no hacía nada**: en una anulación con la multa cargada en dólares sobre
   una factura en pesos, el botón "Corregir monto y moneda" aceptaba la corrección y el
   sistema la volvía a trabar con el mismo motivo — un círculo sin salida.

Además pidió resolver los 3 temas fiscales pendientes **por investigación** (decisión
vigente: no se gatea con contador; se investiga y se le informa).

## Bug 1 — el cartel pegado (F-2026-1045)

**La causa**: cuando ARCA aprueba la nota de débito de la multa, el sistema guardaba el
CAE en la factura pero **nadie actualizaba el estado de la anulación** (quedaba en
"en camino" para siempre). El único lugar que lo corregía era la pantalla
"Comprobantes por resolver" — al abrirla, de paso, reconciliaba. Por eso la multa de
F-2026-1045, aprobada el 10/07 a las 22:12, siguió mostrando "se está emitiendo"
**3 días**, hasta que Gaston abrió esa pantalla el 13/07 a las 23:32. El refresco
automático del cartel (agregado el 10/07) funcionaba bien… pero refrescaba un dato
que nunca cambiaba.

**El arreglo de raíz** (`9f8fefc`): en el momento exacto en que el trabajo que pide el
CAE termina (aprobado o rechazado), ahora actualiza también el estado de la anulación.
Regla compartida en un solo lugar (`CancellationDebitNoteReconciliation`), blindada para
que un fallo ahí jamás pierda el CAE, y la bandeja tolera la carrera "el trabajo me ganó
de mano" sin romperse. Gates: backend (aprobado con 2 mejoras aplicadas), seguridad
(0 bloqueantes), exposición (0 bloqueantes). Deployado verde.

## Bug 2 — el círculo sin salida (F-2026-1033)

**La causa**: un choque de fechas de construcción. El candado "la moneda de la multa debe
coincidir con la de la factura" es del 08/07 (y está BIEN que exista: evita emitir un
comprobante con el número en la escala equivocada — 200 dólares no son 200 pesos). La
maquinaria de conversión de moneda es del 10/07, pero corre DESPUÉS del candado y solo
para anulaciones con cargos tipificados. Resultado: multa en USD + factura en ARS =
revisión manual eterna, y la "salida" ofrecida era el mismo botón que volvía a chocar.

**El arreglo** (`0b37e16`, diseño desafiado dos veces por el arquitecto revisor):
**convertir al capturar**, con el candado intacto como última defensa.

- Al corregir una multa en dólares sobre factura en pesos, el modal ahora te guía:
  cargás el monto en dólares + **la fecha en que el operador cobró** + el tipo de cambio
  (viene pre-escrito con el dólar oficial del BNA de esa fecha si lo tenemos; lo podés
  pisar escribiendo encima, y ahí pasa a "a mano" con justificación). Ves en vivo
  "→ Se le cobra al cliente $ X" y guardás. Todo queda auditado (cuánto era en dólares,
  a qué cambio, de qué fecha, quién).
- Diseño de pantalla firmado por Gaston (2 respuestas: aviso suave si el dólar está >20%
  lejos del oficial, sin frenar; y el sugerido se pisa escribiendo encima).
- Caso especial (los servicios del operador están genuinamente en otra moneda que la
  factura): NO se convierte a ciegas — queda en manos de una persona, con mensaje claro.
  Resolverlo bien es una tanda futura ya anotada en el diseño.
- Guardas del lado del servidor: tipo de cambio en rango de cordura, fecha no futura,
  el convertido nunca puede superar el total de la factura (ahí frena y va a revisión).
- Pieza nueva: consulta del dólar BNA por fecha (funciona para fechas recientes;
  la serie histórica completa es el proyecto ADR-011 pendiente del norte multimoneda).

Gates: ronda completa de 4 revisores (backend, seguridad/datos, exposición de internos,
frontend contra la spec firmada) — **cero bloqueantes**; los endurecimientos sugeridos
se aplicaron antes de subir. Migración aditiva validada contra prod ANTES (0 columnas
existentes) y DESPUÉS del deploy (6 creadas, 0 filas tocadas). Unit backend 3505/3505,
frontend 2038/2038, integración real verde en CI. Deploy verde.

## Los 3 temas fiscales — resueltos por investigación

Informe completo con fuentes: `docs/fiscal/2026-07-13-investigacion-multas-3-temas.md`.

1. **IVA de la multa trasladada**: la multa del operador pasada tal cual NO lleva IVA
   (es indemnizatoria); el cargo de gestión propio de la agencia SÍ (21%). Para
   monotributo (factura C) no cambia nada visible. Riesgo residual anotado para cuando
   se venda a un Responsable Inscripto (conviene firma de matriculado antes de prender
   la emisión automática RI — el sistema ya la bloquea).
2. **Ajuste por el dólar**: es un resultado contable interno al momento del cobro; sin
   comprobante fiscal en el flujo normal. Coincide con lo construido.
3. **Corrección de comprobantes**: lo correcto es NC que anula + comprobante nuevo,
   asociados. El plazo de ARCA de 15 días corre **desde la anulación**, no desde la
   venta. Pendiente anotado: sumar el contador de plazo (verde/amarillo/rojo) a
   "Comprobantes por resolver".

## Del checklist de Gaston

- Punto 1 (recarga forzada por la seguridad nueva): ✅ la web anda.
- Punto 2 (F-2026-1044): ✅ números coherentes; anotado el detalle cosmético
  "Falta facturar US$-650" en gris (feo pero correcto).
- Punto 3 (paso de multa): destapó el Bug 1 → arreglado y deployado.
- Punto 4 (multa USD 200): la anulación 10 original ya estaba cerrada sin multa desde
  el 08/07; la prueba real la hizo sobre F-2026-1033 y destapó el Bug 2 → arreglado y
  deployado. **Pendiente de su mano**: destrabar F-2026-1033 con el modal nuevo.
- Punto 5 (temas de contador): resueltos por investigación, ver arriba.

## Qué queda para la próxima

1. **Gaston prueba el modal nuevo** en F-2026-1033: "Corregir monto y moneda" → USD 200 +
   fecha en que cobró el operador → confirmar la conversión → el comprobante debe salir
   en pesos y el cartel destrabarse solo.
2. **La tanda de emisión de la anulación parcial (T5)** sigue siendo la próxima obra
   grande, con sus bloqueantes duros documentados en el commit `32d9b23` y el ADR-044.
3. Anotados en el camino: contador de plazo de 15 días en "Comprobantes por resolver";
   serie histórica del dólar BNA (ADR-011); conversión también en el confirmar del día 0
   (hoy solo en corregir, decisión de alcance B2); caso multa en tercera moneda
   (tanda futura del diseño Fix B); "Falta facturar" negativo en anuladas (cosmético).
