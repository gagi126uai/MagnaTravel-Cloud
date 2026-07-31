# 2026-07-30 — La pantalla nueva de Copias de seguridad y el misterio de la "versión más nueva"

*Explicación en criollo de lo que pasó hoy, para leer sin saber programar.*

## El problema con el que arrancó el día

Gastón fue a hacer la verificación pendiente de ayer (restaurar la copia del 27/07) y el
sistema le tiró un cartel absurdo: **"Ese resguardo es de una versión MÁS NUEVA del sistema"**.
Imposible: esa copia era de tres días atrás. Encima no podía restaurar NINGUNA copia, todas
rechazadas con el mismo cuento. Y de paso: la pantalla le pareció (con razón) un desastre —
ventana sobre ventana, texto amontonado, feedback confuso.

## El misterio, resuelto

El chequeo de versiones compara la "libreta de cambios" que viaja adentro de cada copia contra
la lista de cambios que conoce el sistema actual. Si la copia trae un cambio que el sistema no
reconoce, concluía: "esto es del futuro, no lo toco".

Lo que nadie sabía: en la base de producción quedaron **dos anotaciones fantasma de febrero**
(un cambio que se rehízo a los 19 minutos y cuya anotación vieja nunca se borró). Como todas
las copias salen de esa base, **todas** traían esas anotaciones → el sistema no las reconocía →
"es del futuro" → bloqueado todo. Una fila basura de hace cinco meses tenía secuestrado el
botón de restaurar.

**El arreglo**: ahora el chequeo mira la fecha de cada anotación desconocida. ¿Es más vieja que
el último cambio del sistema? Entonces es una anotación huérfana inofensiva: se tolera, y queda
registrado en la auditoría cuántas se toleraron (solo el número). ¿Es más nueva? Se sigue
rechazando, porque ése sí es el peligro real. ¿No se puede leer la fecha? No se toca nada y se
avisa con honestidad.

## La pantalla nueva (diseño firmado por Gastón: 12 preguntas, 12 respuestas)

Se terminó el modal. "Volver atrás" y "Empezar de cero" ahora viven en una **solapa propia
"Copias de seguridad"** dentro de Administración, todo en la página:

- **Tabla de copias**: fecha con "hace cuánto", columna nueva **"Por qué se guardó"** ("Antes
  de empezar de cero" / "Antes de volver a una copia"), tamaño, y un botón "Usar esta" por fila.
- **Ficha en línea**: al tocar "Usar esta" se abre debajo de esa misma fila el aviso de versión,
  la frase, la contraseña y el motivo. Un botón grande ("Volver a esta copia") y dos chicos.
- **La única ventana** de todo el flujo es el "¿Seguro?" de siempre.
- **Espera con pasos**: mientras restaura, se ve en qué paso va (primero trae los datos a una
  base aparte, después guarda la copia del estado actual, al final se pone al día) — ese orden
  es el real del motor, elegido a propósito para no gastar tiempo de mantenimiento si el
  archivo estaba dañado.
- **Éxito**: cartel verde en la página con la fecha a la que volvió el sistema.
- **Rechazo**: cartel rojo con el motivo tal cual, y al cerrarlo una marca roja queda pegada a
  la copia que falló, con "Ver el motivo".
- **"Empezar de cero" plegado**: bloque compacto de tres líneas con un botón; el formulario
  completo recién aparece al tocarlo (retoque pedido por Gastón al verlo desplegado).

## La prueba, hecha de verdad en producción

Claude manejó el navegador (con el login de Gastón) e hizo el circuito completo: vio la tabla,
abrió la ficha del 27/07, corrió "Ver qué contiene" (59 reservas, 16 clientes, 97 facturas,
contadas en una base de prueba sin tocar nada), probó una contraseña equivocada (cartel claro,
nada se pierde), y ejecutó **"Volver a esta copia" con éxito**: cartel verde, copia previa
primera en la lista, y la verificación de fondo — **Caja con $5.492.890 de resultado del mes y
los 16 clientes con sus saldos reales**. La auditoría registró motivo, copia previa, AFIP en
homologación y las 2 anotaciones huérfanas toleradas. La verificación §7 pendiente desde ayer
quedó CERRADA.

Detalle esperado: al navegar después de restaurar, la sesión se corta (las sesiones también
vuelven a la foto) — el cartel verde se alcanza a leer antes, pero hay que loguearse de nuevo.

## Reviews del día

Backend (2 veces), riesgo de datos (bloqueó por auditoría → corregido → levantado), exposición
de datos (2 veces), frontend (bloqueó por 3 avisos perdidos en la mudanza + vocabulario →
tanda única de 12 fixes → levantado). Commits: `e7bfd9de` (fix versión), `3e16224f` (solapa),
`fa095417` (retoques).

## Deudas anotadas

- Tests de render para los componentes nuevos de la solapa (hoy solo hay tests de lógica) y
  cobertura E2E automatizada del flujo (candidato: qa-automation-senior).
- Los identificadores internos de fila usan el nombre del archivo (solo visible en devtools).
- El cartel emergente compartido no atrapa el foco del teclado (deuda vieja de toda la app).
- Pantalla de espera para el usuario que cae durante un mantenimiento que no es restauración:
  hoy ve los pasos en gris; el motivo real que manda el motor queda sin usar.
