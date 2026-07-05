# 2026-07-05 — Saneamiento integral: qué se arregló y qué falta (explicado en fácil)

## Por qué hicimos esto

Gaston venía pisando incoherencias sueltas sin buscarlas: reservas anuladas que
mostraban deuda, otras que decían "sin movimientos" sin razón, carteles apilados,
avisos que no se apagaban nunca. Pidió tres cosas: (1) un escaneo completo del
sistema, (2) arreglar TODO lo encontrado, (3) reforzar el núcleo para que esta
clase de problemas los encuentre el sistema y no él. Recién después seguimos con
el norte (dólar del BNA + facturas en dólares).

Se corrió una auditoría con 4 revisores en paralelo (plata en anuladas, máquina
de estados, frontend, notificaciones): 26 hallazgos. Gaston aprobó un plan de 7
tandas. En esta sesión entraron las primeras 4; la 5 quedó lista esperando
revisión (está en el working tree, sin commitear).

## El misterio resuelto: por qué unas anuladas mostraban deuda y otras no

Las anulaciones hechas ANTES del 24 de junio quedaron con la plata congelada:
en esa época, anular no daba de baja los servicios, así que la "venta" seguía
viva y la deuda nunca bajaba. Las anuladas nuevas sí quedan en cero. Encima, el
cartelito rojo "Vencida con deuda" no miraba el estado de la reserva, así que se
lo ponía a esas anuladas viejas. Mismo estado, distinta pantalla, según la fecha
en que se anuló: eso era lo que "no cerraba".

## Qué entró (4 commits, ya deployados)

1. **`3cbdbbc` — Una sola regla para "¿debe plata?"** La regla ahora vive en un
   solo lugar del núcleo y mira el estado: una anulada nunca más puede aparecer
   como "Vencida con deuda". Además cada anulada informa su contexto real de
   plata: saldo a favor pendiente, multa por cobrar, o "inconsistente" (dato
   roto que revisa el vigía).

2. **`8a80470` — Todas las transiciones de estado pasan por un punto único.**
   Antes había 16 lugares que cambiaban el estado a mano y solo uno limpiaba la
   etiqueta "con cambios"; por eso quedaba pegada e reaparecía al reabrir. Ahora
   hay una tabla declarativa (estado destino → qué se limpia) y un transicionador
   único que la aplica siempre. Bonus: ya no se puede emitir un comprobante en $0.

3. **`96d4499` — Reparación de las anuladas viejas.** Una migración con BACKUP
   automático adentro da de baja los servicios vivos de las anuladas legacy
   (el rastro visible dice "Ajuste del sistema", sin jerga). La plata NO se
   recalcula con SQL a mano: la recalculan los mismos calculadores del flujo
   real (sin inventar plata, sin duplicar saldo a favor si ya hubo NC).

4. **`9b997e4` — El vigía nocturno.** Un chequeo que corre todas las noches
   (3am AR) y busca combinaciones imposibles: reservas terminales con etiquetas
   pegadas (las repara solo), proyecciones de plata desactualizadas (las repara
   con el calculador canónico y deja rastro viejo→nuevo en el log), anuladas con
   servicios vivos o con deuda sin comprobante (esas las REPORTA con un aviso,
   porque la plata la decide una persona). La primera corrida nocturna termina
   sola la reparación de las anuladas viejas. Esto es el "que deje de pasar":
   lo que antes encontraba Gaston a los golpes, ahora lo encuentra el vigía.

## Qué quedó a mitad (para la próxima sesión)

**Tanda 5 — avisos que se apagan solos** (implementada, falta revisión): cuando
la causa de un aviso se resuelve (pagaste, diste el OK, la anulación salió al
reintentar), el aviso muere solo; "visto" en un lugar = visto en todos; los
avisos de AFIP llegan en el momento (sin recargar la página); y los avisos
diarios ya no se acumulan uno por noche.

Después: **Tanda 6** (los números del frontend salen todos del mismo cálculo:
"Recaudado" vs "Cobrado", cuenta del cliente en la moneda real, la factura en $0
explica por qué) y **Tanda 7** (ordenar la "mamushka" de carteles según el
diseño que Gaston ya aprobó).

## Proceso

Cada tanda pasó por tres revisores obligatorios (backend, seguridad de datos,
exposición de internos). El de exposición encontró un bloqueante real en la
Tanda 3: el rastro de la reparación iba a mostrar jerga técnica en la ficha del
servicio — se corrigió antes de deployar. Los tests unitarios quedaron en
3156/3156 al cierre de la Tanda 4.
