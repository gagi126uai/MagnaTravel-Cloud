# Deshacer una multa YA emitida y aprobada por ARCA (2026-07-14)

> **Origen:** una reserva anulada tiene la multa del operador cobrada al cliente con un
> comprobante ya aprobado por ARCA... y estaba mal (monto o moneda equivocados, o no
> correspondía). Un comprobante aprobado no se edita: se emite el comprobante inverso
> (lo deja sin efecto), la plata del cliente se acomoda sola, y el paso de la multa
> vuelve a quedar abierto en la ficha para corregir y volver a cobrar, o cerrar sin
> multa.
>
> **Es un caso DISTINTO del "Deshacer" que ya existe.** Hoy ya hay un "Deshacer" en la
> ficha, pero es para el cierre **"sin multa"** (`Waived` — nunca se emitió ningún
> comprobante). Esto de acá es para deshacer una multa que **sí se cobró y sí tiene un
> comprobante aprobado** (familia `confirmada` / `Done`, ADR-044 T4). Mismo espíritu,
> mismo patrón visual, pero es una acción fiscal más grande (reversa un comprobante con
> CAE), así que el modal de confirmación es más explícito.
>
> Reusa sin inventar: el cartel de multa resuelta y su link "+ Agregar otro cargo de
> este operador" (`OperatorPenaltyStepPanel.jsx`, familia `confirmada`), el patrón del
> link "Deshacer" del cierre sin multa (`ReservaDetailPage.jsx`, línea ~1449), el panel
> `DeshacerCierreSinMultaInline.jsx` (motivo obligatorio + confirmación explícita en dos
> pasos), el rastro `textoRastroWaived` (`operatorPenaltyBanner.js`), las familias
> `procesando`/`accionTrabada` ya definidas para la Nota de Débito, y las 8 reglas de voz
> (2026-07-08). Todo **EN LÍNEA, nunca ventana flotante** (regla dura de siempre).

---

## 1. La entrada: link "Deshacer" en el cartel de multa RESUELTA

**Forma:** link discreto (no botón), mismo patrón visual que el "Deshacer" del cierre
sin multa: texto chico, sin fondo, con un guioncito "·" adelante, separado del resto
por una línea fina arriba (`border-t`). Nunca un botón grande — es una acción rara y de
último recurso, no una acción de todos los días (regla: "link discreto vs botón" para
lo destructivo/poco frecuente).

**Quién lo ve:** el mismo criterio que ya usa el "Deshacer" de cierre sin multa —
**solo Administrador**. Es más consecuente que reabrir un paso que nunca tuvo
comprobante (esto reversa un comprobante YA aprobado por ARCA), así que aplica, como
mínimo, el mismo límite que ya existe para lo menos grave. (Ver pregunta P1 más abajo
por si Gastón quiere ampliarlo.)

**Ubicación:** dentro del cartel verde "multa del operador confirmada", debajo del
monto y (si los hay) del desglose de cargos, en su propio bloque separado por línea
fina — al mismo nivel que "+ Agregar otro cargo de este operador", pero como una
segunda fila aparte (no se mezclan en el mismo renglón).

### Caso simple — UN solo cargo confirmado (el 99% de los casos)

```
┌──────────────────────────────────────────────────────────────┐
│ Anulada — multa del operador confirmada.        US$ 200        │
│ ──────────────────────────────────────────────────────────── │
│ + Agregar otro cargo de este operador                          │
│ ──────────────────────────────────────────────────────────── │
│ · Deshacer: el operador cobró mal esta multa                   │
└──────────────────────────────────────────────────────────────┘
```

### Caso con MÁS de un cargo (fee + retención, el caso del contador — ADR-044 T4)

Acá aparece el desglose (`DesgloseCargosOperador`, ya existe). El link "Deshacer" es
**UNO solo, al pie del cartel** (nunca uno por renglón): deshacer deja sin efecto el
**comprobante entero**, con TODOS los cargos que salieron en él. No se puede deshacer un
cargo suelto (ver "P1 — resuelta por regla fiscal" más abajo). Después de deshecho, cada
cargo se corrige/edita por separado y se vuelve a emitir.

```
┌──────────────────────────────────────────────────────────────┐
│ Anulada — multa del operador confirmada.                       │
│ ──────────────────────────────────────────────────────────── │
│ Cargo administrativo · descontado     US$ 120                  │
│ Retención fiscal · facturado aparte   US$  80                  │
│ ──────────────────────────────────────────────────────────── │
│ + Agregar otro cargo de este operador                          │
│ ──────────────────────────────────────────────────────────── │
│ · Deshacer: el operador cobró mal esta multa                   │
└──────────────────────────────────────────────────────────────┘
```

---

## 2. La confirmación: panel en línea con motivo obligatorio

