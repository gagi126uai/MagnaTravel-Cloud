# 2026-07-24 — Día de ejecución del barrido: Tandas 3, 4 y 5 en producción

> Nivel: explicado en fácil, para leer sin saber programar.
> Contexto: el 22/23-07 se barrió TODO el sistema en producción y salieron 47
> hallazgos. Gaston aprobó atacarlos en 5 tandas en un día. Las tandas 1 y 2 ya
> estaban en producción al empezar el día; hoy salieron la 3, la 4 y la 5.

## Qué salió a producción hoy

### Tanda 3 — Pestaña Anuladas + "Volver atrás" que deshace la anulación entera

**El problema que había:** si anulabas una reserva y después te arrepentías,
"Volver atrás" solo cambiaba el cartelito de estado: los servicios quedaban
anulados (huérfanos), el saldo a favor del cliente quedaba dando vueltas y el
registro de la devolución del operador quedaba vivo. Una promesa a medias.

**Lo que hace ahora (decisión firmada de Gaston):** "Volver atrás" en una
reserva anulada **deshace la anulación entera**:

- Los servicios anulados en ese acto **reviven** al estado que tenían antes.
  Los que habías anulado uno por uno ANTES de anular todo, no reviven (así lo
  decidió Gaston).
- Si el cliente tenía saldo a favor por esa anulación y **no lo usó**, se
  retira con un contra-asiento tachado: nada se borra, queda el rastro de
  quién, cuándo y por qué (regla F-6 de la constitución).
- El registro de la devolución del operador se aborta, también con rastro.
- Antes de hacer todo eso, la pantalla te lo explica y te pide confirmación
  (regla P-14).

**Los frenos:** si el saldo a favor **ya se usó** en otra reserva, o si ya se
emitió la **nota de débito de la multa**, el sistema bloquea el deshacer con un
cartel (no un avisito que desaparece): "Ese saldo a favor ya se usó en otra
reserva..." / "Ya se emitió la nota de débito de la multa...". Técnica: el
motor manda una marca estable (`UNDO_ANNULMENT_BLOCKED`) y la pantalla muestra
cartel siempre que venga esa marca — se dejó de adivinar por el largo del
texto, que era frágil (regla T-6).

**El detalle que salvó la migración:** el SQL que marca la fecha de anulación
de las reservas ya anuladas estaba escrito con el nombre de columna del código
(`ReservaId`), pero la base real usa `TravelFileId`. Se corrigió antes del
deploy y se verificó contra la base de producción: 27 reservas anuladas
recibieron su fecha, cero conflictos.

**Además en la Tanda 3:** pestaña "Anuladas" con contador propio y botón
"Todas" real · badge de saldo de clientes con 3 estados (Deuda / A favor / Al
día) sin mezclar monedas · al elegir un servicio en el pago al operador el
monto se completa solo · botones "Marcar confirmado"/"Marcar emitido" con
casillero de N° de confirmación, iguales en la ficha de la reserva y en la
cuenta del operador (el editor viejo quedó detrás de "Corregir a mano") ·
buscador honesto ("Mostrando solo lo tuyo" cuando corresponde) · aviso cuando
las fechas de la reserva no coinciden con las de los servicios.

### Tanda 4 — Sobrecobro con aviso, editar pago sin perder la imputación, formatos

- **Sobrecobro (decisión firmada: aviso, nunca tope):** cobrar o pagar de más
  ya no pasa mudo ni se bloquea: el sistema avisa cuánto queda a favor y pide
  confirmación. Se eliminó un tope duro que contradecía la decisión.
- **Editar un pago a proveedor ya no pierde la imputación:** antes, editar
  cualquier cosa de un pago lo dejaba "a cuenta" sin avisar. Ahora la
  reserva/servicio imputados vienen precargados, incluso si la lista todavía
  está cargando (se siembra el dato del propio pago para que ninguna carrera
  de red lo pierda).
- **Costo Neto en formato argentino** con la moneda real del dato (adiós
  `1,234.56` y el "USD" inventado en Tarifas).
