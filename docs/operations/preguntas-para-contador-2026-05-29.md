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

---

## ✅ RESPUESTAS DEL CONTADOR MATRICULADO (2026-06-01)

> Respondidas por un contador **matriculado** (confirmado por Gastón). Esto pasa de "borrador a confirmar" a **criterio profesional firmado**.

1. **Cotización para facturar en USD:** correcto lo de la resolución → **vendedor divisa BNA, día hábil anterior** (RG 5616). Confirmado.

2. **Dólar de la NC:** el **mismo de la factura original** (el del día que se facturó). Confirmado el criterio que ya teníamos.

3. **Penalidad/cargo en una cancelación → CAMBIO DE MODELO IMPORTANTE.** No se hace una NC parcial. Lo correcto es: **Nota de Crédito por el TOTAL** (se anula la factura entera) **+ Nota de Débito por la penalidad** que exista. El sistema de penalizaciones depende de la **política de cada proveedor/operador**. Refleja la cadena: el operador mayorista, que también le facturó al minorista, le hace al minorista una **NC por el total + una ND por el cargo de anulación** que le cobra; el minorista hace lo mismo con su cliente.
   - **Implica:** enterrar el enfoque de **NC parcial** (FC1.3 fase 2, flag `EnablePartialCreditNoteRealEmission`, hoy OFF) y construir **"NC total + ND por penalidad"**. El sistema HOY ya emite NC total; falta agregar la **emisión de Nota de Débito** por la penalidad.

4. **Diferencia de cambio (factura USD vs cobro/devolución):** **se factura la diferencia en dólares** (factura nueva), porque el cliente va a pedir una factura por el cambio de cotización. NO es solo un resultado contable: genera comprobante.

5. **Momento que cuenta como "el hecho" para el plazo de la NC:** **acuerdo entre operador y minorista**. En la práctica AFIP no tiene cómo determinar el momento exacto; se documenta lo acordado.

6. **Tope de efectivo / medio de devolución:** **NO se hardcodea un tope.** Como es un sistema que se vende a distintas agencias, el límite y el medio (efectivo o transferencia) quedan a **criterio humano del cliente que usa el sistema**. Basta con que el sistema **registre cómo se hizo** (medio + moneda + TC). Principio general (vale para varias respuestas): la moneda y el TC de la devolución son **los mismos con los que se pagó**, avalados por el TC del mayorista; todo se alinea según la política del mayorista y de la minorista (suelen acordarse entre ellos).

7. **Campo `CanMisMonExt`:** **sí**, se necesita. (Ya implementado, commit `614d7d7`.)

8. **Transición Mono → RI → REGLA DURA.** Desde el momento en que se es RI, **todo se trata como RI**. Las NC y ND de operaciones viejas (facturadas en C cuando era Mono) pasan a ser **tipo B**. **Un RI NO puede emitir factura C, ni NC C, ni ND C.** El tipo de comprobante lo manda la condición fiscal **actual** del emisor, no la de la operación original.

9. **Facturar USD siendo Monotributo:** **no** hay límites ni requisitos especiales. Lo único es la **recategorización** normal del monotributo si se supera el nivel de ventas (tema de AFIP, no del sistema).

### Qué desbloquea / qué implica construir
- **Multimoneda (1, 2, 7):** confirmado el criterio → desbloquea homologación ARCA + NC/ND total USD heredando moneda/TC.
- **Cancelación (3) — el más grande:** revisar si se entierra el NC parcial y se construye **NC total + ND por penalidad**. Requiere análisis del contador-integrado (impacto sobre `BookingCancellationService` / `AfipService` / `FiscalLiquidation`).
- **Diferencia de cambio (4):** nueva pieza a futuro (RI) → factura por la diferencia de cambio.
- **Devoluciones (6):** registrar medio + moneda + TC; sin tope hardcodeado.
- **Mono→RI (8):** el tipo de comprobante (incluida NC/ND sobre facturas viejas) se deriva de la condición fiscal **vigente** del emisor. Verificar que el sistema lo resuelva así.

### Decisión de negocio de Gastón (2026-06-01) sobre la penalidad
- **¿Quién se queda la penalidad? → "DEPENDE DEL OPERADOR".** El sistema debe soportar AMBOS modos (consistente con la decisión previa "facturación configurable por operador"):
  - **Operador retiene** → para el minorista es **pass-through** (NO ingreso propio). Cuidado fiscal: no documentarla como venta del minorista.
  - **Minorista se la queda** → **ingreso propio** → NC total + ND (con IVA si RI).
- Esto deja abierto el **tratamiento fiscal del caso pass-through**, que requiere firma del matriculado. → ver `docs/operations/preguntas-contador-round2-cancelacion-2026-06-01.md`.
