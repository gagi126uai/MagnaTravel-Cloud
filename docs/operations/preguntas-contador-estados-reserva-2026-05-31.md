# Dos preguntas para el contador — Estados de la reserva

> Contexto rápido (para el contador): estamos por activar en el sistema dos etapas nuevas en el ciclo de vida de una reserva de viaje, para que se parezca a cómo trabaja de verdad la agencia. Las dos etapas nuevas son **"Vendida"** y **"A liquidar"**. Antes de prenderlo necesitamos que un contador confirme dos criterios. Abajo cada uno con un ejemplo bien simple. Lo demás (cómo se programa) ya está resuelto; solo necesitamos el visto bueno de estos dos puntos.

Antes de las preguntas, qué significan las dos etapas nuevas, en criollo:

- **"Vendida"**: el cliente ya te compró el viaje y te pagó (o te dio una seña), **pero el operador mayorista todavía no te confirmó** los servicios. Es el ratito entre que vendés y que el operador te dice "ok, confirmado".
- **"A liquidar"**: el viaje **ya terminó**, pero todavía te falta **cerrar cuentas con el operador** (comisiones, saldos). Es un paso opcional, solo para las reservas que arreglás la plata con el operador después del viaje.

---

## Pregunta 1 — ¿"Vendida" y "A liquidar" cuentan como plata que te deben? (cuentas por cobrar)

**Ejemplo fácil:**

Una pareja te compra un viaje a Brasil por $1.000.000. Te paga $300.000 de seña y queda debiendo $700.000. Todavía **no le hiciste la factura** y el operador **todavía no te confirmó** los hoteles. O sea: la reserva está en la etapa **"Vendida"**.

En el reporte de tesorería hay un número que dice **"plata que los clientes todavía te deben" (cuentas por cobrar)**.

**La pregunta:** esos $700.000 que la pareja te debe, estando la reserva en **"Vendida"** (operador sin confirmar todavía) y también cuando está en **"A liquidar"** (viaje terminado, cerrando con el operador)...

> **¿tienen que aparecer en ese número de "plata que te deben"? ¿Sí o no?**

**Qué hace hoy el sistema:** los suma (dice que SÍ, son plata por cobrar). Necesitamos que el contador confirme que ese criterio está bien, o nos diga cómo debería ser.

---

## Pregunta 2 — Si ya facturaste y la reserva "vuelve para atrás" de etapa, ¿hay que anular la factura?

Primero, dos reglas que ya tiene el sistema hoy y que **no cambian**:

- Cuando **CANCELÁS** una reserva que ya tiene factura con CAE, el sistema **te obliga** a anularla primero (se emite Nota de Crédito). Bien.
- Una reserva en etapa **"Vendida"** (operador sin confirmar) **no se puede facturar** todavía, justamente para no emitir un comprobante por algo que el operador podría rechazar.

**Ejemplo fácil:**

Vendés un viaje, el operador te lo confirma (etapa **"Confirmada"**) y le hacés la **factura con CAE** al cliente. Más tarde te das cuenta de que cargaste algo mal y necesitás **retroceder una etapa** la reserva (de "Confirmada" para atrás) para corregir.

Acá aparece una asimetría:

- Si **cancelaras** la reserva → el sistema te obliga a anular la factura (Nota de Crédito).
- Si solo **retrocedés** la etapa para corregir → el sistema **NO** te obliga a anular la factura. La factura con CAE sigue viva, y la reserva queda en una etapa anterior.

**La pregunta:**

> **¿Está fiscalmente bien que, al retroceder de etapa (sin cancelar), la factura con CAE quede viva sin emitir Nota de Crédito?**
>
> ¿O al retroceder también habría que obligar a anular la factura, como cuando se cancela?

**Por qué preguntamos:** retroceder de etapa es para corregir cosas internas (un dato mal cargado), no para "deshacer la venta". La factura sigue siendo válida porque la venta sigue existiendo. Pero queremos que un contador confirme que dejarla viva no genera ningún problema fiscal, o nos diga en qué casos sí habría que anularla.

---

## Resumen para que el contador conteste rápido

| # | Pregunta | Qué hace hoy el sistema | Necesitamos que el contador diga |
|---|----------|------------------------|----------------------------------|
| 1 | ¿"Vendida" y "A liquidar" cuentan como plata por cobrar del cliente? | Las cuenta (SÍ) | ¿Está bien? ¿Sí / No / depende? |
| 2 | Al retroceder de etapa (sin cancelar) con factura ya emitida, ¿hay que anular la factura? | NO obliga a anular | ¿Está bien dejarla viva? ¿O hay que anular? |

> Cualquiera de las dos respuestas es fácil de ajustar en el sistema. Solo necesitamos el criterio del contador por escrito antes de activar las etapas nuevas. Mientras tanto, el sistema sigue funcionando exactamente como hasta hoy (las etapas nuevas están apagadas).
