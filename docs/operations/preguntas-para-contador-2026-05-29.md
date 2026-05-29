# 📋 Preguntas para el contador — facturación, multimoneda y cancelaciones (2026-05-29)

> Para cuando consigas un contador matriculado. Cada pregunta viene con un **ejemplo de la vida real** para que se entienda rápido, **por qué nos importa**, y **lo que averiguamos por nuestra cuenta** (para que lo confirme o corrija — NO es palabra santa, lo sacamos de fuentes oficiales pero sin firma profesional).

## Contexto para el contador (leer primero)
Tengo un sistema (software) para mi agencia de viajes minorista. **Hoy soy Monotributista**, pero **a futuro puedo pasar a Responsable Inscripto** y el sistema tiene que aguantar ese cambio. Hoy **facturo solo en pesos**; quiero poder **facturar también en dólares** (y las notas de crédito/débito en consecuencia). Estas preguntas son para hacerlo bien fiscalmente.

---

## 1. ¿Qué cotización del dólar uso para facturar en dólares?
**Ejemplo:** vendo un paquete en USD 1.000. El día que facturo, el dólar está a $1.000 según un diario, a $1.020 según otro, a $980 el "mayorista". ¿Cuál pongo en la factura?

**Lo que averiguamos:** la RG 5616/2024 de ARCA diría que hay que usar el **dólar tipo VENDEDOR DIVISA del Banco Nación, del día hábil anterior** a la fecha de la factura.
**Pregunta concreta:** ¿es correcto? ¿"vendedor divisa" (no "billete")? ¿"día hábil anterior"? ¿Hay alguna prórroga vigente sobre la obligatoriedad de esto?
**Por qué importa:** el sistema tiene que traer ese número solito y dejarlo "congelado" en la factura. Si uso el dólar equivocado, la base en pesos queda mal.

## 2. ¿La nota de crédito usa el MISMO dólar que la factura, o el del día de la nota?
**Ejemplo:** facturé en USD cuando el dólar estaba $1.000. Dos semanas después anulo parte y hago una nota de crédito, pero ahora el dólar está $1.100. ¿La nota de crédito la hago con $1.000 (el de la factura) o con $1.100 (el de hoy)?

**Lo que averiguamos:** buscamos y **NO encontramos una norma de ARCA** que lo diga expresamente. Nuestro criterio (sin firma) es usar el **mismo dólar de la factura original**, porque la nota de crédito corrige esa operación, no es una nueva.
**Pregunta concreta:** ¿es correcto usar el dólar de la factura original? ¿Hay norma que lo respalde o es criterio profesional?

## 3. (IMPORTANTE para cuando sea RI) Cuando me quedo una penalidad/cargo en una cancelación, ¿lo facturo o solo "no lo devuelvo"?
**Ejemplo:** el cliente pagó $100.000, cancela, le devuelvo $70.000 y me quedo $30.000 "por la gestión / penalidad". ¿Esos $30.000 son una venta mía que tengo que facturar (con IVA si soy RI), o simplemente no se los devuelvo y listo?

**Lo que averiguamos:** depende de QUÉ es ese monto:
- Si es un **servicio mío** (cargo de gestión que me quedo) → probablemente **hay que facturarlo** (con IVA si soy RI).
- Si es una **indemnización pura** (castigo, sin servicio a cambio) → puede ir **sin IVA**.
- Si lo **retiene el operador mayorista** (no yo) → no es ingreso mío, no lo facturo.
**Pregunta concreta:** ¿cómo trato cada caso? ¿Hoy como Monotributo cambia algo (no discrimino IVA)? ¿Y cuando pase a RI?
**Por qué importa:** hoy el sistema "netea" (no factura) lo retenido. Si eso está mal para RI, hay que cambiarlo antes de pasar a RI.

## 4. (Para cuando sea RI) La diferencia de cambio entre la factura y el cobro/devolución, ¿cómo se trata?
**Ejemplo:** facturé en USD a dólar $1.000. El cliente me paga (en pesos) cuando el dólar está $1.050. Esa diferencia de $50 por dólar, ¿es una ganancia que tengo que declarar? ¿Toca el IVA?

