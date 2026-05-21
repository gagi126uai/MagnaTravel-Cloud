# 2026-05-21 - Mensaje al contador (round 3) - FC1.3 Fase 1

> Para mandar al contador antes de implementar Fase 2 (no bloquea Fase 1). Mensaje unico con 12 puntos: 3 fiscales nuevas + 8 confirmaciones profesionales + 1 confirmacion G4.

---

## Asunto sugerido

**MagnaTravel FC1.3 - Round 3: cierre operativo NC parcial Hotel (12 puntos)**

---

## Cuerpo del mensaje

Hola [contador],

Gracias por el criterio que nos diste el 2026-05-21 sobre NC parcial (matriz 8 casos + escenarios A y B). Lo estamos implementando en lo que llamamos FC1.3 Fase 1 (solo Hotel por ahora). Antes de la implementacion definitiva al ARCA (Fase 2), nos quedaron 12 puntos para cerrar con vos. Estan agrupados en 3 bloques.

---

### Bloque 1 - Fiscales nuevas (3 puntos)

**F1 - Criterio de borde en thresholds monetarios**

Vamos a parametrizar tres niveles:
- $500.000 -> debajo de este monto, NC parcial se auto-emite.
- $500.000 a $2.000.000 -> revision admin reforzada (admin distinto al vendedor, comentario min 20 chars).
- Mas de $2.000.000 -> revision contable (admin con comentario min 100 chars).

**Pregunta**: el borde es estricto `<` o `<=`? Nuestra propuesta: estricto `<`. Es decir, un monto exactamente de $500.000 entra en admin reforzada, no en auto. Lo mismo $2.000.000 entra en revision contable.

**F2 - NC parcial cuando el operador devuelve en cuotas**

Hay casos donde el operador devuelve la plata en varias cuotas a lo largo de semanas. Nuestra propuesta: **emitir UNA sola NC parcial en T0** (al momento del acuerdo de devolucion con el operador), por el total fiscal a acreditar, independiente de los flujos de plata posteriores. La NC fiscal es un evento fiscal; las cuotas son un movimiento financiero.

**Pregunta**: confirmas este criterio? O preferis que emitamos NCs parciales sucesivas a medida que llega cada cuota?

**F3 - Correccion de NC parcial ya emitida**

Si despues de emitir una NC parcial detectamos error (admin se equivoco al aprobar la liquidacion), tenemos dos vias:

- Emitir **ND complementaria** por la diferencia.
- **Anular la NC parcial original** y emitir una nueva NC parcial corregida.

Hoy nuestro ADR-002 dice "no usar ND en flow normal" para mantener el flujo simple. Pero esto puede chocar con casos reales.

**Pregunta**: cual de las dos vias preferis para FC1.3? O mejor que el sistema soporte ambas y el admin elija segun el caso?

**F4 - Penalidad del operador en modo reseller: reduce la NC al cliente o no?**

Nos cruzamos con una contradiccion en el criterio que nos pasaste:

- Por un lado, dijiste que cuando la agencia opera como **revendedor** (factura todo al cliente), la penalidad operador es "costo de la agencia, no reduce NC al cliente".
- Por otro lado, en uno de los casos de la matriz dijiste que la penalidad **SI reduce** el monto fiscal acreditable (caso 3: "factura total + penalidad valida = NC parcial, neto facturado por retenido").

Escenario concreto: cliente paga $1.000.000 por hotel (modo revendedor, Factura A). Cancela. Operador retiene $200.000 de penalidad. Cliente recibe $800.000 al bolsillo.

- **Opcion A**: NC parcial al cliente por **$800.000**. La penalidad reduce el monto fiscal. (Lo que dice el caso 3 de la matriz).
- **Opcion B**: NC parcial al cliente por **$1.000.000**. La penalidad es costo de la agencia, no afecta lo que el cliente "perdio de credito fiscal". La agencia se come la penalidad como gasto en su balance. (Lo que decia la parte general del criterio).

**Pregunta**: cual es la regla correcta?

---

### Bloque 2 - Confirmaciones profesionales (8 puntos)

**1 - Prorrateo IVA en NC parcial**

Nuestra propuesta: prorrateo proporcional al neto acreditado. Ej: factura $1.000.000 con IVA 21% sobre $826.446,28 neto -> NC parcial de $750.000 lleva IVA de $130.165,29 (proporcion 75%).

**Pregunta**: confirmas? O cuando la factura discrimino bien por item, preferis prorrateo item por item?

**2 - Penalidad del operador en modo "solo comision" (intermediario)**

Cuando un operador opera bajo modelo intermediario (la agencia factura solo su comision al cliente, no el total del servicio), y ese operador retiene una penalidad de la plata que tenia que devolver al cliente:

**Pregunta**: la penalidad reduce el monto que la agencia debe acreditar al cliente via NC, o solo afecta la cuenta corriente con el operador sin tocar la NC al cliente?

**3 - Casos triviales de Factura A**

Acordamos que Factura A (cliente RI) va siempre a revision manual obligatoria (caso 8 matriz). Nuestra implementacion no automatiza ningun caso de Factura A en Fase 1.

