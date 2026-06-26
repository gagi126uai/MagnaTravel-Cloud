# Pantallas de la Feature A — Cuenta corriente del cliente (UX)

Fecha: 2026-06-26
Autor: ux-ui-disenador
Estado: BORRADOR DE TRABAJO. Lo que está "Cubierto por la guía" es spec firme; lo que está en
"PREGUNTAS PARA GASTON" NO se construye hasta que Gastón responda.

> Método: para cada una de las 5 pantallas, primero lo que YA está decidido (citando la guía
> `docs/ux/guia-ux-gaston.md` o el diseño técnico `docs/ux/diseno-cuenta-corriente-y-descuento.md`),
> después lo que falta decidir (preguntas con opciones, recomendación única y dibujito).

---

## Decisiones del dueño que se dan por CERRADAS (no se re-preguntan)

Vienen del encargo y del diseño técnico Feature A. Se usan tal cual:

1. El **modo de cobro es POR CLIENTE** (prepago / cuenta corriente), con un **default que pone la agencia**.
2. **Límite de crédito POR MONEDA** (pesos y dólares por separado). _(Ojo: el diseño técnico recomendaba
   límite solo en pesos —su Pregunta 3, opción A—; Gastón lo resolvió por moneda. Vale la decisión de
   Gastón. No es contradicción de la guía: la guía todavía no cubría esto.)_
3. **Plazo de pago en días** (vencimiento de la cuenta).
4. Si el cliente a cuenta se pasa del límite o está en mora: al **dejarlo viajar FRENA** (configurable
   a "solo avisa" por agencia); al **confirmar / facturar solo AVISA**.

Quedan FUERA de UX (son negocio/contable, los define dominio/contador, no se diseñan acá):
cerrar el viaje con deuda (P5 técnica), desde cuándo cuentan los días de plazo (P6 técnica),
comisión del vendedor en ventas a cuenta (P7 técnica), reconocimiento de ingreso (C-1).

---

## Reglas de la guía que mandan en TODAS estas pantallas (firmes)

- **Multimoneda, 3 reglas duras (2026-06-09):** las monedas van SIEMPRE separadas, NUNCA se suman ni se
  convierten; NUNCA aparece "diferencia de cambio"; si hay UNA sola moneda se ve EXACTAMENTE como hoy.
- **Cuenta corriente del cliente ya APROBADA (2026-06-09 P5 + 2026-06-10):** dos saldos arriba
  ("Debe en $" / "Debe en US$") + una sola lista de movimientos con la moneda en cada fila.
- **Chips de plata = secundarios (ADR-035 A-quinque, 2026-06-19):** más chicos que el badge de estado,
  con prefijo gris "Pago:", para que NO parezcan un segundo estado operativo.
- **Un solo cartel arriba para explicar el estado (ADR-035 A):** nada de un mensajito por cada botón.
- **Avisos que no frenan = franja amarilla (2026-06-13, "factura desde servicios"):** se factura igual,
  la franja solo advierte.
- **Sin permiso `cobranzas.see_cost` no se ven montos de costo/deuda al operador (2026-06-05).** Lo que el
  cliente debe SÍ se ve sin ese permiso (no es costo).
- **Formularios sin aclaraciones (2026-06-05):** nada de leyendas largas ni "(opcional)"; lo secundario
  detrás de "Más detalles". Y **"no asumas nada que no te pida" (Ronda 7, 2026-06-06).**
- **Botón ya cumplido se esconde / bloqueado va gris (Fase 4, 2026-06-26).**

---

# Pantalla 1 — Ficha del cliente (modo de cobro + límite + plazo)

**Hoy:** la ficha del cliente es un formulario de alta/edición. La cuenta corriente del cliente
(`CustomerAccountPage.jsx`) ya muestra una tarjeta "Límite credito" (campo `CreditLimit`, hoy muerto).

**Cubierto por la guía:**
- Formulario sin aclaraciones, lo secundario detrás de "Más detalles" (2026-06-05).
- Multimoneda: los dos límites (pesos / dólares) van separados, nunca mezclados (2026-06-09).

**Falta decidir (CC1, CC2).**

---

# Pantalla 2 — Configuración de la agencia (default + frenar/avisar)

