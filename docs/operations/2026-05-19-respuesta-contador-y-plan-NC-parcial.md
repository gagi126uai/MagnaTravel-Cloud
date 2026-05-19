# Respuesta del contador (2026-05-19) + plan para cumplir condiciones

## Resumen de la respuesta del contador

El contador NO firmo el signoff con la version original. Acepta el principio
de "una sola aprobacion" pero con 9 condiciones especificas. La clave de su
critica es:

> "En turismo eso puede estar mal, porque una cancelacion no siempre implica
> una devolucion total ni una NC directa por el total facturado."

Esto significa que el sistema tiene que poder emitir **NC parcial** (no
siempre total), porque en agencia de viajes lo normal es que el operador
retenga una penalidad y solo se devuelva una parte.

## Mapeo de las 9 condiciones contra el codigo actual

| # | Condicion | Estado | Acciones requeridas |
|---|---|---|---|
| 1 | Pantalla muestra factura afectada | Backend OK, UI por verificar | Auditar modal `RequestApprovalModal.jsx` + agregar info si falta |
| 2 | Muestra si NC es total o parcial | **NO IMPLEMENTADO** — solo NC total hoy | Implementar NC parcial (ver plan abajo) |
| 3 | Muestra importe de la NC | Backend OK, UI no muestra | Sumar campo `creditNoteAmount` al payload de aprobacion + render en modal |
| 4 | Muestra motivo | OK — `OverrideReason` es free-text required en `ConfirmCancellationRequest` | Solo verificar que el modal lo expone |
| 5 | NC asociada a comprobante original | OK — `Invoice.OriginalInvoiceId` + `Invoice.AnnulmentApprovalRequestId` + prefix `[BC-XXXX]` en reason | — |
| 6 | NC no puede ser mayor a importe fiscal pendiente | Hoy implicito (NC = total). Cuando implementemos parcial hay que validar | Sumar invariante `INV-127` (a definir): `nc.Amount <= invoice.Total - SUM(NCs previas)` |
| 7 | Audit log completo | OK — 14 audit actions + 10 tests audit presence | — |
| 8 | Si hay dudas/diferencias → revision manual | **CLARIFICAR CON CONTADOR**: que considera "diferencia" o "duda" | Mensaje al contador con preguntas (abajo) |
| 9 | Boton emergencia no inventa, solo concilia | OK — `ForceArcaConfirmationRequest` requiere `CreditNoteInvoicePublicId` (NC realmente emitida en ARCA) | — |

## Plan para cumplir las condiciones

### Fase A — Implementar NC parcial (varios subtasks)

**Backend:**
1. Sumar campo `CreditNoteAmount` (decimal) al `ConfirmCancellationRequest` o
   un DTO derivado. Hoy el calculo es implicito (`invoice.ImporteTotal`).
2. Modificar `BookingCancellationService.ConfirmAsync` para aceptar el
   importe parcial desde el request (o calcularlo a partir de los datos del
   refund esperado del operador).
3. Modificar `InvoiceService.EnqueueAnnulmentAsync` para aceptar
   `annulmentAmount` opcional (default = `invoice.ImporteTotal` para
   backward-compat).
4. Modificar el job `ProcessAnnulmentJob` para que la NC fiscal use ese
   importe en vez de siempre `ImporteTotal`.
5. Sumar invariante `INV-127` (o el siguiente libre): la suma de todas las
   NC emitidas para una factura no puede exceder `invoice.ImporteTotal`.
6. Migracion EF: sumar columna `Invoice.AnnulledAmount` (decimal nullable) o
   computar dinamicamente la suma de NCs hijas.
7. Actualizar 5+ tests para reflejar el nuevo flujo.

**Frontend:**
8. Auditar `RequestApprovalModal.jsx` — sumar bloque que muestre:
   - Factura original (numero + fecha + importe total).
   - Importe propuesto para la NC (calculado).
   - Si la NC es total o parcial.
   - Si es parcial: importe que queda activo en la factura original.
   - Motivo (textarea required).
9. Si el flow lo permite: editor del importe NC (con validacion contra el
   maximo).
10. Tests UI para el modal nuevo.

**Documentacion:**
11. Nuevo ADR (ADR-009): "NC parcial vs total en cancelacion de reservas".
12. Actualizar ADR-004 con condiciones del contador.
13. Actualizar `docs/explicaciones/2026-05-18-fc1-2-implementacion.md`
    seccion "lo que falta antes de prod".

### Fase B — Clarificar condicion 8 con el contador

