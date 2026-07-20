# 2026-07-19 — Tanda 1 del contrato pantalla-motor: construida, revisada y probada con la app de verdad

**Para quién es este doc**: para entender en criollo qué se hizo hoy, sin saber programar.

## El problema que ataca la Tanda 1

Cuando pagabas a un operador y algo estaba mal (moneda equivocada, reserva sin
servicios de ese operador, cargo ya pagado), la pantalla mostraba un cartel inútil:
"No se pudo registrar el pago al proveedor." El motor SABÍA el motivo exacto, pero
el mensajero lo tiraba a la basura. El vendedor quedaba sin ninguna pista.

## Qué se construyó (ayer) y qué se cerró hoy

1. **El mensajero ya no se traga el motivo**: los 7 mensajes reales del motor
   llegan al cartel rojo de la ficha, tal cual (decisión firmada: no se reescriben).
2. **Avisar ANTES cuando se puede**: el selector de servicio filtra por la moneda
   del pago, y si la reserva no tiene servicios de ese operador te lo dice antes
   de que aprietes Confirmar.
3. **"Nueva factura" escondido** para operadores que facturan directo al cliente
   (Intermediación): esa factura no existe para ellos, el botón no aparece.
4. **Tope real en "Usar saldo del operador"**: el monto no puede superar ni el
   saldo disponible ni la deuda de la reserva destino (antes te dejaba teclear
   de más y explotaba después).

## Lo que pasó hoy, en orden

1. **Tests de integración** nuevos (5): comprueban contra la API real que cada
   rechazo devuelve SU texto exacto, no el genérico.
2. **Tres revisores** (frontend, backend, y el estricto de fuga de datos técnicos).
   Los dos últimos BLOQUEARON por lo mismo: al propagar el mensaje tal cual,
   si algún día explota algo interno del framework, ese texto en inglés con
   nombres de código llegaría al vendedor. Además tres validaciones tenían un
   sufijo técnico `(Parameter 'request')` que ahora se vería.
3. **El arreglo** (sin volver al genérico): se creó una "familia" de errores de
   negocio del circuito de pagos a proveedor. El cartel muestra SOLO esos
   (todos en criollo, juzgados uno por uno); cualquier explosión interna cae en
   la red de seguridad global con su cartel amistoso. Re-review: los dos
   revisores APROBARON.
4. **E2E REAL con la app corriendo** (la regla de "verificado de verdad"):
   se levantó TODO local (base, API, pantalla) y un robot caminó la app como
   un usuario: se logueó, creó 2 operadores, 2 reservas con hoteles (uno en
   dólares, otro en pesos), y probó los flujos. **13 de 13 chequeos verdes**:
   - El caso trampa (pagar en pesos una reserva que debe en dólares) muestra
     el mensaje REAL del motor en el cartel rojo, la ficha queda intacta y se
     puede corregir y reintentar.
   - Con la moneda equivocada, el hotel no aparece en el selector y sale el
     aviso "no tiene servicios en esta reserva en la moneda del pago".
   - El operador Intermediación NO tiene botón "Nueva factura"; el normal sí.
   - El camino feliz (pago en dólares imputado al hotel) se registra bien.
   - Ningún cartel contiene texto técnico.
   Evidencia: capturas en `scripts/e2e-local/`.

## Yapas que dejó el día (hallazgos para otra tanda)

- **Las migraciones no pueden crear una base desde cero** (el renombre histórico
  de tablas se hizo a mano en la base y la cadena quedó incoherente). En el VPS
  nunca se notó. Anotado como deuda de infraestructura.
- **El circuito de cobros a cliente tiene el mismo agujero** que se cerró hoy en
  proveedores (cartel que puede propagar texto interno). Mismo arreglo, otro día.
- La llave `EnableCatalogFindOrCreate` apagada hace que la moneda del servicio
  SE IGNORE al crear (todo nace en pesos). En PROD está prendida; ojo con
  entornos nuevos.
- Menores del frontend anotados por el revisor (una rama de texto muerta, un
  test que duplica una fórmula en vez de importarla, el caso teórico de una
  reserva con más de 100 servicios del mismo operador).

## Qué falta (mañana)

Commit + push (CI corre los tests de integración) + verificar PROD + Tanda 2
(pedir autorización desde la ficha de reserva, spec ya firmada).