**Cubierto por la guía:**
- **Dónde vive:** Configuración → **"Operativa, Cobranzas y Facturación"** (`OperationalFinanceSettingsTab.jsx`).
  Es el lugar establecido para este tipo de llaves: ahí ya viven "Alertas por reservas próximas con
  deuda" + "Días previos para alertar" (ADR-036 P5=B) y la caducidad de presupuestos (G6, 2026-06-24).
  El bloque nuevo va como un casillero/selector más, con el mismo aspecto.
- La llave del tarifario se prende DIRECTO, sin ventanita; el "Guardar configuración" sigue siendo el
  paso final (Ronda 6, 2026-06-06).

**Falta decidir (CC3, CC4): los textos exactos de los dos controles nuevos.**

---

# Pantalla 3 — Aviso / bloqueo al viajar, y aviso al confirmar/facturar

**Cubierto por la guía (mucho):**
- El patrón "**Debe — no viaja**" ya existe para el prepago: **cartel chico arriba** + **chip de pago rojo**
  con prefijo "Pago:" en gris (ADR-036 punto 7 + ADR-037 punto 3). La cuenta corriente reusa EXACTAMENTE
  este patrón visual; solo cambia el texto (no es "debe todo", es "se pasó del límite" o "está en mora").
- El cartel y el chip **no muestran montos de costo ni deuda al operador**; sí pueden mostrar lo que el
  cliente debe (ADR-036 punto 7).
- Aviso que NO frena = **franja amarilla** (2026-06-13).
- Un solo cartel arriba, nunca mensajito por botón (ADR-035 A).

**Falta decidir (CC5, CC6, CC7): los textos, y cómo se ve la variante "solo avisar".**

---

# Pantalla 4 — Cuenta corriente del cliente con vencimientos (aging)

**Cubierto por la guía:**
- La **cuenta corriente del cliente ya existe** (`CustomerAccountPage.jsx`): cabecera con datos del
  cliente + tarjeta de saldo + tarjeta "Límite credito" + solapas Reservas / Pagos / Facturación, y los
  carteles "A FAVOR EN $/US$" reutilizables.
- Layout multimoneda aprobado (2026-06-09 P5 + 2026-06-10): dos saldos arriba + lista única con moneda por
  fila. El aging respeta esto: **un bloque de vencimientos por cada moneda, nunca mezclados.**
- **NO contradice** la regla "vencimientos = NO" del 2026-06-22: aquella era para el **Estado de Cuenta POR
  RESERVA** (el extracto de una reserva). Esto es a nivel **CLIENTE** (toda su cartera), donde el aging sí
  tiene sentido. Son dos pantallas distintas.

**Decisión de encargo (la resuelvo con recomendación, va en CC8):** esto NO es una pantalla nueva; es una
**extensión de la Cuenta Corriente del cliente que ya existe** (ahí ya están el saldo y el límite).

**Falta decidir (CC8, CC9): cómo se dibuja el aging y para qué clientes aparece.**

---

# Pantalla 5 — Dónde se ve "en mora"

**Cubierto por la guía:**
- El patrón de chip de plata secundario con prefijo "Pago:" gris (ADR-035 A-quinque). "En mora" es estado
  de PAGO del cliente, así que usa ese mismo tratamiento.

**Falta decidir (CC10): en qué pantallas aparece la marca "En mora".**

---

# PREGUNTAS PARA GASTON

> (Estas preguntas están listas para reenviarse tal cual. Cada una tiene una recomendación marcada con ←.)

### Tema: Ficha del cliente — modo de cobro, límite y plazo

**CC1. Cuando un cliente es "Prepago", ¿Límite y Plazo se esconden, o se ven apagados?**
Contexto: el cliente prepago paga antes de viajar, así que no tiene ni límite ni plazo. La pregunta es si
esos dos casilleros desaparecen o quedan a la vista pero apagados.

  A) **Si elegís "Prepago", Límite y Plazo DESAPARECEN; si elegís "Cuenta corriente", aparecen.** ←
     (recomendada: no mostramos campos que no aplican)
```
 Forma de cobro:  (•) Prepago     ( ) Cuenta corriente
 ───────────────────────────────────────────────────────
 (no se ve nada más: prepago no tiene límite ni plazo)
```
```
 Forma de cobro:  ( ) Prepago     (•) Cuenta corriente
 ───────────────────────────────────────────────────────
 Límite en $   [ 500.000 ]      Límite en US$ [ 2.000 ]
 Plazo de pago [ 30 ] días
```
  B) Se ven SIEMPRE los tres, pero en Prepago, Límite y Plazo quedan grises (apagados).
