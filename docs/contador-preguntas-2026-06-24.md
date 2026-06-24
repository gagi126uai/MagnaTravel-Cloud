# Consultas al contador — MagnaTravel (2026-06-24)

Contexto: agencia de viajes minorista. Hoy emisor Monotributo (en algún momento pasaría a RI).
Facturación electrónica ARCA. Ya está implementado y en uso: al ANULAR un viaje se emite Nota de
Crédito TOTAL al cliente; si el operador retiene una multa que se le traslada al cliente, se emite
una Nota de Débito (pass-through). Estas consultas son para confirmar criterios antes de avanzar.

---

## 1. Nota de Débito "pass-through" por multa del operador
El 1/6 usted indicó que **si la penalidad se la queda el operador, la agencia minorista NO emite
Nota de Débito propia** (no es contraprestación de la agencia). Hoy el sistema, cuando el operador
retiene una multa y la agencia se la traslada al cliente, emite una **Nota de Débito al cliente**
por ese monto.

**Pregunta:** ¿Es compatible emitir esa ND al cliente como traslado de la multa del operador con
el criterio que firmó (que la agencia no genera ND propia)? Es decir: ¿esa ND es un **recupero de
gasto sin ingreso gravado propio** de la agencia, o debería documentarse de otra forma? ¿Lleva IVA
en cabeza de la agencia (cuando pase a RI) o no?

## 2. Anulación de una venta con VARIAS facturas
Una reserva puede tener más de una factura emitida (anticipo + saldo, o servicios facturados por
separado). Vamos a permitir anularla emitiendo **una Nota de Crédito por cada factura**.

**Preguntas:**
- ¿Correspondencia de tipos: Factura A → NC A, Factura B → NC B, etc., una por comprobante? (así
  lo entendemos)
- Si las facturas son de **distinta moneda** (una en ARS y otra en USD), ¿cada NC va en la moneda
  de su factura origen, con el TC del comprobante asociado?
- ¿Algún recaudo especial si las facturas tienen alícuotas de IVA distintas?

## 3. Momento de emisión de la factura de venta
Modelo prepago (el cliente suele pagar antes de viajar). Hoy podemos emitir la factura desde que la
reserva está confirmada, independientemente del pago.

**Pregunta:** Para servicios, entendemos que ARCA exige emitir **cuando se percibe el precio (total
o parcial) o cuando concluye la prestación, lo que ocurra primero**. ¿Confirma ese criterio para
esta agencia? Concretamente: cuando el cliente paga una **seña/cuota**, ¿ya corresponde emitir
comprobante por esa percepción, o se puede esperar a la factura final? ¿Cambia algo por el régimen
especial de turismo / intermediación?

## 4. Moneda de la multa del operador y diferencia de cambio
Al operador se le paga en USD. La multa que retiene puede ser en USD o ARS. La ND al cliente se
emite a un TC (el de emisión), que puede no coincidir con el TC al que el operador retuvo.

**Preguntas:**
- ¿La ND al cliente por la multa se emite en la **misma moneda que la factura original** del cliente?
- La **diferencia en pesos** entre lo que retuvo el operador (a su TC) y lo que se le cobra al
  cliente por la ND (a otro TC), ¿es resultado de la agencia (resultado financiero / diferencia de
  cambio) y se registra aparte del costo/ingreso de la operación?

## 5. (Para cuando construyamos la gestión del reembolso del operador) Conciliación
"Le pagué al operador USD X, esperaba que devuelva USD Y, me llegó USD Z, retuvo USD W."

**Preguntas:**
- ¿Cuándo se da por **cerrada** la cuenta por cobrar al operador? ¿Con qué tolerancia (centavos)?
- El **descuadre en pesos** por diferencia de TC entre el pago y el reembolso, ¿a qué cuenta va?
- Si el operador retiene **más de lo esperado** después de emitida la NC/ND, ¿se emite una ND
  complementaria al cliente o lo absorbe la agencia?

---

Estas confirmaciones definen comportamiento fiscal de producción; la validación final es suya.