Se abre **debajo del cartel** (nunca ventana flotante), mismo patrón de dos pasos que
`DeshacerCierreSinMultaInline`: **paso 1** explica y pide el motivo, **paso 2** confirma
explícitamente antes de tocar el backend.

### Paso 1 — explicación + motivo

```
┌──────────────────────────────────────────────────────────────┐
│ ↺  Deshacer el comprobante de la multa      Reserva #F-1042 ✕ │
│                                                                  │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ Se deja sin efecto el comprobante completo, con todos los │ │
│ │ cargos que salieron en él. La deuda de US$ 200 desaparece  │ │
│ │ sola.                                                        │ │
│ │                                                              │ │
│ │ Después podés corregir cada cargo (monto o moneda) y        │ │
│ │ volver a cobrarla, o cerrar el paso sin multa.              │ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                                  │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ ⚠ Quedan 6 días para hacer esta corrección sin trámites   │ │
│ │   extra ante ARCA (vence el 20/07).                        │ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                                  │
│ ¿Por qué la deshacés? *                                        │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ El operador cobró la multa en pesos, no en dólares...      │ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                     0/500       │
│                                                                  │
│                                       [ Volver ]  [ Deshacer ] │
└──────────────────────────────────────────────────────────────┘
```

**Variante — el cliente YA le había pagado esta multa a la agencia:**

```
│ Se emite el comprobante que deja sin efecto la multa actual.  │
│ Como ya te había pagado, le va a quedar US$ 200 a favor para  │
│ usar en otra reserva.                                          │
```

**Variante — sin permiso para ver montos** (`cobranzas.see_cost`): igual que en el
resto de la ficha, el monto se tapa con "—" (nunca desaparece la frase, solo el número):
"Se emite el comprobante que deja sin efecto la multa actual. El monto queda oculto —
no tenés permiso para ver costos."

### Paso 2 — confirmación explícita (mismo patrón que "Sí, reabrir")

```
┌──────────────────────────────────────────────────────────────┐
│ ↺  Deshacer el comprobante de la multa      Reserva #F-1042 ✕ │
│                                                                  │
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ Esto deja sin efecto el comprobante de la multa de la      │ │
│ │ reserva F-1042. Se va a emitir uno nuevo que lo anula;      │ │
│ │ después vas a poder corregirla y volver a cobrarla, o        │ │
│ │ cerrarla sin multa.                                          │ │
│ └──────────────────────────────────────────────────────────┘ │
│                                                                  │
│                                    [ Volver ]  [ Sí, deshacer ]│
└──────────────────────────────────────────────────────────────┘
```

Botón "Deshacer"/"Sí, deshacer" apagado mientras el motivo no cumple el mínimo
(5..500 caracteres, mismo límite que el panel existente). Si falla el pedido, el panel
queda abierto con el motivo intacto + cartel rojo de error (mismo patrón que todos los
paneles inline de la ficha).

---

## 3. Los estados nuevos del cartel

### (a) "Deshaciendo…" — familia `procesando`, mientras espera a ARCA

Mismo color y mecánica que "se está emitiendo la multa" (ámbar, se refresca solo cada
~10 s, sin que el vendedor tenga que hacer nada):

```
┌──────────────────────────────────────────────────────────────┐
│ ⏳ Anulada — se está dejando sin efecto la multa.               │
│    Puede demorar unos minutos, no hace falta que hagas nada.    │
└──────────────────────────────────────────────────────────────┘
```

Si se pasa el tiempo prudente de espera (mismo tope que ya existe, ~3 min), aparece la
misma línea chica de siempre:

```
│    ¿Tarda mucho? Actualizá la página.                            │
```

### (b) "No se pudo deshacer" — familia `accionTrabada`, con Reintentar

Mismo naranja y mismo patrón de un botón puntual que el resto de `accionTrabada`:

```
┌──────────────────────────────────────────────────────────────┐
│ Anulada — no se pudo dejar sin efecto la multa.                │
│ Probá de nuevo.                                  [ Reintentar ]│
└──────────────────────────────────────────────────────────────┘
```

Al resolverse (para cualquier lado: éxito o falla), el panel de motivo/confirmación ya
se cerró — estos dos cartelitos reemplazan al cartel de multa confirmada mientras dura
el trámite, igual que hoy pasa con la emisión.

---

## 4. El rastro

Después de deshecha y vuelta a resolver (con multa corregida, o cerrada sin multa),
aparece una línea chica debajo del cartel nuevo — mismo estilo que
`multa-waived-rastro` (texto chico, color apagado del cartel que corresponda):

```
El comprobante anterior se dejó sin efecto el 14/07 por Ana — motivo: "el operador
cobró la multa en pesos, no en dólares."
```

Reglas del texto (mismo criterio que `textoRastroWaived`):
- Si no viene quién lo hizo, cae a: `El comprobante anterior se dejó sin efecto el 14/07.`
- Si no viene el motivo, no se inventa nada: se corta después de la fecha (o de "por Ana").
- Se muestra **también** mientras el paso queda reabierto en "pregunta" (¿el operador
  cobró...? Sí/No), para que quien decide de nuevo tenga el contexto de por qué se
  volvió a abrir.
