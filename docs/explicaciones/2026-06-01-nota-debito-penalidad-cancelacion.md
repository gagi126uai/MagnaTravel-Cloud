# Nota de Débito por penalidad en cancelaciones (2026-06-01)

> Para Gastón, explicado fácil. Esta sesión cambiamos **cómo se documenta la penalidad cuando un cliente cancela**, siguiendo lo que firmó un contador matriculado. Todo quedó **detrás de una llave (flag) apagada**: mientras esté apagada, no cambia NADA de lo que ves hoy.

## El problema

Cuando un cliente cancela y hay una penalidad, el sistema hoy hace **una sola Nota de Crédito por el total** y la penalidad queda como un número suelto, sin comprobante propio. Un contador **matriculado** dijo que lo correcto es:

> **Nota de Crédito por el TOTAL** (se anula la factura entera) **+ Nota de Débito por la penalidad** — pero SOLO cuando la penalidad es **plata tuya** (un cargo propio de la agencia). Si la penalidad la retiene el mayorista, vos hacés solo la nota de crédito y nada más.

Ejemplo pelotudo: cliente pagó $100.000, cancela, penalidad $30.000.
- Si la penalidad es **tuya**: Nota de Crédito de $100.000 + Nota de Débito de $30.000.
- Si la penalidad la retiene el **mayorista**: solo la Nota de Crédito de $100.000 (vos no facturás nada, esa plata no es tuya).

## Qué construimos (el MVP)

La **Nota de Débito ya existía** en el sistema (para otras cosas). Lo que hicimos fue **engancharla a la cancelación**, con mucho cuidado:

- Solo emite la nota de débito **cuando la penalidad es tuya** y **cuando el operador ya confirmó el monto** (nunca sobre una estimación).
- Por ahora solo en **Monotributo** (nota de débito tipo C, sin IVA discriminado). Cuando seas Responsable Inscripto, eso va aparte (con IVA), y por ahora va a **revisión a mano**.
- Ante cualquier duda (multimoneda, seguros, factura A, etc.), **no emite solo: manda a revisión manual**. Es conservador a propósito.

## Los dos candados importantes

1. **Que la penalidad no se cobre dos veces.** El sistema podía, sin querer, descontarte la penalidad del reembolso al cliente Y además hacerle la nota de débito. Pusimos un candado para que la penalidad propia se cobre **una sola vez**: o como nota de débito, o como descuento, nunca las dos.

2. **Un bug que encontró el revisor.** El sistema elegía mal el "tipo de papel" de la nota de débito: para una nota de débito asociada a una factura C, salía como "factura" en vez de "nota de débito". Lo arreglamos y le pusimos pruebas automáticas para que no vuelva a pasar.

## La llave de seguridad

Todo esto está detrás de una **llave llamada `EnableCancellationDebitNote`, que viene APAGADA**. Con la llave apagada, el sistema hace **exactamente lo de hoy** (nota de crédito total, sin nota de débito).

## Cómo se trabajó (cadena de revisión)

Por ser algo que toca **plata, facturas y AFIP**, pasó por muchas manos: el contador matriculado (3 rondas) → el arquitecto diseñó → un revisor lo desafió y encontró un candado mal puesto → el arquitecto lo corrigió → segundo revisor lo aprobó → el backend lo programó → el revisor de backend encontró el bug del tipo de papel → lo corregimos. Quedó committeado (`d29ac8a`).

## Lo que FALTA antes de poder prender la llave (NO es ahora)

1. **La pantalla (frontend)** para que el vendedor diga "esta penalidad es propia / del operador" y para confirmar el monto. Hoy el backend está listo pero **nadie le dice** qué tipo de penalidad es, así que siempre manda a revisión manual (es a propósito, falta la pantalla).
2. **Correr los tests en el servidor (VPS)** + aplicar la migración nueva.
3. **Detalles finos**: un trabajito que revise solo si quedó alguna nota de débito a medio emitir, y confirmar con el contador/AFIP el código de IVA de la nota de débito C antes de homologar.
4. **Marcar qué operadores** te dejan cobrar penalidad propia (es configuración tuya).

> Recordá: aunque deployes, **la llave arranca apagada** → no cambia nada hasta que se cierren los puntos de arriba y vos la prendas.
