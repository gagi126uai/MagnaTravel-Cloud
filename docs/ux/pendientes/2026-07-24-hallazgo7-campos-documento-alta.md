---
título: Hallazgo #7 del barrido PROD — campos de documento en el alta de cliente y pasajero
estado: ESPERANDO RESPUESTA DE GASTÓN
origen: docs del barrido PROD 2026-07-22/23, hallazgo #7 ("Alta de cliente: campos 'Documento / Pasaporte'
        y 'CUIT / Documento' parecidos y confusos") + hallazgo #29 ("vencimiento de pasaporte" — decisión
        de agregarlo YA FIRMADA como campo opcional, no se re-pregunta)
---

# Hallazgo #7 — campos de documento del alta (cliente y pasajero)

## (a) Qué resuelve la guía sola (`docs/ux/guia-ux-gaston.md`)

La guía **no tiene ninguna sección específica sobre el alta de cliente ni sobre estos dos campos**
(busqué "CUIT", "Documento", "Pasaporte", "DNI", "alta de cliente" y no hay reglas puntuales). Pero sí
hay reglas generales que aplican y que uso para RECORTAR las opciones que le ofrezco a Gastón (no elijo
por él, pero descarto lo que ya está prohibido):

1. **"Basta de formularios aclarativos" (2026-06-05, Reglas generales).** Nada de cartelitos ni leyendas
   largas explicando un campo. Por eso NO le ofrezco como opción "dejar los dos campos con una frase
   explicando para qué sirve cada uno": esa opción ya está descartada por la guía.
2. **"Lo opcional no se decide solo" (2026-06-05, Reglas generales).** Qué campo es obligatorio y cuál no
   es una decisión de negocio (experto de dominio + Gastón), no mía. Por eso en este paquete **no toco ni
   pregunto** si el documento o el CUIT deben ser obligatorios — dejo la obligatoriedad como está hoy en
   el sistema (ninguno de los dos es obligatorio salvo Nombre y Condición AFIP) y me limito a cómo se ven
   y se organizan.
3. **"Más detalles" arranca CERRADO por defecto, en todas las fichas" (Ronda 7, 2026-06-06).** Si alguna
   opción manda un campo ahí adentro, tiene que respetar que arranca plegado.
4. **Vencimiento de pasaporte — YA DECIDIDO (2026-06-13, sección "Vencimientos").** "El vencimiento de
   pasaporte del pasajero SÍ se carga a mano, en los datos de cada pasajero" + tiene su propia sección en
   la campanita de avisos (aparte de "Próximos inicios" y "Costos a confirmar"). Esto **NO se re-pregunta**:
   va SIEMPRE en el pasajero (no en el cliente/titular de cuenta, que es una entidad distinta), a mano, y
   opcional. Lo único que falta definir es el detalle de layout (cuándo se ve el casillero y en qué parte
   del formulario) — eso sí se pregunta abajo, porque la guía no lo cubre.

**Importante — son DOS pantallas distintas del sistema**, revisé el código de ambas:
- **Alta de CLIENTE** (`CustomerFormModal.jsx`, el titular de la cuenta que se factura) — acá vive el
  problema textual del hallazgo #7: dos campos parecidos, **"Documento / Pasaporte"** (número suelto, sin
  decir de qué tipo) y **"CUIT / Documento"** (con buscador de AFIP), uno al lado del otro, ambas etiquetas
  repiten la palabra "Documento".
- **Alta de PASAJERO** (`PassengerFormModal.jsx`, quien viaja en una reserva) — este formulario **ya
  tiene un patrón más claro**: un desplegable "Tipo documento" (DNI / Pasaporte / Cédula / Otro) + un solo
  campo "Número de documento". No tiene el problema de nombres confusos, pero **no tiene el campo de
  vencimiento de pasaporte** (por eso el hallazgo #29 lo marca como faltante pese a estar decidido desde
  el 13/06).

## (b) y (c) Preguntas para Gastón, con mi recomendación

Ver el bloque de abajo — está listo para reenviar tal cual.

---

## PREGUNTAS PARA GASTON

### Tema: Alta de un CLIENTE nuevo — los dos campos de documento

