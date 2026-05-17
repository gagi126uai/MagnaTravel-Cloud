# Preguntas al contador — Módulo de cancelaciones y reembolsos

**Contexto para el contador**: estamos desarrollando un módulo nuevo en el sistema de MagnaTravel que maneja todo el ciclo de cancelación de reservas: emisión de la nota de crédito al cliente, recepción del refund del operador (con sus penalidades y deducciones), y posterior devolución al cliente (saldo a favor o egreso físico).

Antes de poner el módulo en producción, necesito tu validación profesional sobre estos puntos. Algunos son urgentes (bloquean el lanzamiento), otros son para tener previsto a futuro.

---

## Mensaje 1 — Lo urgente (bloquea producción)

### Pregunta 1 — Plan de cuentas

Para registrar gastos y créditos fiscales del nuevo módulo de cancelaciones, necesito saber **qué sistema contable usás** (Tango, Bejerman, Holistor, Excel manual, otro) y si me podés pasar los **códigos de las cuentas** que vas a usar para estos casos:

- Penalidades que cobra el operador cuando cancelamos
- Gastos bancarios o comisiones por transferencias
- Diferencias de cambio (cuando cobramos en USD y devolvemos en USD con otro tipo de cambio)
- Retenciones que nos hacen (si en el futuro pasamos a Responsable Inscripto)

### Pregunta 2 — Tipo de cambio

Cuando facturamos en dólares y después tenemos que emitir una nota de crédito por una cancelación, **¿qué tipo de cambio usás?**

- ¿BCRA comunicación A 3500?
- ¿Banco Nación mayorista?
- ¿Banco Nación minorista?
- ¿El oficial de AFIP?

Y ¿lo tomás del **día anterior** o del **mismo día** de la operación?

### Pregunta 7 — Comprobantes de las penalidades del operador

Cuando el operador nos cobra una penalidad de $200 por una cancelación, **¿siempre nos manda factura o recibo formal**, o a veces solo nos descuenta sin documento?

Si no hay comprobante formal, **¿podemos igual deducirlo en Ganancias** o ARCA nos lo rechaza?

---

## Mensaje 2 — Operativo

### Pregunta 4 — Plata que el operador debe y no devuelve

Cuando un cliente cancela y le pedimos al operador que nos devuelva la plata, **¿cuánto tiempo esperamos antes de considerar que el operador no va a devolver más?**

Y cuando ya pasó ese tiempo, **¿cómo lo registrás contablemente?**

- ¿Lo damos por perdido (pérdida del ejercicio)?
- ¿Lo dejamos como "crédito a cobrar" y reclamamos legalmente?
- ¿Lo provisionamos parcialmente (50% a los 60 días, 75% a los 120, 100% al año)?

### Pregunta 9 — Cancelaciones viejas que ya están en el sistema

Hoy el sistema tiene **cancelaciones viejas** que se manejaron con el flow viejo (antes del módulo nuevo). Algunas tienen plata que no se sabe bien cómo cerró (si se devolvió, si quedó como saldo a favor, si se perdió).

**¿Querés que las reconcilie manualmente una por una** antes de que arranquemos el módulo nuevo, **o las dejamos como deuda fiscal conocida** y las atendés cuando aparezcan?

---

## Mensaje 3 — Cuando crezcamos / Si pasa

### Pregunta 3 — Diferencias de cambio

Cuando vendemos un paquete a USD 1000 en marzo (TC $1000 = $1.000.000 pesos) y el operador nos devuelve USD 800 en mayo (TC $1100 = $880.000 pesos), tenemos una diferencia de $120.000 pesos por el dólar que se movió.

**¿Vos esa diferencia la registrás:**

- **Al cierre mensual** (aunque no se haya cobrado/pagado todavía — "no realizada")?
- **O solo cuando efectivamente se mueve la plata** ("realizada")?

### Pregunta 5 — Si crecemos a Responsable Inscripto

Hoy MagnaTravel está como Monotributo. Si en algún momento pasamos a Responsable Inscripto:

- **¿Qué pasa con las facturas y notas de crédito que ya están emitidas como Monotributista?** ¿Se mantienen como están o se reclasifican?
- **¿Qué pasa con saldos a favor de clientes en USD que quedaron pendientes** del régimen Monotributo? ¿Hay un tratamiento especial al cruzar?

### Pregunta 6 — Operadores del exterior

Cuando trabajamos con operadores de otros países (Brasil, USA, España, etc.):

- **¿Argentina tiene convenio para evitar doble imposición con esos países?** ¿Cuáles son los más comunes que usamos?
- Si el operador retiene un impuesto del país de origen (ej: withholding tax USA del 30%), **¿lo podemos descontar de nuestro Ganancias o es un gasto perdido?**

### Pregunta 8 — Ingresos Brutos por provincia

**¿En qué provincias opera MagnaTravel** (donde están los clientes o donde tributamos IIBB)?

Y de esas provincias, **¿alguna obliga a los Monotributistas a actuar como agentes de retención de IIBB o sufrir retenciones**? Algunas jurisdicciones lo hacen, otras no.

### Pregunta 10 — Diferencias de cambio según norma profesional

**¿Vos seguís la RT 54 de FACPCE** (norma argentina sobre operaciones en moneda extranjera con inflación) **o seguís otra norma?**

Pregunto porque cambia cómo registramos las diferencias de cambio en USD cuando hay inflación alta.

---

## Notas para mí (Gaston)

- **Mensaje 1** = lo mínimo para que el módulo entre en producción.
- **Mensaje 2** = se puede levantar después del primer release, pero antes de tener volumen real de cancelaciones.
- **Mensaje 3** = se puede dejar abierto. Aplica cuando MagnaTravel crezca o aparezcan casos específicos (operadores del exterior, paso a RI).

**Decisión 2026-05-13**: parte contable se hace **al final del roadmap B1.15**. El módulo de cancelación (FC1..FC4) puede arrancar técnicamente sin estas respuestas — los datos quedan registrados, el contador asienta manualmente hasta que las respuestas estén integradas.
