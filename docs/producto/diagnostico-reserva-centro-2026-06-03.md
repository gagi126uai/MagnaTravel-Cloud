# Diagnóstico de fondo: por qué se siente "todo desconectado"

**Fecha:** 2026-06-03. Análisis del código real (arquitecto) + referencia de cómo funcionan los ERP de agencias de viajes (dominio). No se tocó nada. Reemplaza el finding #1 del recorrido anterior (que estaba mal: facturación va aparte a propósito).

---

## La buena noticia primero

**El sistema NO te deja borrar lo que no se debe.** Hay candados bien puestos (`DeleteGuards`, `MutationGuards`): no podés borrar ni editar una reserva, un pago, un pasajero o un proveedor que ya tienen factura, recibo o voucher. La parte fiscal (AFIP) es **lo mejor armado de todo el sistema**. Tu miedo de "se rompe/se borra cualquier cosa" está mayormente cubierto.

---

## El problema REAL (y es uno solo, de fondo)

**Cada módulo cuenta la plata por su cuenta, con reglas distintas. No hay un solo número que mande.**

- La **reserva** calcula lo que vendiste de una forma.
- El **proveedor** calcula lo que le debés de otra (con otros estados).
- La **factura** usa montos que se **tipean a mano** y no se comparan con nada de la reserva.
- La **caja** suma de otra fuente más.

No existe ningún lugar donde *"esto vendí = esto facturo = esto cobré = esto debo, y todo cuadra"*. **Por eso sentís que ninguna idea cierra: no es que esté roto, es que nadie pone de acuerdo los números entre los módulos.** La reserva es una isla porque no es la dueña de la verdad de la plata — cada esquina inventa la suya.

---

## Los 3 agujeros concretos

1. **🔴 La factura no nace de la reserva.** Los renglones de la factura se cargan a mano en otra pantalla; el sistema no controla que el total facturado coincida con lo vendido. Podés facturar $100.000 una reserva de $80.000 y nadie lo nota.
2. **🔴 El cliente no tiene saldo.** El campo "saldo del cliente" existe en el código pero **nunca se llena** (está muerto). No hay forma de responder "¿cuánto me debe Juan en total?" — su deuda está partida adentro de cada reserva.
3. **🔴 El permiso "Caja" no controla la caja.** Existe en la pantalla de roles y se lo asignás a empleados, pero **ningún lugar del código lo usa**. Dás o quitás "Caja" y no pasa nada: es seguridad que creés tener y no tenés.

(Además: la cuenta del proveedor se lleva global, no por reserva — otra cosa que no "vuelve a la reserva".)

---

## Por dónde se empieza a poner orden (sin reescribir ni fusionar nada)

El orden importa. No se empieza por las esquinas, se empieza por el centro.

1. **Hacer que la reserva sea la ÚNICA fuente de la verdad de la plata.** Un solo cálculo central que diga: esto vendí, esto cuesta, esto cobré, esto es el saldo — y por cada servicio. Hoy ese cálculo está duplicado en varios lados con reglas distintas. **Este es el paso clave: es darle al centro del hexágono el número correcto.**
2. **Atar la factura a ese número** (que al facturar, el sistema compare contra lo vendido y avise si no cuadra). Sin tocar el flujo de AFIP que ya anda.
3. **Darle saldo real al cliente** (llenar el campo muerto, o sacarlo).
4. **Cerrar el permiso de Caja** (conectarlo o eliminarlo). Es chico.
5. **Decidir qué pasa con el "servicio" genérico viejo** que convive con los 5 tipos nuevos.

**Por qué en este orden:** una vez que el centro (la reserva) tiene el número correcto, todas las esquinas (factura, cliente, caja, proveedor) lo leen de ahí en vez de inventarlo. Eso es, literalmente, el hexágono funcionando: centro con la verdad, esquinas que la consultan. Si empezás por las esquinas, seguís parchando números que no cuadran.

---

## En una frase

No hay que reescribir nada ni juntar módulos. Hay que **hacer que la reserva sea la dueña de la verdad de la plata, y que el resto la lea de ahí.** Ese es el hilo natural que falta.