**Pregunta**: confirmas que NO queres automatizar ningun caso de Factura A (incluso cancelacion 100% sin retenciones)? O hay algun caso trivial que se pueda automatizar?

**4 - Wording default del template NC parcial**

El detalle/concepto que aparece en la NC parcial va a ser un template parametrizable desde el panel admin. Nuestro default propuesto:

```
NC parcial s/Fc {tipo} {numero} (PV {puntoVenta}). Monto fiscal acreditado: {monto} {moneda}. Concepto: {motivo}. Items no reintegrables retenidos: {nonRefundable} {moneda}.
```

Variables soportadas: `{tipo}`, `{numero}`, `{puntoVenta}`, `{monto}`, `{moneda}`, `{motivo}`, `{nonRefundable}`, `{operatorPenalty}`, `{cliente}`, `{cuit}`.

**Pregunta**: que wording preferis por default? Queres distintos templates por tipo de comprobante (A vs B vs C)?

**5 - Senales para detectar "cambia naturaleza fiscal del retenido"**

Para clasificar correctamente el caso 7 de tu matriz, vamos a detectar automaticamente 3 senales:

a. **Mix heterogeneo de retenciones**: si en la misma cancelacion conviven retenciones fiscales (IVA, IIBB) y deducciones operativas (penalidad, cargo administrativo), pasa a revision.

b. **Cambio de modo facturacion del operador**: si el operador en este momento esta en modo X pero al emitir la factura estaba en modo Y, pasa a revision.

c. **Cambio de condicion fiscal del cliente**: si el cliente cambio de Monotributo a RI (o viceversa) entre la emision y la cancelacion, pasa a revision.

Mas una senal manual: checkbox vendedor "la retencion tiene naturaleza distinta a la factura original".

**Pregunta**: estas 3 senales automaticas cubren los casos reales que ves en la practica? Sumarias o sacarias alguna?

**6 - Heuristicas para detectar "factura original confusa"**

Para clasificar correctamente el caso 4 de tu matriz, vamos a aplicar 4 heuristicas:

a. **Una sola linea generica**: si la factura tiene una sola linea con descripcion tipo "servicio turistico", "concepto", "operacion", pasa a revision.

b. **Items sin trazabilidad al booking**: si mas del 50% del importe de la factura corresponde a items que no tienen vinculo a la reserva original, pasa a revision.

c. **IVA no cuadra**: si la suma de IVA por linea no cuadra con el total IVA de la factura, pasa a revision.

d. **Facturas legacy**: si la factura fue emitida antes del deploy de FC1.3 (asumiendo que el modelo viejo no discrimino bien), pasa a revision.

Mas checkbox vendedor para forzar revision.

**Pregunta**: estas heuristicas son razonables o producen demasiados falsos positivos? Sumarias alguna?

**7 - Umbrales monetarios razonables**

Los umbrales que propusimos:
- $500.000 -> auto.
- $500.000 a $2.000.000 -> admin reforzada.
- Mas de $2.000.000 -> revision contable.

**Pregunta**: estos valores son razonables para MagnaTravel? Necesitan ajustarse hacia arriba/abajo? Son configurables en panel.

**8 - Vencimiento del plazo RG 4540 con BC en revision pendiente**

RG 4540 exige emitir la NC dentro de los 15 dias del hecho documentable (T2 = acuerdo con operador). Si una cancelacion queda mas de 13 dias en revision admin sin aprobar:

a. **Alerta + auto-aprobar** (riesgo: emite sin validacion humana).
b. **Alerta + bloquear emision** (riesgo: caemos en mora fiscal).
c. **Alerta + escalar a contador externo** (necesita rol nuevo).

**Pregunta**: cual de las 3 vias preferis? O hay otra estrategia que recomendarias?

---

### Bloque 3 - Confirmacion de decision interna (1 punto)

**9 - Cliente RI con Factura A + NC parcial: ND complementaria?**

En el escenario A que nos diste:
- Factura A $1.000.000 al cliente RI.
- Cancelamos, retenemos $200k penalidad + $50k item no reintegrable.
- Emitimos NC parcial $750.000 al cliente.
- La factura A original queda viva por $250.000 (neto facturado final).

Internamente decidimos NO emitir una ND complementaria por esos $250.000, asumiendo que la factura A original + la NC parcial son documentacion suficiente para que el cliente RI compute su credito fiscal.

**Pregunta**: estas de acuerdo? O el cliente RI necesita una ND complementaria para tener claridad documental? (esto seria tu excepcion 7 - "cliente RI necesita claridad para credito fiscal").

---

## Pie del mensaje

Lo que vos respondas en estos 12 puntos lo incorporamos al plan tactico de Fase 2 (emision real al ARCA) y a la configuracion del sistema (`OperationalFinanceSettings`).

Fase 1 (que ya esta funcionalmente disenada) puede arrancar implementacion sin esperar estas respuestas, porque solo persiste la liquidacion y bloquea los casos sensibles a revision admin - no emite NC todavia.

Si necesitas ver algun detalle adicional, esta todo documentado en `docs/architecture/plan-tactico-fc1-3.md` y `docs/explicaciones/2026-05-21-fc1-3-fase-1-plan-funcional.md`.

Gracias!

[Gaston]