```
 Forma de cobro:  (•) Prepago     ( ) Cuenta corriente
 Límite en $   [   0   ] (gris)  Límite en US$ [  0  ] (gris)
 Plazo de pago [  0 ] días (gris)
```

**CC2. ¿Cómo cargás los dos límites, y qué significa dejar uno en 0?**
Contexto: decidiste límite por moneda (pesos y dólares por separado). Falta el dibujo fino.

  A) **Dos casilleros lado a lado ("Límite en $" y "Límite en US$"); 0 = "no le fío en esa moneda"
     (tiene que pagar todo en esa moneda antes de viajar).** ←
```
 Límite en $   [ 500.000 ]      Límite en US$ [ 2.000 ]
 (0 en una moneda = en esa moneda paga todo antes de viajar)
```
  B) Un solo casillero de límite y al lado un selector de moneda (un límite por vez).
```
 Límite [ 500.000 ]  en  [ $ ▾ ]
```

---

### Tema: Configuración de la agencia

**CC3. El texto del default "forma de cobro" para clientes nuevos.**
Contexto: va en Configuración → "Operativa, Cobranzas y Facturación", como un bloque más.

  A) **"Los clientes nuevos nacen como:  (•) Prepago   ( ) Cuenta corriente"** (default Prepago, como hoy) ←
```
 ┌─ Cuenta corriente de clientes ──────────────────────┐
 │ Los clientes nuevos nacen como:                      │
 │   (•) Prepago      ( ) Cuenta corriente              │
 └──────────────────────────────────────────────────────┘
```
  B) Otro texto (contame cuál).

**CC4. La llave "frenar / solo avisar" cuando el cliente a cuenta se pasa del límite o está en mora.**
Contexto: definiste que al dejarlo VIAJAR puede frenar o solo avisar, configurable por agencia.

  A) **Un selector: "Si un cliente a cuenta supera el límite o está en mora, al dejarlo viajar:
     (•) Frenar hasta resolver   ( ) Solo avisar y dejar pasar"** (default Frenar) ←
```
 ┌─ Cuenta corriente de clientes ──────────────────────┐
 │ Los clientes nuevos nacen como: (•) Prepago ( ) Cta cte │
 │                                                      │
 │ Si un cliente a cuenta supera el límite o está en    │
 │ mora, al dejarlo viajar:                             │
 │   (•) Frenar hasta resolver                          │
 │   ( ) Solo avisar y dejar pasar                      │
 └──────────────────────────────────────────────────────┘
```
  B) Otro texto / otra forma (contame).

---

### Tema: El aviso al viajar y al facturar

**CC5. El cartel + chip cuando FRENA (modo "frenar"). Hay dos motivos: pasó el límite, o está en mora.**
Contexto: reuso el mismo lugar y estilo del cartel rojo "Debe — no viaja" que ya existe para el prepago.

  A) **Cartel rojo chico arriba + chip rojo "Pago:", con texto según el motivo:** ←
```
 (supera el límite)
 🔴 No puede viajar: el cliente superó su límite de crédito.
    Cobrá una parte o ampliá el límite para que pueda viajar.
   ...  [Estado: CONFIRMADA]   Pago: Supera el límite (chip rojo)

 (en mora)
 🔴 No puede viajar: el cliente tiene saldo vencido (en mora).
    Regularizá la cuenta para que pueda viajar.
   ...  [Estado: CONFIRMADA]   Pago: En mora (chip rojo)
```
  B) Otros textos (contame).

**CC6. Cuando la agencia eligió "solo avisar": ¿se ve el mismo cartel pero deja pasar?**
Contexto: si la agencia confía en su cartera, el cliente viaja igual, pero conviene que quede el aviso.

  A) **Mismo cartel pero en ÁMBAR (no rojo) y NO frena; el chip también ámbar.** ←
```
 🟡 Atención: el cliente superó el límite (o está en mora), pero la
    configuración permite que viaje igual.
   ...  [Estado: EN VIAJE]   Pago: Supera el límite (chip ámbar)
```
  B) En "solo avisar" no se muestra nada (viaja como un cliente al día).

