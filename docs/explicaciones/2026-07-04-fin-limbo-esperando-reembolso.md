# 2026-07-04 — Fin del limbo "esperando reembolso" (y alertas que mentían en anuladas)

> Nivel: trainee. Sin jerga. Qué estaba roto, por qué, y qué se hizo.

## El problema que trajo Gastón (dogfood del 03/07)

1. La reserva **#F-2026-1025** estaba anulada, sin ningún movimiento de plata,
   y sin embargo quedó **"esperando reembolso del operador" para siempre**.
2. En la ficha de reservas **anuladas** seguía apareciendo el cartel de
   "confirmada con cambios" con el botón **"Dar OK"** — daba a entender que
   había que "confirmar una anulación", cosa que no existe.
3. Sensación general de estados descoordinados entre la parte de reservas y la
   parte de operadores. Pedido: buscar TODOS los bugs de esta familia.

## Por qué pasaba (las causas de verdad)

### El limbo
Cuando anulás una reserva, el sistema deja una pregunta abierta:
**"¿el operador cobra multa?"**. Hasta que un humano no entrara a responderla
(confirmar multa o "cerrar sin multa"), el cierre automático quedaba bloqueado
— aunque nunca se le hubiera pagado un peso al operador y no hubiera nada que
esperar. Nadie avisaba con fuerza que esa pregunta estaba pendiente. Además,
aunque la respondieras, el cierre recién pasaba **a la madrugada** (barrido de
las 4am): hacías lo correcto y seguías viendo el estado viejo todo el día.

### La alerta mentirosa
La marca interna "tiene cambios de precio sin revisar" **nunca se borraba al
anular**. La campanita global filtraba bien, pero el cartel y la etiqueta de la
ficha miraban solo la marca, sin fijarse en el estado. Darle "OK" no rompía
plata, pero dejaba registrado "el dueño revisó los cambios" sobre un viaje
anulado — basura para la auditoría.

## Qué se decidió (Gastón, 2026-07-04)

- **"Cerrar igual, multa como tarea"**: la anulación sin plata al operador se
  cierra sola apenas la AFIP confirma la nota de crédito, aunque la multa siga
  sin decidir. La pregunta de la multa queda como tarea pendiente **visible en
  la ficha de la reserva ya anulada** ("Anulada — falta decidir la multa del
  operador", con los botones ahí mismo). Si después aparece una multa, la nota
  de débito al cliente sale igual (todo el circuito de la multa funciona con la
  anulación ya cerrada).
- **Arreglar los 5 bugs nuevos** que encontró la auditoría interna (abajo).

## Qué se construyó

1. **Al anular se descarta la marca "confirmada con cambios"** en todos los
   caminos de la anulación + migración que repara las reservas viejas que ya
   quedaron marcadas + la pantalla solo muestra el cartel en reservas vivas.
2. **La multa sin decidir ya no bloquea el cierre** (solo bloquea una nota de
   débito a medio emitir). La ficha de la anulada muestra el paso de la multa.
3. **Cierre inmediato al resolver la multa** (sin esperar a la madrugada).
4. **Reembolso tardío sobre anulación cerrada**: si el operador devolvió una
   parte, la anulación se cerró, y meses después manda el resto, ahora hay
   botón para reabrir y registrar esa plata (antes era imposible).
5. **Red de seguridad para el aviso de AFIP perdido**: si la AFIP aprueba la
   nota de crédito pero el aviso interno muere, una tarea cada 30 minutos lo
   detecta y destraba la anulación sola (antes quedaba invisible para siempre).
6. **Anulaciones muy viejas sin detalle interno**: el barrido las cierra solo
   si de verdad no esperaban ni recibieron nada (con doble chequeo de
   imputaciones reales, pedido del review de seguridad).
7. **Barrido nocturno con horario propio** (5am) además del existente, para
   que un fallo en la tarea de vencimientos no salte la noche de barrido.
8. **Campo interno duplicado del estado del reembolso** ahora nace coherente y
   quedó documentada su semántica real.

## Cómo se verificó

- Suites: backend 3230/3230, integración con base real 208/208, front
  1708/1708 + build. Los tests de integración viejos simulaban "el operador
  devuelve plata que nunca le pagaste" — se corrigieron los escenarios para
  pagarle primero (más fieles al negocio) y se agregó una prueba nueva del
  auto-cierre de punta a punta.
- Reviews: funcional backend, seguridad/datos, exposición de internos y
  frontend — los cuatro aprobados; las dos condiciones se aplicaron (carrera
  benigna barrido-vs-cierre tragada; guarda de imputaciones activas en legacy).
- Gate UX: aprobado sin preguntas (excepción documentada en la guía: la ficha
  anulada sigue siendo solo-lectura salvo el paso de la multa).
- **App real en navegador**: login + creación de reserva + ficha abierta con
  Playwright, cero errores de página (lección TDZ aplicada).

## Qué le pasa a la #F-2026-1025 con esto

Con el deploy: si su multa estaba sin decidir, **se cierra sola esa misma
noche** (barrido de las 4/5am). Si lo que tiene es una nota de débito a medio
emitir, va a estar en la bandeja de NDs pendientes: un toque en "Reintentar" y
se cierra **en el momento**.

## Pendientes anotados (no bloquean)

- **Lista de trabajo "multas sin decidir"**: hoy la multa pendiente de una
  anulación cerrada se ve solo en la ficha de esa reserva; no hay una bandeja
  que las junte. Recomendado para una próxima tanda (pasa por el gate UX).
- La red de seguridad de AFIP avisa por log del servidor, no por campanita
  (para avisar "una sola vez" haría falta un contador persistido — anotado).
- **Arranque desde base vacía roto** (preexistente): la cadena de migraciones
  histórica no puede crear una base de cero (la primera migración referencia
  una tabla que se crea después). Al servidor no lo afecta (su base ya existe);
  documentado porque complica levantar entornos locales nuevos.
- ESLint `no-use-before-define` en TravelWeb sigue pendiente (prevención TDZ).
