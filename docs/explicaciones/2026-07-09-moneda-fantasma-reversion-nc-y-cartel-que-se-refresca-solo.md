# 2026-07-09 — La reversión de una NC en dólares quedaba en pesos + el cartel "se está emitiendo la multa" ahora se refresca solo

> Esta tanda se escribió la noche del 2026-07-08 pero la sesión se cortó por cuota antes de
> commitear. Se retomó y cerró el 2026-07-09: se cableó el último cabo suelto (`silentErrors`),
> se corrieron tests y los 4 gates, y se deployó.

## Para quién es este documento

Para cualquiera que entre al proyecto sin contexto (nivel trainee). Explica QUÉ se arregló,
POR QUÉ estaba mal y CÓMO quedó, sin asumir que leíste las sesiones anteriores.

---

## Parte 1 — El bug de plata: la "deuda fantasma" en pesos

### Qué veía Gastón

En una reserva facturada en dólares que después tuvo una Nota de Crédito (por ejemplo la
F-2026-1044, NC #97 por USD 13.200), el cartel de plata de la ficha quedaba incoherente:

- aparecía una **deuda en pesos de 13.200** que nunca había existido, y
- el **saldo a favor en dólares seguía intacto**, como si la NC no hubiera descontado nada.

Encima, el vigía nocturno de coherencia veía esa "deuda sin comprobante" y generaba avisos
falsos sobre reservas que en realidad estaban bien.

### Por qué pasaba

Cuando una Nota de Crédito obtiene CAE (la aprueba ARCA), el sistema crea un asiento interno
que "devuelve" la plata: un `Payment` negativo con `EntryType = "CreditNoteReversal"`. Ese
asiento se crea en dos lugares de `AfipService` (NC parcial y NC total), y **ninguno de los
dos le ponía la moneda**. El campo `Currency` del entity `Payment` tiene un default: `"ARS"`.

Resultado: una NC en DÓLARES generaba su reversión en PESOS. La plata "salía" de un bolsillo
en pesos (que nunca había recibido nada → quedaba en negativo = deuda fantasma) y el bolsillo
de dólares no se enteraba.

### El fix (3 piezas)

1. **El código** (`AfipService.cs`, los 2 sitios): ahora el asiento de reversión lleva
   `Currency = ArcaCurrencyMapper.ToIso(invoice.MonId) ?? Monedas.ARS`. La factura ya sabía
   su moneda real (`MonId`, en códigos de ARCA: `"PES"`/`"DOL"`); solo había que traducirla
   al ISO que habla el módulo de plata (`"ARS"`/`"USD"`).

2. **Los datos ya rotos en producción** (migración
   `20260708224240_RepairPhantomCurrencyCreditNoteReversal`): reparación de datos pura, cero
   cambios de esquema. Primero hace backup de las filas que va a tocar (tabla
   `_repair_20260708_phantom_currency_backup`, se deja en la base como red forense) y después
   corrige la `Currency` de los `CreditNoteReversal` cuya moneda no coincide con la de la
   factura que referencian. Es idempotente (una segunda corrida no encuentra nada para tocar)
   y conservadora (si algo no matchea, no lo toca).

3. **El recálculo del saldo** NO se hace en SQL crudo: eso es lógica de dominio. Lo hace el
   vigía nocturno de coherencia (W3, `RepairStaleMoneyProjectionAsync`), que compara la
   proyección guardada contra el cálculo fresco (que ahora lee la moneda correcta) y la
   reescribe con el escritor canónico. Si se quiere destrabar antes de la corrida de ~3am AR,
   el atajo correcto es el endpoint admin `POST /api/admin/maintenance/coherence/run-watchdog`
   (barre TODAS las reservas no archivadas). Ojo: el otro endpoint, `recalculate-money`, NO
   sirve acá — solo barre anuladas, y una NC parcial sobre una reserva viva quedaría afuera
   (hallazgo del gate de riesgo de datos).

### Tests de regresión

En `AfipServicePartialCreditNoteReversalTests`: 2 tests nuevos (NC total en USD y NC parcial
en USD → el reversal tiene que quedar en `"USD"`; antes del fix daban `"ARS"`) y se agregó el
assert de moneda a los 3 tests históricos en pesos (fijan que el default siga siendo `"ARS"`).

---

## Parte 2 — El cartel trabado: "se está emitiendo la multa" para siempre

### Qué veía Gastón

En la ficha de una anulada con multa, el cartel ámbar "Anulada — se está emitiendo la multa
al cliente. Puede demorar unos minutos" **quedaba clavado para siempre**, aunque del lado del
backend la ND ya se hubiera emitido hacía rato. La base decía "emitida"; la pantalla nunca se
enteraba. Solo un F5 a mano lo destrababa — y el texto prometía "unos minutos".

### El fix

Un hook nuevo (`useOperatorPenaltyPolling`, mismo patrón que el polling de facturas que ya
existía) que refresca la reserva solo, **cada ~10 segundos** (el intervalo lo pidió Gastón),
**únicamente mientras el cartel esté en la familia "procesando"** — es la única familia donde
el usuario no tiene ningún botón para apretar; las demás se refrescan solas cuando el usuario
hace algo, así que pollearlas sería gastar llamadas al pedo.

Detalles que importan:

- **Tope de ~3 minutos**: si el backend quedó realmente trabado (cola caída), deja de
  insistir y suma una línea chica: *"¿Tarda mucho? Actualizá la página."*
- **Errores silenciosos en los ticks de fondo**: `fetchReserva` ganó la opción
  `silentErrors`. Un refresco automático que falla NO le grita "Error al cargar la reserva"
  al usuario cada 10 segundos: la reserva sigue en pantalla y el próximo tick reintenta.
  (Este cableado en `ReservaDetailPage` fue justo lo que quedó colgado cuando se cortó la
  sesión: la opción existía pero nadie la pasaba.)
- La lógica de "¿debo pollear?" y "¿se agotó la espera?" vive en funciones puras en
  `operatorPenaltyBanner.js`, con 10 tests propios que simulan el paso del tiempo sin
  esperas reales.

---

## Gates y verificación

- Tests backend de la clase afectada: 8/8. Tests frontend: 1855/1855. Build de frontend OK.
- **4 gates APROBADOS, 0 bloqueantes**: `backend-dotnet-reviewer`, `frontend-reviewer`,
  `security-data-risk-reviewer` (migración + plata) y `data-exposure-reviewer`.
- Mejoras del review aplicadas antes de commitear:
  - bandera de cancelación en `useOperatorPenaltyPolling` **y** en el `useInvoicePolling`
    preexistente (si el usuario se iba de la página con un refresco en vuelo, el timer
    encadenaba un tick huérfano que nadie limpiaba);
  - `data-testid="multa-polling-timeout-hint"` en la línea "¿Tarda mucho?";
  - el comentario de la migración ahora apunta al endpoint correcto (`run-watchdog`, no
    `recalculate-money` que solo barre anuladas).
- CI/CD de la tanda anterior verificado verde (`8ecfdff` y `88aeed2` deployados).
- Post-deploy pendiente de esta tanda: correr `run-watchdog` para reacomodar los saldos
  reparados sin esperar al vigía nocturno, y chequear en prod que no haya reversiones
  legacy sin `RelatedInvoiceId` (el review sugirió el conteo; si da 0, no queda nada).
