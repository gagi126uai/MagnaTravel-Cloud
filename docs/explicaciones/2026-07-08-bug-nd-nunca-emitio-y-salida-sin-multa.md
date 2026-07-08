# 2026-07-08 (noche) — El bug por el que la ND por multa NUNCA se emitió + la salida "no cobró multa"

Continuación de `2026-07-08-paso-de-multa-en-la-ficha.md`. Gastón probó el panel
nuevo en producción y reportó: loop ("corregir" nunca desaparecía), ninguna ND
emitida jamás, y la sensación de que "todos tienen multa". Pidió investigar en
el servidor y rediseñar si hacía falta.

## Diagnóstico con datos reales (ops-diagnostico: SQL + logs)

1. **SQL de prod**: las 13 anulaciones existentes tenían la ND en "revisión
   manual" — incluso 3 cerradas SIN multa y varias viejas. Sus correcciones de
   hoy (USD 200 / 500 / 649,98) se guardaron bien pero volvían a caer a manual.
2. **Logs de la API**: cada click en "Corregir monto y moneda" terminaba en
   `metric:cancellation_debit_note_emission_failed` con
   `KeyNotFoundException: Reserva no encontrado` en
   `TryEmitCancellationDebitNoteAsync`.

## Causa raíz (desde el commit original de ADR-013, `d29ac8a`)

`TryEmit` armaba el pedido de la ND con el **id interno** de la reserva como
texto ("31"). El resolvedor de `InvoiceService.CreateAsync`
(`ResolveInternalIdAsync`) **solo acepta GUID**: con "31" devuelve null → tira
"Reserva no encontrado" → catch → ND a revisión manual con mensaje genérico.
**La emisión automática de la ND por multa nunca funcionó en producción, ni una
vez.** Los ~3.200 unit tests no lo cazaron porque esta pieza se prueba con el
emisor simulado (mock de IInvoiceService): el resolvedor real nunca corría.

La NC parcial usa el mismo patrón de int-como-string PERO llama
`CreatePendingInvoice(int, request)` directo (sin resolvedor) — no está rota.

## Lo que se hizo (commit `88aeed2`, deployado)

1. **Fix**: TryEmit resuelve el PublicId real de la reserva antes de armar el
   pedido; si no aparece, rutea a manual con motivo en criollo (sin ids).
2. **Test de regresión que hubiera cazado el bug**: el request capturado debe
   llevar GUIDs parseables (verificado: fallaba sin el fix).
3. **La salida que faltaba** (el "todos tienen multa"): en el cartel trabado de
   la ficha, link secundario discreto "El operador no cobró esta multa" →
   confirmación en línea → cierre sin multa (waive existente). Gateado por
   `CanWaive` nuevo del read-model: multa Confirmed + ND no en juego + **admin**
   (la MISMA condición del waive real, extraída a un helper compartido; el
   requisito de admin lo cazó el reviewer: sin él, un no-admin veía un botón
   que rebotaba 409).
4. Endurecimiento: botón principal y waive no pueden dispararse a la vez.

Gates del delta: backend APROBADO, frontend APROBADO, data-exposure APROBADO.
Unit 3286/3286, front 1845/1845, build OK, deploy verde.

## Juicio sobre el diseño (pedido explícito de Gastón)

El diseño "el paso vive en la ficha" NO falló: **nunca llegó a correr** — todo
lo que Gastón vio era el síntoma de este bug (acciones que no completaban nunca
+ 13 anulaciones de prueba con estados heredados). Decisión: NO rediseñar
todavía; primero dogfoodear la versión que funciona. Si con el circuito sano
sigue sintiéndose mal, ahí sí se rediseña sobre datos reales.

## Cómo probarlo (el dogfood pendiente)

En cada ficha anulada con cartel naranja, UNA de dos:
- Si la multa es real → el botón principal ("Reintentar" / "Corregir monto y
  moneda" / "Emitir"). Ahora la ND sale de verdad: el cartel pasa a "se está
  emitiendo" y al rato queda resuelto (AFIP homologación).
- Si no hubo multa (dato de prueba) → el link chico "El operador no cobró esta
  multa" → confirmar. El cartel desaparece.

Caso especial F-2026-1041: multa 650.000 > factura 480.000 → va a seguir en
manual a propósito (candado M2); se resuelve corrigiendo el monto o cerrándola
sin multa.

Verificación técnica post-dogfood: en los logs no debe aparecer más
`cancellation_debit_note_emission_failed` con `KeyNotFoundException`.