- **Aviso temprano de condición fiscal:** si el operador de un servicio no
  tiene la condición fiscal cargada, el modal de cancelar te avisa ANTES
  ("Falta la condición fiscal del operador. Cargala en su ficha antes de
  cancelar, así la nota de crédito no se traba después."). El bloqueo tardío
  queda como red.
- **"Crear como nuevo" preserva lo tipeado:** solo limpia los campos que
  vinieron sugeridos del producto.

### Tanda 5 — Pulido, fugas selladas y filtros reales

- **Banda "SIN VALIDEZ FISCAL — COMPROBANTE DE HOMOLOGACIÓN"** en los PDFs
  mientras la facturación esté en ambiente de pruebas de ARCA.
- **Fugas técnicas selladas:** el error crudo de ARCA que se mostraba como
  "Motivo del rechazo" · nombres internos de la base en mensajes de error ·
  etiquetas en inglés ("Transfer" → "Traslado") · tokens de método de pago
  crudos en el historial (ahora "Otro medio" como último recurso).
- **Historial de la reserva en criollo:** los cobros nuevos ahora dicen
  "Cobro registrado: $150.000,00 — Transferencia" leyendo los datos reales del
  motor (antes caía en "Modificaciones en campos técnicos.").
- **Los filtros "Pagadas" y "Con deuda vencida" de Cobranza eran fantasma**
  (mostraban lo mismo que "Activas"): ahora filtran de verdad.
- **La página en blanco de /operators** era en realidad "cualquier dirección
  inexistente queda en blanco": ahora hay una pantalla "No encontramos esta
  pantalla" con botón al inicio (pendiente de la firma de Gaston, ver P6).
- Detalles: "Hotel Hotel Prueba" (no se duplica más el prefijo) · paréntesis
  vacíos "()" en asistencias · el avisito "Escribí la ruta o aerolínea." que
  quedaba pegado · horas en formato 24 en los 5 lugares que faltaban ·
  "Falta facturar" negativo ahora explica "Facturaste $X de más".

## Cómo se trabajó (proceso)

Cada tanda pasó por: implementación (agentes en paralelo motor/pantallas en
zonas separadas) → 4 revisores (motor, pantallas, seguridad/plata, fugas
técnicas) → fixes de los bloqueos → re-verificación de los revisores que
bloquearon → commit separado motor/pantallas → CI (tests unitarios + tests de
integración contra Postgres) → deploy → verificación contra la base de
producción cuando hubo migración. Los revisores bloquearon 4 veces en el día y
las 4 tuvieron razón: cartel vs avisito (T-6), backfill faltante de la fecha
de anulación, el resumen del historial que nunca disparaba con datos reales, y
el token "Other" crudo.

## Hallazgos nuevos que quedaron anotados (obras futuras, NO hechas hoy)

1. **El historial pierde el detalle de TODAS las altas** ("Modificaciones en
   campos técnicos."): el mecanismo de auditoría guarda las altas en un
   formato que el lector no entiende. Obra aparte, toca un mecanismo
   compartido.
2. **Traslado ida y vuelta NO genera 2 tramos** (verificado #30): no está
   implementado en ningún lado — modelo, motor y pantalla lo tratan como un
   solo servicio. Es obra nueva si el negocio la quiere.
3. **Antes de pasar la facturación a producción real:** persistir el ambiente
   (homologación/producción) en cada comprobante, para que los PDFs viejos de
   homologación no pierdan la banda.
4. **RatesPage tiene otros montos crudos** fuera del formulario de edición
   (filas de la tabla) — mismo bug #26, seguimiento.
5. **"Deuda vencida" multimoneda:** el filtro nuevo usa el saldo escalar; en
   casos multimoneda puede diferir del chip de la ficha.
6. **Performance de la ficha:** el aviso de condición fiscal hace que el
   preflight de cancelación corra también con factura viva (~6 lecturas por
   apertura de ficha). Batch, no N+1, pero es candidato a memoización.
7. **Backfill de la fecha de anulación:** una reserva cancelada puramente
   uno-por-uno (sin acto de anular) pudo recibir fecha de anulación por
   aproximación — revisar las 27 filas si aparece un deshacer raro.
8. **Aviso de condición fiscal solo cubre al operador** — si la agencia o el
   cliente tienen condición desconocida, el bloqueo tardío sigue sin aviso
   temprano (límite de alcance documentado).

## Preguntas pendientes de Gaston (P1..P6)

- **P1** Alta de cliente: cómo quedan los dos casilleros de documento
  (recomendado: uno solo con desplegable de tipo, como pasajero).
- **P2/P3** Vencimiento de pasaporte: ¿siempre visible o solo con tipo
  "Pasaporte"? ¿en Datos personales o en Identidad? (el motor ya lo tiene
  todo; falta solo el casillero).
- **P4** Cobrar sobre una reserva 100% saldada: ¿aviso con confirmación
  (recomendado) o se mantiene bloqueado?
- **P5** Firma de la regla T-14 "hora argentina siempre".
- **P6** Pantalla "No encontramos esta pantalla": ¿texto y destino OK?

## Qué falta del plan del día (Cierre)

- Pruebas de Gaston en su navegador (tandas 3, 4 y 5 — listas entregadas).
- Completar la condición fiscal del operador de prueba 84222 y terminar el
  circuito de NC en homologación (necesita la app con credenciales).
- Limpieza de datos de prueba decidida (factura C 0001-00000054 con NC,
  cliente 149b0743, operador 84222, reservas F-2026-1053..1063, saldos
  artificiales) — por la app, con Gaston.
- Checklist de 5 minutos post-deploy (regla de la constitución).
