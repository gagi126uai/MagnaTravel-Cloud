# 2026-07-07 — El aviso del chequeo nocturno ahora dice QUÉ reservas revisar

## El problema (reportado por Gastón)

El chequeo nocturno (el "vigía de coherencia") le avisaba al dueño:

> "El chequeo nocturno encontró 3 reservas anuladas con datos para revisar: 3 con una
> deuda sin comprobante que la justifique. Revisalas cuando puedas."

El aviso decía **cuántas** reservas tenían problema, pero no **cuáles**. El dueño
quedaba obligado a buscarlas a mano entre todas las anuladas — o sea, el aviso no
servía para actuar.

¿Por qué estaba así? Por una regla sana llevada demasiado lejos: el detalle de cada
hallazgo lleva el id interno de la reserva (un número de base de datos que el usuario
jamás debe ver), así que el detalle iba SOLO al log del servidor y el aviso quedaba
en puro conteo. La solución no era mostrar el id interno, sino mostrar el **número de
reserva de negocio** (`NumeroReserva`, ej. "F-2026-1025"), que es el que el usuario ya
ve en toda la app.

## Qué se cambió

Ahora el aviso lista los números de reserva por categoría:

> "El chequeo nocturno encontró 3 reservas anuladas con datos para revisar: 3 con una
> deuda sin comprobante que la justifique (F-2026-1010, F-2026-1012 y F-2026-1020).
> Revisalas cuando puedas."

Y con las dos categorías a la vez:

> "...: 2 con servicios sin cancelar (F-2026-1001 y F-2026-1002) y 3 con una deuda sin
> comprobante que la justifique (F-2026-1010, F-2026-1012 y F-2026-1020). ..."

Detalles de diseño:

- **Tope de 10 números por categoría**: si hay más, termina en "... y N más". Un aviso
  es un aviso, no un listado infinito.
- **`CoherenceFinding` ganó un campo `DisplayReference`**: el número de negocio de la
  entidad, documentado como *lo único mostrable al usuario*. El id interno (`EntityId`)
  sigue con su promesa de siempre: solo para el log del servidor.
- Solo los checks que REPORTAN a una persona (W2 "anulada con servicios vivos" y W5
  "anulada con deuda sin Nota de Débito") cargan ese campo. Los que auto-reparan
  (W1/W3/W4) no van al aviso, quedan como estaban.
- **Orden estable por número de reserva** en las consultas de W2 y W5. Motivo fino: la
  deduplicación de avisos compara el texto completo del mensaje; si la base devolviera
  las mismas reservas en otro orden, el sistema creería que la situación cambió y
  apagaría/recrearía el aviso todas las noches sin que nada haya cambiado.
- Defensa por datos legacy: si una reserva viniera sin número cargado, no se lista
  (pero sí se cuenta) — nunca se muestra el id interno como reemplazo.

## Verificación y gates

- Suite unitaria backend completa: **3208/3208 verde** (filtro del CI).
- 5 tests nuevos en `CoherenceWatchdogTests`: números en el mensaje, no-fuga del id
  interno, tope de 10 + "y N más", dos categorías con formato exacto, singular.
- **backend-dotnet-reviewer: APROBADO** con un cambio menor (el orden estable, ya
  aplicado antes de commitear).
- **data-exposure-reviewer: APROBADO** — rastreó los 5 constructores de findings y
  todo el camino hasta la campanita: al mensaje solo llegan castellano de negocio y
  `NumeroReserva`; ids internos, estados crudos y claves internas no se serializan
  al navegador.

Commit: `8ca5193` — CI verde completo y deployado al VPS.

## Qué va a ver Gastón

El aviso viejo (sin números) sigue en la campanita hasta la próxima corrida del vigía
(~3 AM Argentina). Esa corrida arma el mensaje nuevo, que es distinto → el aviso viejo
se apaga solo y nace el nuevo **con los números de las reservas**. No hay botón de
"correr ahora" en pantalla (el disparo manual es un endpoint de admin), así que la vía
natural es esperar la corrida nocturna.

## Pendiente relacionado (no de esta tanda)

- Que el aviso además tenga link directo a cada reserva (hoy es texto). Puede entrar
  con las pantallas UX ya especificadas (pestaña Anuladas + buscador global).