- Si esto pasa más de una vez, se muestra solo el ÚLTIMO deshacer (mismo criterio que
  ya usa `textoRastroWaived` con el revert de "sin multa" — no se acumula historial
  completo en el cartel).

---

## 5. Aviso de plazo (15 días de ARCA)

Aviso **suave, nunca bloquea** — mismo espíritu que el aviso de "el dólar está muy
lejos del oficial" (2026-07-13): informa, no frena. Vive dentro del panel de
confirmación (paso 1), entre la explicación y el campo de motivo.

- **Si todavía quedan días:** `⚠ Quedan {N} días para hacer esta corrección sin
  trámites extra ante ARCA (vence el {fecha}).`
- **Si ya se pasaron los 15 días** (texto exacto elegido por Gastón, 2026-07-14):
  `⚠ Pasaron más de 15 días desde que se emitió este comprobante. Se puede deshacer
  igual, pero convendría consultarlo con un contador antes de seguir.` (tono más fuerte
  porque el riesgo es mayor, pero **sigue sin bloquear** el botón "Deshacer").
- **Si no corresponde** (la multa se emitió hace muy poco, lejos del límite, o el
  backend no informa el dato todavía): el aviso no aparece, nada ocupa ese lugar.

**Dependencia de backend (no es diseño):** el frontend necesita que le llegue, para el
comprobante a deshacer, la **fecha en que se emitió** (para calcular los días) o
directamente los **días restantes** ya calculados — se recomienda que el backend mande
el número ya calculado (mismo criterio que "el front no deduce, lo dice el backend",
2026-07-03), para no repetir la regla de los 15 días en dos lugares.

---

## Decisiones cerradas (ya no hay preguntas abiertas)

### P1 — RESUELTA POR REGLA FISCAL FIRMADA (no la decide Gastón)

**"Deshacer" deja sin efecto el COMPROBANTE ENTERO, con todos los cargos que viajaron en
él — nunca un cargo suelto.** Lo fija la regla dura del contador (spec fiscal firmada,
regla dura 3): la nota de crédito anula el 100% del comprobante, nunca en forma parcial.
Por eso:
- El link "Deshacer" es **UNO solo por comprobante emitido**, al pie del cartel (nunca
  uno por renglón del desglose — ver mockup del caso multi-cargo, sección 1).
- El modal lo dice en criollo: *"Se deja sin efecto el comprobante completo, con todos
  los cargos que salieron en él. Después podés corregir cada cargo y volver a cobrarla."*
- Después de deshecho, cada cargo se corrige/edita por separado y se vuelve a emitir
  (con el flujo de corrección que ya existe).

### P2 — RESPONDIDA POR GASTÓN (2026-07-14)

Pasado el plazo de ARCA, el aviso sube de tono pero **sigue sin bloquear** (texto exacto
de Gastón):

> ⚠ Pasaron más de 15 días desde que se emitió este comprobante. Se puede deshacer
> igual, pero convendría consultarlo con un contador antes de seguir.

Mientras el plazo NO venció, el aviso es el liviano ("Quedan N días… vence el {fecha}").
Ver sección 5.

---

## Qué implementa `frontend-senior` (resumen para no reabrir nada)

1. Extiende el cartel de la familia `confirmada` de `OperatorPenaltyStepPanel.jsx` con
   el link "Deshacer" (mockup sección 1). El link es **UNO solo por comprobante**, al pie
   del cartel — NUNCA uno por renglón del desglose (regla fiscal, P1): deshacer anula el
   comprobante entero con todos sus cargos.
2. Nuevo panel en línea (mismo patrón que `DeshacerCierreSinMultaInline.jsx`: motivo
   obligatorio 5-500 caracteres, dos pasos, error inline, datos intactos si falla) con
   los textos exactos de la sección 2, incluida la variante "ya pagó" y la variante
   "sin permiso de ver costos".
3. Dos cartelitos nuevos (sección 3): "deshaciendo…" (reusa el mismo hook de polling
   que "se está emitiendo la multa") y "no se pudo deshacer" (mismo patrón que
   `copyAccionTrabada`, con botón "Reintentar").
4. Línea de rastro (sección 4), con la misma función pura que ya existe
   (`textoRastroWaived`) adaptada al nuevo evento — se muestra en el cartel reabierto Y
   en el cartel vuelto a confirmar.
5. Aviso de los 15 días (sección 5), condicionado a que el backend mande el dato
   calculado — si no lo manda, el aviso simplemente no aparece (nunca se calcula la
   fecha a mano en el frontend).
6. Gateo de visibilidad: **Admin only** (mismo criterio que el "Deshacer" existente),
   salvo que Gastón responda distinto.
