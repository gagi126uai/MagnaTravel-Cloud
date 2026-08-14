# 2026-08-14 — Obra "PDF completo" deployada: vuelo con horarios + cuotas de hotel

## Qué se construyó (commit `fb6aec2b`)

La obra que faltaba para que el PDF de presupuesto quede calcado al ejemplo
BAYAHIBE en los dos huecos que quedaban:

1. **Horarios del vuelo**: en la ficha de Aéreo, adentro de "+ Más detalles",
   apareció el bloque **"Horarios del vuelo"** con 4 casilleros de hora
   (Sale ida / Llega ida / Sale vuelta / Llega vuelta). Los de vuelta quedan
   apagados si no hay fecha de vuelta. En el PDF, el vuelo se dibuja con la
   hora grande y el aeropuerto abajo, más la duración calculada (ej.
   `07:30 EZE · BUENOS AIRES → 11:45 AEP · IGUAZU · 4h 15m`). Si hay vuelta,
   salen DOS filas (dos tramos), como en el ejemplo.
2. **Plan de cuotas del hotel**: en la ficha de Hotel, adentro de
   "+ Más detalles", los casilleros **Cuotas** y **Valor por cuota**. Es un
   dato puramente informativo para el PDF (`6 CUOTAS 280 USD` debajo de la
   tarifa) — NO participa de la Venta total ni de ningún saldo.

## Las tres decisiones técnicas que importan

### 1. La ventana del viaje y los horarios del papel son datos DISTINTOS

`FlightSegment.DepartureTime`/`ArrivalTime` quedaron como **ventana de
fechas** del viaje (alimentan las fechas calculadas de ADR-053) y NUNCA más
se leen como horario. Los horarios del PDF viajan SOLO por los 4 campos
nuevos (`OutboundDepartureTime`/`OutboundArrivalTime`/`ReturnDepartureTime`/
`ReturnArrivalTime`, todos `TimeOnly?`). Esto mató el bug latente que había
cazado el gate UX: el form viejo mandaba la fecha de VUELTA adentro del
campo de hora de llegada, y apenas se cargara una hora real iban a aparecer
"+1" y duraciones de 100+ horas.

### 2. Vaciar un casillero BORRA de verdad (el bloqueante del review)

La primera versión reusó el patrón anti-clobber (null = "conservá lo que
había"). El review de frontend cazó la consecuencia: un plan de cuotas o un
horario que el vendedor borraba… reaparecía en el PDF del cliente (dato
fantasma). Como la ficha inline es hoy el ÚNICO emisor del PUT de
vuelo/hotel y manda SIEMPRE los 6 campos, la respuesta correcta era mapeo
directo por convención (null = borrar), igual que Origen/Destino/Estrellas.
El test `PdfCompletoFieldsAntiClobberTests` (que blindaba el comportamiento
equivocado) se reemplazó por `PdfCompletoFieldsClearRoundTripTests`:
cargar → vaciar → queda NULL persistido.

**Moraleja trainee**: antes de reusar un patrón defensivo (anti-clobber),
preguntate si la amenaza que lo justificaba sigue existiendo. Acá el
"llamador viejo que no conoce el campo" ya no existía para vuelo/hotel, y el
patrón pasó de protección a bug.

### 3. Migración aditiva pura

`PdfCompleto_M1`: 4 columnas nullable (`FlightSegments` + `HotelBookings`),
`Down` simétrico, cero riesgo para datos de PROD. `InstallmentAmount` es
`decimal(12,2)` de punta a punta — jamás float para plata.

## Yapa del review

Faltaba un `.Include(r => r.Servicios)` en la query que alimenta el PDF —
sin eso, el servicio "Otro" (que ahora se dibuja en el PDF, default espejo
recomendado) no aparecía nunca. Lo cazó el review backend.

## Verificación real (no "debería andar")

- CI verde run 31771089708 (unit + integración Postgres + front + deploy VPS).
- Ronda N del robot en PROD (reserva de prueba 2026-1067): horarios y cuotas
  **persisten** tras recargar ✔ · PDF con fila de vuelo con horas/duración y
  línea `6 CUOTAS 280 USD` ✔ (verificado por mí leyendo el PDF) · vaciar
  todo y guardar **borra de verdad** ✔ · PDF final limpio, sin datos
  fantasma, con el vuelo en modo "solo fecha" ✔. La reserva de prueba quedó
  como estaba.

## Lección del arnés QA (para las próximas rondas)

El panel "+ Más detalles" puede venir YA ABIERTO al editar un servicio (el
toggle dice "Menos detalles"). Clickearlo a ciegas lo CIERRA y los
casilleros "desaparecen" — el primer intento de la ronda N falló entero por
eso. El helper ahora mira el texto del toggle antes de clickear.

## Qué sigue

- `Adr053_M2` (DROP de `DatesManuallySet`) en release APARTE, runbook D6.2.
- Pendientes de Gaston: probar "Enviar por WhatsApp" con una reserva suya;
  decisión pendiente de confirmar: el servicio "Otro" en el PDF quedó como
  default (fácil de revertir si no le cierra).
- Deuda anotada: tests de autorización de MessagesController; validación
  server-side de rango para cuotas si algún día dejan de ser informativas.
