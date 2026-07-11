# 2026-07-11 — Anulación parcial de servicios (T5): la ronda de controles y la subida

> Nivel trainee. Qué pasó hoy con la última tanda de la obra de multas (ADR-044).

## Qué es la anulación parcial

Hasta ahora, si una reserva tenía factura, no se podía anular UN solo servicio: era todo
o nada. Con T5, el pasajero puede dar de baja la excursión y quedarse con el hotel.
El sistema resuelve a qué factura corresponde el crédito y cuánto, sin pasarse nunca
del total de la factura, y lo deja **visible como pendiente** en la bandeja
"Comprobantes por resolver". La nota de crédito real todavía NO se emite: eso llega
con la pantalla de confirmación de la próxima tanda (necesita datos que solo el
usuario puede confirmar, como el tipo de cambio).

## Cómo se trabajó (el método completo, una vez más)

1. La construcción venía de la sesión anterior en un commit local SIN subir.
2. Se pasó por **6 controles en paralelo**: backend, riesgo de plata, fiscal/contable,
   exposición de internos, calidad de tests, y el arquitecto decidiendo los huecos
   documentados.
3. Resultado de la primera ronda: **5 bloqueantes reales** que ninguna suite de tests
   había atrapado:
   - "Cancelo un servicio y después anulo el resto" heredaba la carpeta a medias:
     los servicios vivos quedaban sin su línea → cliente acreditado DE MENOS y
     se perdía el registro de lo que el operador debía devolver.
   - La reserva del cupo de crédito se hacía con un registro fantasma que nunca se
     liberaba y confundía a otros procesos.
   - El guardado no era todo-o-nada: una falla a mitad de camino dejaba el servicio
     cancelado sin rastro del crédito.
   - El tope comparaba dólares contra pesos sin convertir.
   - El pendiente quedaba MUDO (sin superficie visible), justo lo que el diseño
     aprobado prohibía.
4. **Decisión de Gaston**: "arreglar todo ahora" (rechazó diferir el camino
   con-factura). El arquitecto convirtió todos los hallazgos en un plan único de
   8 frentes; un solo implementador lo ejecutó.
5. **Re-reviews: los 5 controles aprobaron.** El de calidad exigió además que las
   pruebas de choques simultáneos verifiquen el resultado EXACTO y corran 25
   repeticiones (para que no puedan dar "verde de mentira").
6. La migración de índices se validó contra producción ANTES de subir (solo lectura):
   índices con el filtro viejo presentes, 0 duplicados, y 13 anulaciones cerradas
   que quedan destrabadas por este cambio.
7. Commit único limpio + push → CI con compuerta de integración real + deploy.

## Números finales

- Pruebas unitarias completas: **3571/3571**.
- Pruebas de integración contra base real: **212/212** (concurrencia x25 repeticiones).
- Bloqueantes cazados por los controles antes de producción en esta tanda: **5 + varios mayores**.

## Un hallazgo importante que quedó anotado (no era lo que parecía)

El implementador había documentado el miedo de que el camino viejo de "anular el resto"
pudiera acreditar DOS VECES la misma factura. El arquitecto demostró en el código que
**hoy no puede pasar**: cada factura tiene un candado (una sola anulación en curso o
terminada por factura). Lo que sí destapó es la pregunta para la tanda que viene:
ese candado permite UNA sola nota de crédito por factura para siempre. Si se cancela
un servicio hoy y otro de la misma factura el mes que viene, la segunda se bloquea
(bloquea, no acredita de más — dirección segura). Decidir si el negocio necesita
varias notas de crédito por factura es condición dura de la tanda de emisión.

## Qué falta (bloqueantes duros de la TANDA DE EMISIÓN, ya documentados)

1. Pantalla de confirmación que emite la nota de crédito real (gate UX con Gaston).
2. Tope del camino viejo independiente de la llave + anti-doble-conteo por línea
   al crear la nota real.
3. Prueba de regresión del candado por factura.
4. Firma del contador: alícuota de IVA para Responsable Inscripto al trasladar la
   multa, y el plazo de 15 días de la RG 4540 para el "pendiente de emisión".
5. Decisión fiscal: ¿una o varias notas de crédito por factura?

## Menores anotados (no urgentes)

- El campo de estado del detalle de la anulación viaja con el nombre técnico (nadie
  lo muestra en pantalla hoy; convertirlo a etiqueta como se hizo con la bandeja).
- Rama muerta en `comprobantesPorResolverLogic.js:32` (compara contra un texto que
  el backend ya no manda).
- Barrido futuro de mensajes defensivos "no debería pasar" con jerga interna.