**CC7. El aviso al CONFIRMAR o FACTURAR (este NUNCA frena, solo avisa).**
Contexto: definiste que al confirmar/facturar solo avisa. Al facturar ya existe un cartel "¿seguro?"; acá
le sumamos una franja amarilla de aviso.

  A) **Franja amarilla dentro de la confirmación, no frena:
     "El cliente supera su límite de crédito / está en mora. Podés facturar igual."** ←
```
 ┌─ ¿Emitir factura? ──────────────────────────────────┐
 │ 🟡 El cliente supera su límite de crédito.           │
 │    Podés facturar igual.                             │
 │ Facturás a: Viajes ACME S.A.  ·  Total: $ 480.000    │
 │                       [ Volver ]   [ Sí, emitir ]    │
 └──────────────────────────────────────────────────────┘
```
  B) Otro texto / otro lugar (contame).

---

### Tema: La cuenta corriente del cliente con vencimientos (aging)

**CC8. ¿Cómo se ve "lo que te debe, por vencer y vencido", dentro de la cuenta corriente del cliente?**
Contexto: esto es una EXTENSIÓN de la pantalla de Cuenta Corriente del cliente que ya existe (no una
pantalla nueva). Va separado por moneda (nunca mezclado). Los tramos: por vencer / vencido 1-30 /
31-60 / 61-90 / +90 días.

  A) **Una franja de "Vencimientos" arriba de las solapas, una fila por moneda:** ←
```
 CUENTA CORRIENTE — Viajes ACME S.A.
 Debe en $: 480.000        Debe en US$: 1.200

 ┌─ Vencimientos ───────────────────────────────────────────────┐
 │        Por vencer  1-30   31-60   61-90   +90                 │
 │  $       120.000  200.000  90.000  70.000    0                │
 │  US$         800     400        0       0     0               │
 └───────────────────────────────────────────────────────────────┘

 [ Reservas ] [ Pagos ] [ Facturación ]   (las solapas de hoy)
```
  B) Una solapa nueva "Vencimientos" (al lado de Reservas / Pagos / Facturación), con la misma tabla
     por moneda adentro.
```
 [ Reservas ] [ Pagos ] [ Facturación ] [ Vencimientos ]
                                          └─ tabla por moneda (igual que A)
```

**CC9. ¿La franja de vencimientos aparece para todos los clientes, o solo los de cuenta corriente?**
Contexto: un cliente prepago paga antes de viajar, así que nunca tiene saldo vencido.

  A) **Solo para clientes en modo "Cuenta corriente". En prepago, la franja no aparece.** ←
  B) Para todos (en prepago se vería siempre vacía).

---

### Tema: Dónde se ve "En mora"

**CC10. ¿En qué pantallas se muestra la marca "En mora" del cliente?**
Contexto: "en mora" = el cliente tiene saldo vencido. Es un estado del CLIENTE (no de una reserva suelta).

  A) **En el listado de clientes (una marca roja al lado del saldo) + en la cabecera de su cuenta
     corriente. En la reserva ya está el chip "Pago: En mora" del Tema anterior.** ←
```
 LISTADO DE CLIENTES
 Cliente              Saldo actual        Estado
 Viajes ACME S.A.     $ 480.000  🔴 En mora   Activo
 Fam. García          $ 0                      Activo
```
```
 CUENTA CORRIENTE — Viajes ACME S.A.   🔴 En mora
 Debe en $: 480.000     Debe en US$: 1.200
```
  B) Solo en la cuenta corriente del cliente (no en el listado).
  C) También en cada reserva del cliente (un chip "Pago: En mora" aunque esa reserva puntual esté al día).

---

# Qué NO hay que hacer (recordatorios para el implementador)

- NO sumar ni convertir monedas en ninguna de estas pantallas; nunca la palabra "diferencia de cambio".
- NO mostrar montos de costo ni deuda al operador sin permiso `cobranzas.see_cost` (lo que el cliente debe
  sí se ve).
- NO usar ventana flotante para nada de esto (todo en línea / en la pantalla, regla dura de siempre).
- NO poner un mensajito de motivo por cada botón: un solo cartel arriba.
- NO construir ninguna de las 10 piezas marcadas como pregunta hasta que Gastón responda.