**Lo que averiguamos:** sería un "resultado por diferencia de cambio". Como Monotributo **no impacta** (pagás cuota fija). Como RI, impactaría en Ganancias y **no debería** tocar el IVA (que ya quedó fijado en la factura).
**Pregunta concreta:** ¿es así? ¿Cómo lo registro contablemente cuando sea RI?

## 5. ¿En cuánto tiempo tengo que emitir una nota de crédito y desde cuándo se cuenta?
**Ejemplo:** un cliente cancela el 1° del mes, pero el operador mayorista me confirma la devolución recién el 20. ¿Los días para hacer la nota de crédito se cuentan desde el 1° o desde el 20?

**Lo que averiguamos:** la RG 4540 dice **15 días corridos** desde "el hecho o situación que requiere documentarlo". No define un menú; el "hecho" hay que poder justificarlo.
**Pregunta concreta:** en una cancelación de turismo, ¿qué momento cuenta como "el hecho" (cuando el cliente cancela, o cuando el operador confirma la devolución)? ¿Puedo elegir el del operador?

## 6. ¿Cuál es el tope de pago/devolución en efectivo hoy?
**Ejemplo:** le tengo que devolver $800.000 a un cliente. ¿Se los puedo dar en efectivo, o estoy obligado a transferencia/cheque por algún tope legal?

**Lo que averiguamos:** la Ley 25.345 fija un tope, pero el monto del texto original ($1.000) está congelado y el operativo lo fija ARCA por resolución.
**Pregunta concreta:** ¿cuál es el monto vigente hoy para pagos/devoluciones en efectivo? (lo dejo configurable en el sistema, necesito el número correcto).

## 7. El campo nuevo "CanMisMonExt" de la RG 5616 — ¿lo necesito?
**Ejemplo:** (técnico) cuando el sistema le manda una factura en dólares a ARCA, parece que ahora hay que avisarle si "cancelo en la misma moneda" (pago en dólares) o no.

**Lo que averiguamos:** la RG 5616/2024 sumó campos obligatorios para facturas en moneda extranjera; uno (`CondicionIVAReceptorId`) ya lo mandamos, pero `CanMisMonExt` todavía no está en el sistema.
**Pregunta concreta:** ¿desde cuándo es obligatorio (hubo prórrogas)? ¿Qué valor corresponde si facturo en USD pero el cliente me paga en pesos?

## 8. Pasar de Monotributo a Responsable Inscripto — ¿qué cambia en la facturación?
**Ejemplo:** hoy hago factura C. El día que me inscriba como RI, ¿empiezo a hacer factura A/B? ¿desde qué fecha exacta? ¿qué pasa con las facturas/notas de crédito de operaciones viejas (hechas cuando era Mono)?

**Pregunta concreta:** ¿cómo manejo la transición sin romper lo viejo? ¿Las notas de crédito de facturas C viejas se siguen haciendo como C aunque ya sea RI?

## 9. (Hoy, Monotributo) ¿Hay algo especial para facturar en dólares siendo Monotributista?
**Ejemplo:** soy monotributista y quiero facturar un paquete en USD. ¿Puedo? ¿Hay algún límite o requisito particular del monotributo para moneda extranjera?

**Pregunta concreta:** ¿algún recaudo especial siendo Monotributo para facturar en USD?

---

## Fuentes que usamos (para que las verifiques)
- RG 4540/2019 (notas de crédito): Boletín Oficial / Infoleg.
- RG 5616/2024 (facturación en moneda extranjera, tipo de cambio, campos nuevos): Boletín Oficial 18/12/2024.
- Ley 25.345 (tope efectivo): Infoleg.
- Ley 18.829 + Dictamen DAT 44/01 (base imponible agencias de viaje): Infoleg / Consejo.

> **Nota honesta:** todo esto lo verificamos con un asistente con acceso a internet contra fuentes oficiales, pero **no reemplaza tu firma profesional**. Estando en Monotributo el riesgo es bajo; las preguntas 3, 4 y 8 son sobre todo para **antes de pasar a Responsable Inscripto**.
