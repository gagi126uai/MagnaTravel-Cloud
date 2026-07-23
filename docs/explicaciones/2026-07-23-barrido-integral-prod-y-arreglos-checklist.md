# 2026-07-23 — Arreglos del checklist + barrido integral de producción

Explicación nivel trainee de la sesión maratónica del 22/07 a la madrugada del 23/07.

## Parte 1 — Los arreglos que subieron (commits 08cb506d y 3be6bb0d)

Tras verificar el paquete de estándares con capturas, el dueño ordenó "arreglá todo lo que viste". Salieron, con reviews completas y CI verde:

1. **Modal de anular con candado**: ya no muestra el texto de nota de crédito cuando el motivo es otro (solo el mensaje real del motor).
2. **Chau validación nativa del navegador** en 4 formularios de plata (`noValidate`): el aviso de monto inválido ahora es el nuestro, en criollo — nunca más el tooltip en inglés de Chrome.
3. **Obra C1 del candado** (spec firmada): con la reserva confirmada y sin destrabe, los 9 botones de edición se ven grises con candadito y al tocarlos ofrecen destrabar. Exentos según spec (cobrar, facturar, anular reserva, agregar pasajero).
4. **Fechas SIEMPRE en hora argentina**: la causa raíz del "cobro del 22 mostrado como 21" era formateo inline que convertía a la zona del navegador; se centralizó todo en `formatDate`/`formatDateTime` (`src/TravelWeb/src/lib/utils.js`) con `timeZone: America/Argentina/Buenos_Aires` explícito, en 16 pantallas. Y el bug espejo cazado EN VIVO a las 21:50: los formularios proponían el día UTC (23 en vez de 22 de noche) — nuevo helper `hoyArgentina()` aplicado a 8 formularios de plata. Tests con reloj simulado en 3 husos.

**Regla nueva permanente**: la verificación final de cada tanda se hace en el navegador del dueño contra producción (él pone las credenciales, el asistente maneja y él mira).

## Parte 2 — El barrido integral (5 etapas + pruebas puntuales del dueño)

Con su sesión y en modo homologación (comprobantes sin validez legal, banner naranja verificado en cada pantalla), se recorrió el sistema entero como un usuario real: circuito del cliente, circuito del operador, anulaciones y NC, tipos de servicio y monedas, y la prueba integral de consistencia entre módulos.

**Resultado: 47 hallazgos numerados** (detalle completo con capturas en la memoria del proyecto y en `D:\Documentos\MagnaTravel\capturas-barrido-2026-07-22\`, 355 imágenes). Los más graves:

- **No se pueden cargar vuelos** (error 500 siempre).
- **Asistencias rotas de raíz**: cuentan la tarifa diaria como total (plata mal contada en cascada), su estado nunca se resuelve, y eso deja a cualquier reserva con asistencia **infacturable para siempre**; además, guardar la asistencia pisa las fechas de toda la reserva.
- **"Eliminar pago" no hace nada** (ni siquiera dispara el pedido al servidor).
- **Facturar una reserva sin cliente revienta** (500) — y con "Eliminar pago" roto, el circuito de reembolsos queda **inalcanzable** (confirma la duda del dueño sobre la exigencia de factura para anular).
- **La fecha de emisión de la factura sale en día UTC** (comprobante fiscal con fecha corrida de noche) y **el PDF de homologación no tiene leyenda** "sin validez".
- **Desconexiones**: "Cobranza y Facturación" pierde comprobantes que "Facturación" sí tiene; una reserva "Esperando reembolso" es invisible en el listado de Reservas; el buscador global no encuentra nada.
- **Sobrecobro mudo**: cobrar $99.999 sobre un saldo de $300 pasa sin aviso.

**Lo que reconcilia bien** (importante para calibrar): la plata del operador cierra centavo a centavo entre sus 4 pantallas, y el extracto del cliente cierra matemáticamente.

## Qué sigue

Plan de ataque de 5 tandas propuesto (en memoria: `plan-ataque-hallazgos-2026-07-23`), con asistencias y circuitos muertos en la Tanda 1. Se ejecuta con el OK del dueño, tanda por tanda, con specs citando la Constitución, revisores completos y verificación final en su navegador. Quedan además 2-3 decisiones de negocio que son solo suyas (sobrecobro, regla de factura-para-anular, campo de vencimiento de pasaporte).
