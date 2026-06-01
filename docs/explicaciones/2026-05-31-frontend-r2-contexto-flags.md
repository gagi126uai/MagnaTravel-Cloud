# Frontend R2: sacar el "parpadeo" de la pantalla (2026-05-31)

> Para Gastón, explicado fácil. Esta tarea es un detalle de pantalla del rediseño de estados de la reserva. No toca plata, ni estados, ni nada del backend. Solo arregla un saltito visual.

## El problema en una frase

Cuando prendas la llave de los estados nuevos (`EnableSoldToSettleStates`), al abrir la pantalla de reservas se veía por un instante el **modo viejo** (botón "Confirmar Reserva", 6 pestañas) y enseguida **saltaba al modo nuevo** (botón "Vender", 8 pestañas). Ese saltito es el "parpadeo".

## Por qué pasaba (en criollo)

Pensá que cada pedacito de la pantalla, al cargar, le preguntaba al sistema *"¿la llave está prendida?"*. Esa pregunta tarda un toque (hay que ir al servidor). Mientras llegaba la respuesta, la pantalla **asumía que estaba apagada** (lo más seguro) y dibujaba el modo viejo. Apenas llegaba la respuesta diciendo "está prendida", **rehacía** todo al modo nuevo. De ahí el salto.

Peor todavía: **cada** pedacito preguntaba **por su cuenta** (la lista de reservas preguntaba, el detalle preguntaba, etc.), o sea varias preguntas repetidas al servidor por lo mismo.

## Qué hicimos

Dos cosas:

1. **Una sola pregunta para toda la app.** Ahora apenas entrás (ya logueado) el sistema pregunta **una vez** "¿qué llaves están prendidas?" y guarda la respuesta en un lugar común. Todos los pedacitos de pantalla leen de ahí. Si navegás entre páginas, ya no vuelve a preguntar: la respuesta ya está.

2. **No dibujar el modo viejo mientras no sabemos.** Mientras llega la respuesta, en lugar de mostrar el modo viejo y después saltar, mostramos un **placeholder gris** (unas barritas que "respiran") en el lugar de los botones y las pestañas. Apenas llega la respuesta, aparece directamente el modo correcto. Sin salto.

## ¿Y con la llave apagada (como está hoy)?

El resultado final es **idéntico a hoy**: mismas pestañas, mismo botón "Confirmar Reserva". La única diferencia es que, **la primera vez** que entrás a reservas después de loguearte, podés llegar a ver el placeholder gris por unos milisegundos antes de que aparezcan los botones. Es una vez por sesión, no en cada página. El revisor lo evaluó y lo dio por bueno: es el precio correcto de no adivinar y no saltar.

## Por qué no se puede "mostrar el modo viejo de una"

Porque la pantalla **no sabe** si la llave va a terminar prendida o apagada hasta que el servidor le contesta. Si mostrara el modo viejo "de una", volveríamos a tener el salto cuando la llave esté prendida. El placeholder gris es la forma de esperar sin comprometerse.

## Qué tocamos (técnico, por si hace falta)

- **Nuevo**: `contexts/OperationalFlagsContext.jsx` — el "lugar común" que pregunta una vez y comparte la respuesta. Va dentro de la zona logueada (la pregunta necesita usuario logueado).
- **Borrado**: `hooks/useOperationalFlags.js` — el viejo que preguntaba por cada pedacito.
- **Tocados**: `App.jsx` (monta el lugar común), `ReservasPage.jsx` y `ReservaHeader.jsx` (muestran el placeholder mientras carga).
- Revisado por el revisor de frontend (aprobado) + ajustes menores aplicados (aviso en desarrollo si algo se monta mal, placeholder atado a la cantidad real de pestañas, marca para que los tests automáticos puedan esperar la carga). Build verde.

## Estado

Committeado y pusheado (HEAD `3b3d6d5`). Es el punto 3 de los pendientes para prender la llave. Quedan los otros dos, que dependen de vos:
1. Firma del contador (ya te dejé el Word con las 2 preguntas).
2. Correr los tests en el VPS.