Necesitamos definir que considera "duda" o "diferencia" para disparar
revision manual. Posibles disparadores:

- Cuando el importe propuesto NO coincide con `invoice.ImporteTotal -
  SUM(NCs previas)` (ej. operador devolvio menos de lo esperado).
- Cuando la fecha de cancelacion es mayor a X dias post-emision factura.
- Cuando la moneda es extranjera (USD, EUR).
- Cuando el cliente es Responsable Inscripto (matriz fiscal diferente).
- Cuando el importe es mayor a un umbral X.
- Cuando el operador es nuevo (sin historial).

## Mensaje listo para mandar al contador

> Hola [contador],
>
> Gracias por el feedback. Te respondo punto por punto.
>
> **De las 9 condiciones que pediste:**
>
> Ya tenemos cubiertas las siguientes:
> - **(4)** El admin escribe el motivo al aprobar — campo requerido.
> - **(5)** La NC queda asociada al comprobante original con campo nuevo
>   `Invoice.AnnulmentApprovalRequestId` + prefijo `[BC-XXXX]` en el motivo.
> - **(7)** Audit log completo, con 14 tipos de eventos auditables y 10
>   tests automaticos que verifican que cada evento queda persistido.
> - **(9)** El boton de emergencia (ForceArca) requiere obligatoriamente el
>   ID de una NC efectivamente emitida en ARCA — no se puede "inventar" una
>   confirmacion.
>
> Vamos a trabajar en cubrir las que faltan:
> - **(1, 3)** Pantalla de aprobacion: vamos a sumar al modal la factura
>   afectada y el importe propuesto de la NC.
> - **(2)** **NC parcial**: hoy el sistema solo soporta NC total. Vamos a
>   implementar la logica para NC parcial — el importe se va a calcular en
>   base a lo que el operador efectivamente devolvio (no en base al monto
>   facturado original). Es trabajo de aprox 1 semana.
> - **(6)** Validacion para que la suma de NCs no supere el importe fiscal
>   de la factura. Lo agregamos como invariante de negocio que rechaza el
>   intento.
>
> **Sobre la condicion 8 (revision manual) — necesito que me ayudes a
> definir:**
>
> ¿Que casos consideras que ameritan revision manual? Te tiro algunas
> hipotesis para que me digas cuales aplican:
>
> 1. Cuando el importe propuesto no coincide con el saldo fiscal pendiente
>    de la factura (ej. operador devolvio un monto distinto al esperado).
> 2. Cuando la cancelacion ocurre mas de X dias despues de emitida la
>    factura (ej. 30 dias, 60 dias, mas).
> 3. Cuando la factura es en moneda extranjera (USD, EUR).
> 4. Cuando el cliente es Responsable Inscripto (matriz fiscal compleja).
> 5. Cuando el importe es mayor a X (umbral fiscal).
> 6. Cuando el operador es nuevo o sin historial.
> 7. Otros casos que vos consideres.
>
> Cualquier criterio que me des lo modelamos en el sistema como un check
> que dispara workflow de revision manual (con segunda aprobacion + audit
> reforzado).
>
> **Calendario propuesto:**
>
> - Esta semana: implementar NC parcial + condiciones 1, 3, 6.
> - Proxima semana: implementar condicion 8 segun tu definicion.
> - Cuando este todo: te paso de nuevo para firma definitiva.
>
> **Mientras tanto:** mantenemos el modulo nuevo APAGADO en produccion. La
> agencia sigue operando con el flujo viejo de doble aprobacion (sin
> cambios).
>
> Gracias.
>
> [firma]

## Lista de chequeo para Gaston

- [ ] Mandar este mensaje al contador (texto arriba).
- [ ] Esperar respuesta sobre condicion 8 (los criterios que ameritan revision manual).
- [ ] Una vez recibida la respuesta, abrir tarea de implementacion NC parcial (esta documentada como Task #22 en este repo).
- [ ] NO prender `EnableNewCancellationFlow=true` hasta tener signoff definitivo.

## Cambios respecto al mensaje anterior (`2026-05-18-mensaje-contador-ops-fiscal-001.md`)

El mensaje anterior pedia signoff sobre la version original del modulo. El
contador rechazo eso. Este mensaje:
- Reconoce las 9 condiciones.
- Mapea las que ya estan vs las que faltan.
- Pide clarificacion sobre la condicion 8 (mas grande de definir).
- Propone calendario de implementacion.
- Mantiene el feature flag apagado en prod.

El mensaje anterior queda obsoleto. Si Gaston ya lo mando, este mensaje es
la **respuesta a la respuesta** del contador.
