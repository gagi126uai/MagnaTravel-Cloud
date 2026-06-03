# Recorrido honesto de la reserva — ¿se siente como un ERP de verdad?

**Fecha:** 2026-06-03. **Quién:** recorrido a mano del código real (no de los documentos), mirando la reserva como la usaría un agente de viajes en un día normal. **No se tocó nada.**

> Objetivo: que el dueño vea, en simple, qué fluye bien, qué está desconectado, y qué es una cagada que un agente no querría usar. Para decidir juntos qué se **cierra** primero (no qué se agrega).

---

## Lo que SÍ está bien (esto se siente ERP)

1. **La pantalla de la reserva está bien organizada.** Pestañas claras: Servicios, Pasajeros, Estado de cuenta, Vouchers, Documentos, Historial. El agente entiende dónde está cada cosa.
2. **El ciclo de la reserva tiene sentido.** Presupuesto (cargás cantidades) → Confirmada (cargás pasajeros con nombre) → En viaje → Cerrada. Con chips de color. Razonable.
3. **La reserva se acuerda de dónde vino.** Si nació de un lead de WhatsApp o de una cotización, tiene botón "Abrir cotización origen". Esto es clave para un producto vendible: la cadena WhatsApp → cotización → reserva está conectada.
4. **Cobrar funciona.** Registrar cobranza, emitir comprobante de pago, ver/descargar PDF: todo desde la reserva, ordenado.

---

## Lo que está DESCONECTADO (acá está el "no se siente un ERP")

### 1. 🔴 Facturar NO está en la reserva
El agente hace TODO en la reserva (carga servicios, cobra)… pero **para hacer la factura de AFIP tiene que irse a otro módulo (Cobranzas)**. La reserva solo te **muestra** las facturas en una lista, no te deja crearlas.
→ Para un agente, "cobrar y facturar" es un solo movimiento. Tenerlo partido en dos pantallas se siente como **dos sistemas distintos**, no como un ERP. **Este es el desconecte más grande.**

### 2. 🔴 Todo está en pesos — la reserva no sabe de dólares
La pantalla de la reserva muestra **todo en pesos**, fijo. La reserva por dentro no tiene "moneda": guarda costo, venta y saldo como números sin divisa.
→ Una agencia que vende un paquete en USD (casi todas) ve la reserva en pesos. El soporte de dólares existe **a medias y solo dentro del modal de factura**, pero **nunca llega a la reserva**. Hay un choque entre "yo vendo en dólares" y "el sistema piensa en pesos".

### 3. 🟠 Hay 6 formas de que exista un "servicio"
Hotel, Vuelo, Traslado, Paquete, Asistencia (cada uno su tabla) **+ un "Servicio" genérico viejo**. Por algo el roadmap dice que **solo Hotel está 100%**; los otros están a medio hacer en los flujos nuevos.
→ El agente aprende que "el hotel anda bien, lo demás a veces se rompe". El formulario de cargar servicios tiene **2141 líneas** (creció a los parches). Eso no se siente parejo.

### 4. 🟠 Está todo construido pero APAGADO — nada cerrado
La pantalla carga **dos versiones de cada cosa** según interruptores (dos ciclos de estado, dos formas de cancelar…). Hoy en producción están las viejas; las nuevas (cancelación nueva, NC parcial, estados nuevos, multimoneda, IA) están **construidas pero apagadas**, esperando firmas/pruebas.
→ Mucho hecho, **poco prendido y terminado de punta a punta**. Ese es el "cuento de nunca acabar" que sentís: el trabajo está, pero no cruza la línea de llegada.

### 5. 🟡 El precio se carga dos veces
El total de la reserva **sí se calcula** de los servicios. Pero al facturar, el agente **arma los renglones de la factura a mano** (no salen solos del tarifario/servicios).
→ Doble laburo: ponés el precio en el servicio, y de nuevo en la factura.

---

## El trío que querés vender: ERP + WhatsApp + IA

- **WhatsApp ↔ reserva:** conectado. El bot captura el lead → cotización → reserva, con trazabilidad. Esta parte está bien encaminada.
- **IA ↔ todo lo demás:** NO conectada todavía. Hoy es solo el "cerebro" base (apagado), sin enchufar a ninguna pantalla.

---

## Mi lectura en una frase

No te falta trabajo hecho — **te falta cerrar y conectar.** El sistema tiene las piezas de un ERP, pero el agente las siente sueltas porque **la plata (cobrar/facturar/moneda) está partida entre la reserva y otro módulo**, y porque **casi todo lo nuevo está apagado**. Si la reserva fuera de verdad el centro, desde ahí el agente debería **cobrar Y facturar, en la moneda que vendió, sin saltar de módulo.**

## Para decidir juntos (no hago nada hasta que elijas)

El candidato más fuerte para "que se sienta un ERP" es **#1: traer la facturación adentro de la reserva**. Es lo que más rompe la sensación de sistema único. Pero vos conocés tu operación: decime cuál de estos cinco te duele más en el día a día y por ahí arrancamos — **uno**, cerrado de punta a punta, antes de tocar otro.