Contexto: cuando cargás un cliente nuevo (el que se factura, no el pasajero que viaja), aparecen dos
casilleros pegados que dicen casi lo mismo: **"Documento / Pasaporte"** y **"CUIT / Documento"**. En el
barrido de la semana pasada quedó anotado que confunden — no se entiende cuál llenar, ni por qué hay dos.

**P1. ¿Cómo dejamos estos dos casilleros en el alta de cliente?**

  A) **Uno solo, con un desplegable de tipo — igual al que ya usás en pasajero.** Elegís el tipo (CUIT,
     CUIL, DNI, Pasaporte, Otro) y al lado escribís el número. La lupita para buscar en AFIP aparece solo
     cuando el tipo es CUIT, CUIL o DNI (a pasaporte no lo tiene AFIP). **[RECOMENDADO — mismo patrón que
     ya funciona en pasajero, menos casilleros, cero ambigüedad de cuál llenar]**
     ```
     Nombre Completo *
     [________________________________]

     Tipo de documento          Número
     [CUIT/CUIL/DNI/Pasaporte▾] [______________] 🔍
     ```

  B) **Se quedan los dos casilleros, pero renombrados para que se entienda cada uno de un vistazo,** sin
     agregar texto explicativo (solo el nombre del campo cambia):
     ```
     Nombre Completo *
     [________________________________]

     CUIT / CUIL                DNI o Pasaporte
     [______________] 🔍         [______________]
     ```

  C) **Un solo casillero arriba (el que sirve para facturar: CUIT/CUIL/DNI con lupita AFIP), y el
     pasaporte se esconde detrás de "Más detalles"** (como ya se esconden otros campos secundarios en
     otras pantallas):
     ```
     Nombre Completo *
     [________________________________]

     CUIT / CUIL / DNI
     [______________] 🔍

     ▸ Más detalles
     ```

---

### Tema: Vencimiento de pasaporte en el alta de PASAJERO

Contexto: ya quedó decidido (13/06) que se agrega el vencimiento de pasaporte del pasajero, cargado a
mano y opcional — **esto no se vuelve a preguntar**. Lo que falta es dónde y cuándo se ve el casillero
dentro del formulario de pasajero, que hoy tiene: Tipo documento + Número (sección "Identidad"), Fecha de
nacimiento + Nacionalidad + Género (sección "Datos personales"), Teléfono + Email (sección "Contacto").

**P2. ¿El casillero de vencimiento de pasaporte se ve siempre, o solo cuando el tipo de documento
elegido es "Pasaporte"?**

  A) **Siempre visible**, sin importar qué tipo de documento eligió el vendedor — porque mucha gente
     viaja por Argentina con el DNI pero igual tiene pasaporte vigente para el resto de los viajes, y
     conviene poder cargarlo aunque el documento principal sea otro. **[RECOMENDADO]**
     ```
     Tipo documento     Número
     [DNI▾]             [______________]

     Fecha nacimiento   Nacionalidad      Vencimiento pasaporte
     [__/__/____]        [___________]    [__/__/____]  (opcional)
     ```

  B) **Solo aparece si elegís "Pasaporte" como tipo de documento** — el resto del tiempo el casillero
     está escondido.
     ```
     Tipo documento     Número
     [Pasaporte▾]       [______________]

     Fecha nacimiento   Nacionalidad      Vencimiento pasaporte
     [__/__/____]        [___________]    [__/__/____]  (opcional)

     (si el tipo NO es Pasaporte, este último casillero no se muestra)
     ```

**P3. ¿En qué sección del formulario va el casillero?**

  A) **En "Datos personales"**, junto a Fecha de nacimiento y Nacionalidad (como en los mockups de
     arriba) — porque es un dato de la persona, no del documento en sí. **[RECOMENDADO]**

  B) **En "Identidad"**, pegado al Tipo/Número de documento — porque es un dato específico DEL
     documento pasaporte, no de la persona en general.
     ```
     Tipo documento     Número            Vencimiento pasaporte
     [DNI▾]             [______________]  [__/__/____]  (opcional)
     ```

---

## Especificación (a completar tras las respuestas de Gastón)

Este archivo se actualiza con el layout final, el orden de campos, y el detalle campo-por-campo apenas
Gastón responda P1, P2 y P3. Mientras tanto, `frontend-senior` NO debe implementar nada de esto — sigue
el gate obligatorio de UX.
