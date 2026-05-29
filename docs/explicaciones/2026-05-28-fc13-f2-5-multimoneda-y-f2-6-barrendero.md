# 2026-05-28 (madrugada) — FC1.3 F2.5 (multimoneda) + F2.6/F2.6a (barrendero + contador)

> Para vos cuando retomes, o cualquier developer trainee que se sume.
> Nivel: explicación criolla, sin tecnicismos cuando se puede.

## TL;DR

En esta sesión completamos **tres piezas de código** que le faltaban al módulo de Nota de Crédito (NC) parcial:

1. **Multimoneda (F2.5)** — que el sistema pueda hacer la NC parcial en **dólares**, no solo en pesos.
2. **El "barrendero" (F2.6a)** — un robot que cada tanto destraba NC parciales que quedaron a medio camino con la AFIP.
3. **El "contador de la entrada" (F2.6)** — métricas para ver cuántas NC parciales se emiten/aprueban/rechazan.

Dos commits, ambos **pusheados a `origin/main`**: `0bb9ab6` (multimoneda) + `4b64e9c` (barrendero + contador). HEAD final: `4b64e9c`.

**El flag `EnablePartialCreditNoteRealEmission` sigue OFF.** Producción no cambió en nada. Esto NO está deployado todavía (está en GitHub, no en el VPS corriendo).

## La foto grande (por si te perdiste)

La agencia vende viajes y emite comprobantes oficiales que ve la AFIP. Cuando un cliente cancela **parte** de un viaje ya facturado, hay que emitir una NC parcial: un papel que dice "de aquella factura, te acredito esta parte". Veníamos construyendo la máquina que hace ese papel solo y lo manda a la AFIP. La máquina está construida pero **apagada con una llave** (el flag), hasta que el contador firme y la AFIP homologue.

## Pieza 1 — Multimoneda (F2.5)

### El problema
La agencia factura algunos viajes en **dólares**. Pero el sistema, al armar el mensaje a la AFIP, mandaba **siempre "pesos, cotización 1"** escrito a mano (`<MonId>PES</MonId><MonCotiz>1</MonCotiz>`). Si prendíamos la máquina con una factura en dólares, la NC habría salido como si fueran pesos = desfasaje fiscal grave. Por eso había un candado (guard) que abortaba toda NC parcial que no fuera en pesos.

### Qué hicimos
- El mensaje a la AFIP ahora usa la **moneda y cotización reales** de la factura.
- **Pesos quedó byte-idéntico** (sigue mandando `1`, sin cambiar ni un carácter) — así la facturación que ya anda no corre ningún riesgo de homologación. Los 6 decimales nuevos solo aplican a moneda extranjera.
- Creamos un solo lugar que sabe qué monedas se soportan: `ArcaCurrencyMapper` (ARS→PES, USD→DOL). Si llega una moneda que no sabemos (EUR, etc.), va a revisión manual, no se emite a ciegas.

### Bugs fiscales que atajó la revisión (importante)
La cadena de revisión (revisor de código + contador) atajó **dos errores serios antes de guardar**:
1. **NC en pesos para factura USD**: con la máquina prendida, una cancelación en dólares que se auto-aprobaba se escapaba por el camino viejo y emitía la NC en pesos. Lo cerramos alineando el criterio + un candado extra que impide que una factura no-pesos emita NC en pesos.
2. **Cotización "1" silenciosa**: si una factura en dólares no tenía tipo de cambio cargado, el código ponía "1" callado (un dólar = un peso). Ahora eso falla controlado y no llega a la AFIP.
3. **GAP-1** (defensa extra): una factura en dólares **vieja** quedó registrada en pesos en la AFIP. Si emitíamos una NC en dólares contra ella, el papel no coincidiría con su factura madre. Pusimos un candado que compara la moneda de la NC contra la de la factura original y, si no coinciden, manda a revisión manual.

## Pieza 2 — El barrendero (F2.6a)

### Para qué sirve
Cuando el sistema manda una NC a la AFIP, a veces se corta justo en el medio (timeout, caída) y la NC queda "colgada" sin saber si la AFIP la recibió o no. El barrendero es un robot que cada media hora revisa esas NC colgadas y las resuelve.

### El primer intento estaba mal (y la revisión lo cazó)
La primera versión:
- **No destrababa de verdad**: por un detalle técnico, terminaba solo avisando a los 10 días.
- **Riesgo fiscal**: confirmaba la NC buscando "por monto", así que si había dos NC del mismo monto sobre la misma factura, podía confirmar con el comprobante equivocado.

### Cómo quedó (bien)
Lo rehicimos para que **reuse el mecanismo seguro que ya existía** (el de no duplicar papeles). Ahora:
- Consulta a la AFIP con el **número exacto** del comprobante (no adivina por monto).
- Si la NC todavía la está procesando otro proceso, **no la toca** (evita pisarse).
- Si nunca llegó a mandarse, la **re-manda de forma segura** (sin duplicar).
- Si algo del cierre falla, **no la da por buena**: la deja para el próximo ciclo en vez de dejarla a medias sin avisar.

## Pieza 3 — El contador de la entrada (F2.6)

Como el aparatito que cuenta cuánta gente entra a un local. Lleva la cuenta de: cuántas NC parciales se emitieron, cuántas aprobó la AFIP, cuántas rechazó, cuántas se abortaron por algún candado fiscal. Sirve para monitorear cuando la máquina esté prendida.

## Qué falta (no es de hoy)

### Opcional / cuando quieras
- Correr las pruebas en el **VPS** (`bash scripts/ops/run-tests-fc13.sh`). Algunas pruebas solo corren allá (la base de datos está en el servidor, no en tu máquina). No prende nada.
- Deployar al VPS (no urgente — el flag está apagado igual).

### Deuda anotada para "antes de prender la llave"
- El barrendero, para encontrar la "huella" de cada NC, hoy la **re-calcula** en vez de tenerla guardada en una columna. Hoy funciona, pero conviene a futuro agregar una columna `Invoice.IdempotencyKey` para que sea a prueba de balas. (No bloquea nada.)

### Gestión humana (ya estaba en la lista)
- Firma del contador (4 preguntas).
- Homologación con la AFIP de una NC en dólares con varias alícuotas (conseguir un CAE aprobado de prueba).

## Archivos clave (si querés mirar el código)

| Archivo | Qué tiene |
|---|---|
| `src/TravelApi.Domain/Helpers/ArcaCurrencyMapper.cs` | El único lugar que sabe qué monedas se soportan (ARS→PES, USD→DOL). |
| `src/TravelApi.Infrastructure/Services/AfipService.cs` | `BuildMonedaSoapFragment` (arma moneda/cotización para la AFIP, pesos byte-idéntico). |
| `src/TravelApi.Infrastructure/Services/InvoiceService.cs` | Emisión + el mecanismo de no-duplicar compartido + counters. |
| `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs` | Los candados fiscales de la NC parcial (moneda soportada / TC sano / NC vs origen). |
| `src/TravelApi.Infrastructure/Services/PartialCreditNotePostingReconciliationJob.cs` | El barrendero. |

## Cómo se trabajó (para no olvidar)

Se usó la cadena completa de subagentes (regla del proyecto): implementador → revisores en paralelo (código + contador) → fixes → re-revisión, hasta dejar todo en verde. La revisión **atajó bugs fiscales reales** que el build no detectaba — vale la pena el ida y vuelta en código que toca la AFIP.
