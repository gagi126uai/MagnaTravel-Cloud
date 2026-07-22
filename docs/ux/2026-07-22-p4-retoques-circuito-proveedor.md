# P4 — 3 retoques de pantalla del circuito proveedor (spec UX)

> **Qué es esto:** especificación de los 3 retoques de la Tanda P4 (seguimientos anotados en
> sesiones previas). SOLO diseño; nada de código.
> **Fuente de diseño:** `docs/ux/guia-ux-gaston.md` ÚNICAMENTE. Lo que la guía no cubre va al final,
> en "PREGUNTAS PARA GASTON", con opciones y recomendación. Ningún hueco se rellena "a criterio".
> **Fecha:** 2026-07-22.

Los tres son incoherencias pantalla↔motor o ruido visual: la pantalla afirma algo que el motor no dice,
o apila avisos de más. En los tres, el motor y el mecanismo ya existen; se toca solo la capa visible.

---

## Retoque 1 — El botón "Eliminar" se esconde en cobros con el recibo anulado

### Comportamiento actual (verificado en código)

Archivo: `src/TravelWeb/src/features/reservas/pages/ReservaDetailPage.jsx`, componente
`PaymentReceiptActions` (~465-522).

- Línea 472: `const reciboAnulado = receipt?.status === "Voided";`
- Línea 476: `const cobroEsEditable = Boolean(canEditarEliminar) && !reciboAnulado;`
- Línea 517: `{cobroEsEditable && <EditarEliminarCobro .../>}`

Traducido: si el recibo de ese cobro está **anulado**, la pantalla pone `cobroEsEditable = false` y
**no renderiza** `EditarEliminarCobro`. Resultado: **desaparecen los DOS botones** (Editar **y** Eliminar),
por más que el motor permita alguno. Del cobro solo queda el chip gris **"Comprobante anulado"**.

El problema es que esa regla `!reciboAnulado` está **escrita a mano en la pantalla** y **pisa** lo que dice
el motor. El motor ya manda, por cada cobro, dos permisos separados (`payment.canEdit` y `payment.canDelete`,
forma `{ allowed, reason }`), y el componente `EditarEliminarCobro` + `paymentRowGuard.js` ya saben mostrarlos
con el patrón de la Tanda 6 (2026-07-20): **botón normal si el motor permite; botón gris con el motivo al lado
si el motor bloquea**. La línea local del recibo anulado cortocircuita todo eso y esconde el botón aunque el
motor diga que sí.

Como el motor **permite eliminar** un cobro con recibo anulado (dato del enunciado, confirmado por el circuito
proveedor), la pantalla está **mintiendo**: afirma "no se puede" (escondiendo el botón) algo que el motor sí
deja hacer.

### Propuesta (cubierta por la guía)

Sacar el candado local `&& !reciboAnulado` y dejar que **mande el motor**, exactamente como ya se hace con
todos los demás candados de cobro:

1. `cobroEsEditable` pasa a depender solo de la capacidad de reserva (`canEditarEliminar`, que sigue
   escondiendo los dos botones cuando la reserva entera está terminal/cerrada — eso NO cambia).
2. Con la reserva editable, se renderiza `EditarEliminarCobro`, que decide **por botón** leyendo
   `payment.canEdit` / `payment.canDelete`:
   - **Eliminar**: si el motor lo permite → botón **normal** (rojo, como en cualquier cobro).
   - **Editar**: si el motor lo bloquea (lo típico con recibo anulado) → botón **gris con el motivo al lado**
     ("🔒 …", texto que ya manda el motor, sin reescribir).
3. El chip **"Comprobante anulado"** se mantiene igual: es la trazabilidad del documento y ya le da contexto
   al vendedor de por qué ese cobro es especial.

Esto **no inventa nada nuevo**: es el patrón Tanda 6 que ya está en producción para el resto de los cobros.
La pantalla deja de contradecir al motor.

**Por qué está cubierto por la guía:**
- Regla dura 2026-06-08 ("la palabra al lado, siempre visible; nada de tooltip") + patrón Tanda 6 2026-07-20
  (candado por cobro = botón gris + motivo del motor al lado).
- Principio Fase 4 2026-06-26: **botón escondido = "no aplica"; botón gris con motivo = "bloqueado"**. Acá el
  motor permite Eliminar → el botón **no debe estar escondido** (esconderlo afirma "no aplica", que es falso).

### Mockup ASCII

Cobro con recibo anulado, **hoy** (mal — no hay forma de eliminar aunque el motor deje):

```
┌────────────────────────────────────────────────────────┐
│ Cobro $ 50.000   [ Comprobante anulado ]                │
│ (no aparece ningún botón)                               │
└────────────────────────────────────────────────────────┘
```

Cobro con recibo anulado, **propuesta** (el motor manda: permite Eliminar, bloquea Editar):

```
┌────────────────────────────────────────────────────────┐
│ Cobro $ 50.000   [ Comprobante anulado ]                │
│   [ ✏ Editar (gris) ]   [ 🗑 Eliminar ]                 │
│   🔒 No se puede editar: el comprobante de este cobro   │
│      está anulado.                                       │
└────────────────────────────────────────────────────────┘
```

> Nota para frontend-senior: el resultado exacto (qué botón queda normal y cuál gris) lo decide el motor por
> cobro; la pantalla no debe volver a hardcodear ninguna regla de recibo anulado. Si el motor bloqueara también
> Eliminar en algún caso, ese botón saldría gris con su motivo — igual, obedeciendo al motor.

**Única duda menor de este retoque → P1 abajo** (¿el botón Eliminar aparece igual que en cualquier cobro, o con
algún distintivo?). Recomendación: igual que cualquier cobro.

---

## Retoque 2 — Carteles apilados en "Anular reserva" (CancelarReservaInline)

### Comportamiento actual (verificado en código)

Archivo: `src/TravelWeb/src/features/cancellations/components/CancelarReservaInline.jsx`.

Conté con cuidado **cuántos carteles pueden convivir a la vez**. El panel tiene 6 estados internos
(`estadoMulti`): `form`, `confirmando-multi`, `procesando-multi`, `exito-multi`, `revision-multi`,
`timeout-multi`. **Cinco de esos seis muestran un solo bloque cada uno** — ahí no hay apilamiento y están bien.

El apilamiento vive **solo en el estado `form`** (el panel normal, antes de disparar la anulación). Ahí pueden
convivir **hasta 2 carteles**:

1. **Cartel del caso** (siempre hay uno; son mutuamente excluyentes entre sí, ~580-616):
   - VERDE — "baja directa" (DirectCancel).
   - CELESTE — "la plata queda como saldo a favor" (PaymentsToCredit).
   - ÁMBAR — "se va a emitir una nota de crédito" (CreditNote), con ícono ⚠.
2. **Cartel de error** (rose, ~622-644): aparece **encima** del anterior **solo cuando el motor rechaza** la
   anulación (400/409). Trae el motivo real y, en un caso, un botón "Emitir factura".

O sea: cuando el vendedor aprieta "Anular reserva" y el motor lo frena, quedan **dos cajas de aviso apiladas**,
las dos con triángulo de alerta: la ámbar "esto va a emitir una nota de crédito" **y** la rose "no se pudo,
porque X". La ámbar (informa lo que iba a pasar) ya no aporta: la acción **falló**, y lo único que importa es el
error y qué hacer.

### Propuesta

Que en el estado `form` **nunca haya dos carteles a la vez**: mientras el vendedor decide, se ve **solo el
cartel del caso** (la foto de qué va a pasar); apenas hay un error, **ese mismo lugar muestra solo el error**
(que ya trae el motivo y, si corresponde, el botón "Emitir factura"). El cartel del caso se oculta mientras haya
error. Ningún dato se pierde: el motivo del caso ya no hace falta una vez que sabés que falló y por qué.

Regla concreta:
- Sin error → se ve el cartel del caso (verde / celeste / ámbar), como hoy.
- Con error → se ve **solo** el cartel rose de error. El cartel del caso desaparece hasta que el error se limpie
  (el vendedor corrige y reintenta).

Esto **no toca** los otros cinco estados (confirmar-múltiple, procesando, éxito, revisión, timeout): ya muestran
un bloque único y están bien.

**Cobertura de guía:** el principio 2026-07-05 ("arriba la foto, abajo solo lo que hay que hacer" + "lo
accionable manda, no compite con nada") y la Ronda 2 2026-06-06 (el error de guardado va en un cartel propio,
arriba, con lo cargado intacto) **apuntan** a esta dirección, pero fueron dictados para **otra pantalla** (la
tira de avisos de la ficha de reserva / el guardado del formulario de servicio), no para este panel. Por
prudencia lo llevo a pregunta **P2**, con recomendación A = "un cartel a la vez (el error tapa al del caso)".

### Mockup ASCII

Hoy (con error, dos cajas apiladas):

```
┌───────────────── Anular reserva #1042 — Fam. García ─────┐
│ ⚠ Esta reserva tiene factura: se va a emitir una nota    │  ← cartel del caso (ámbar)
│   de crédito para anularla.                              │
│ ⚠ No se pudo anular: el operador ya cobró y no hay       │  ← cartel de error (rose)
│   factura para anclar el reembolso.   [ Emitir factura ] │
│ Motivo de la anulación *                                 │
│ [__________________________________________]            │
│                              [ Volver ] [ Anular reserva ]│
└──────────────────────────────────────────────────────────┘
```

Propuesta (con error, un solo cartel):

```
┌───────────────── Anular reserva #1042 — Fam. García ─────┐
│ ⚠ No se pudo anular: el operador ya cobró y no hay       │  ← solo el error
│   factura para anclar el reembolso.   [ Emitir factura ] │
│ Motivo de la anulación *                                 │
│ [__________________________________________]            │
│                              [ Volver ] [ Anular reserva ]│
└──────────────────────────────────────────────────────────┘
```

Propuesta (sin error, mientras decide — igual que hoy):

```
┌───────────────── Anular reserva #1042 — Fam. García ─────┐
│ ⚠ Esta reserva tiene factura: se va a emitir una nota    │  ← solo el cartel del caso
│   de crédito para anularla.                              │
│ Motivo de la anulación *                                 │
│ [__________________________________________]            │
│                              [ Volver ] [ Anular reserva ]│
└──────────────────────────────────────────────────────────┘
```

---

## Retoque 3 — El banner del candado le habla igual al Admin que al vendedor

### Comportamiento actual (verificado en código)

Archivos:
- `src/TravelWeb/src/features/reservas/components/ReservaLockBanner.jsx` (variante ámbar, ~91-112).
- `src/TravelWeb/src/features/reservas/components/EditAuthorizationModal.jsx` (el que se abre al tocar el botón).
- `src/TravelWeb/src/features/reservas/pages/ReservaDetailPage.jsx` (~1263 y ~1922:
  `onRequestEdit={() => setShowEditAuthModal(true)}`).

Cuando la reserva está Confirmada (candado), la franja ámbar dice, **para todos por igual**:

> 🔒 **Reserva confirmada.** Para cambiar algo, pedí autorización. &nbsp; **[ Pedí autorización ]**

El botón abre `EditAuthorizationModal`, y **ese modal SÍ distingue** por permiso
`reservas.authorize_locked_edit`:
- **Vendedor común** → "Pedile a un administrador que la destrabe." (pasivo, sin poder hacer nada).
- **Admin** → formulario con motivo + botón **"Desbloquear reserva"** (destraba entero por 30 minutos).

O sea: **el mecanismo para que el Admin destrabe directo YA EXISTE y ya funciona**. Lo único incoherente es el
**texto de la franja**: al Admin le dice "pedí autorización" (como si tuviera que pedírsela a otro), cuando en
realidad, al tocar el botón, se la da él mismo. Le habla como si fuera un vendedor.

### Propuesta

Que la **franja** distinga igual que ya distingue el modal, usando el **mismo permiso**
(`reservas.authorize_locked_edit`), para que el texto del banner coincida con lo que el botón realmente va a
ofrecer:

- **Vendedor** (sin el permiso): **queda EXACTAMENTE como está** (texto y botón 4B de la guía 2026-07-05, sin
  cambios).
- **Admin** (con el permiso): la franja dice que puede destrabarla él mismo, y el botón invita a destrabar en vez
  de a pedir permiso. El click abre **el mismo `EditAuthorizationModal`** (que para el Admin ya muestra el
  formulario de motivo). **No se construye ningún mecanismo nuevo.**

Las otras dos variantes de la franja no necesitan cambio: la naranja de regresión y la verde "destrabada" no
tienen el botón "Pedí autorización" y su texto sirve para ambos roles.

**Cobertura de guía:**
- La **decisión de fondo YA está en la guía**: regla 2026-06-08 #2 — "el vendedor común ve 'Pedile a un
  administrador que la destrabe'; el **admin** escribe el motivo y la **destraba** entera por 30 minutos". La
  guía ya bendice que el Admin destrabe directo (y el modal ya lo cumple).
- Lo que la guía **NO** fija es el **texto exacto y el rótulo del botón de la FRANJA para el Admin**: el texto
  4B (2026-07-05) es genérico y quedó pensado para el vendedor. Introducir la variante Admin **extiende** 4B (no
  la contradice: el caso vendedor queda idéntico). Como es texto nuevo, va a pregunta **P3**, con recomendación.

### Mockup ASCII

Vendedor — **queda como está**:

```
┌──────────────────────────────────────────────────────────────┐
│ 🔒 Reserva confirmada. Para cambiar algo, pedí autorización.  │
│                                          [ Pedí autorización ]│
└──────────────────────────────────────────────────────────────┘
```

Admin — propuesta (recomendación P3-A):

```
┌──────────────────────────────────────────────────────────────┐
│ 🔒 Reserva confirmada (con candado). Podés destrabarla para   │
│    editar.                              [ Destrabar reserva ] │
└──────────────────────────────────────────────────────────────┘
        (el botón abre el MISMO modal que hoy: motivo + 30 min)
```

> Nota para frontend-senior: pasar a `ReservaLockBanner` un prop tipo `puedeAutorizar =
> hasPermission('reservas.authorize_locked_edit')` y elegir texto + rótulo del botón según ese flag. El
> `onRequestEdit` y el modal no cambian. Clave por el **permiso**, no por `isAdmin()` a secas, para que el rótulo
> del banner coincida siempre con lo que el modal le va a ofrecer al usuario.

---

## Resumen de cobertura

| Retoque | ¿Guía lo cubre? | Qué queda para Gastón |
|---|---|---|
| 1 — Eliminar con recibo anulado | **Sí** (patrón Tanda 6 + "obedecer al motor"). Se especifica y se implementa. | Solo P1: confirmar que Eliminar aparece **sin distintivo**. |
| 2 — Carteles apilados al anular | **No** (el principio existe pero para otra pantalla). | P2: un cartel a la vez (el error tapa al del caso). |
| 3 — Banner del candado para Admin | **Parcial**: la guía ya decide que el Admin destraba directo; falta el **texto de la franja** para Admin. | P3: texto + rótulo del botón para Admin. |

---

## PREGUNTAS PARA GASTON

> **✅ RESPONDIDAS Y FIRMADAS por Gaston el 2026-07-21** (eligió la recomendada en las 3):
> **P1 = A** (botón Eliminar igual que siempre; el cartelito gris de la fila alcanza) ·
> **P2 = A** (con error, solo el aviso del error; el del caso reaparece al resolverlo) ·
> **P3 = A** (Admin ve "Podés destrabarla para editar" + botón "Destrabar reserva", mismo modal; vendedor idéntico).
> Con esto la spec queda CERRADA para implementación.

### Tema: cobro con el comprobante anulado (Estado de Cuenta de la reserva)
Contexto: hoy, si a un cobro le anulaste el comprobante, la pantalla te esconde el botón de borrar ese cobro,
aunque el sistema por dentro sí te deja borrarlo. Lo vamos a mostrar. La única duda es si ese botón "Eliminar"
tiene que verse igual que en cualquier otro cobro o con algo que avise que ese cobro es especial.

**P1. Cuando el cobro tiene el comprobante anulado y se puede borrar, ¿cómo se ve el botón "Eliminar"?**
  A) **(Recomendada)** Igual que en cualquier cobro (rojo, normal). El cartelito gris "Comprobante anulado" que
     ya está al lado alcanza para avisar que ese cobro es especial.
```
      Cobro $ 50.000   [ Comprobante anulado ]
        [ ✏ Editar (gris) ]   [ 🗑 Eliminar ]
        🔒 No se puede editar: el comprobante de este cobro está anulado.
```
  B) Con un distintivo extra en el botón (por ejemplo un texto "Eliminar (comprobante anulado)").
```
      Cobro $ 50.000   [ Comprobante anulado ]
        [ ✏ Editar (gris) ]   [ 🗑 Eliminar (comprobante anulado) ]
```

---

### Tema: pantalla "Anular reserva" — que no se amontonen dos avisos
Contexto: cuando querés anular una reserva y el sistema te frena (por ejemplo, "el operador ya cobró y falta la
factura"), en pantalla quedan DOS cuadros de aviso pegados: uno que explicaba qué iba a pasar y otro que dice por
qué no se pudo. El segundo es el que importa. Queremos dejar uno solo.

**P2. Cuando la anulación te da un error, ¿qué aviso se muestra?**
  A) **(Recomendada)** Solo el aviso del error (con el motivo y, si corresponde, el botón para resolverlo). El
     cuadro que explicaba "qué iba a pasar" se esconde mientras haya error, y vuelve si corregís.
```
      ⚠ No se pudo anular: el operador ya cobró y no hay factura
        para anclar el reembolso.        [ Emitir factura ]
      Motivo de la anulación *  [_______________________]
```
  B) Dejar los dos cuadros apilados, como está hoy.
```
      ⚠ Esta reserva tiene factura: se va a emitir una nota de crédito.
      ⚠ No se pudo anular: el operador ya cobró y no hay factura...
      Motivo de la anulación *  [_______________________]
```

---

### Tema: el cartel del candado cuando lo mira un Administrador
Contexto: cuando una reserva está confirmada aparece un cartel con candado que dice "pedí autorización". A un
vendedor eso le sirve (tiene que pedírsela a un admin). Pero a un **Administrador** el mismo cartel le queda raro:
él no le pide permiso a nadie, la puede destrabar solo (y el sistema ya se lo permite al tocar el botón). Queremos
que el cartel le hable distinto al admin.

**P3. Para el Administrador, ¿cómo queda el cartel del candado?** (El del vendedor no cambia.)
  A) **(Recomendada)** El cartel dice que puede destrabarla él mismo y el botón se llama "Destrabar reserva". Al
     tocarlo se abre la misma ventanita de siempre (pide el motivo, destraba 30 minutos).
```
      🔒 Reserva confirmada (con candado). Podés destrabarla
         para editar.                    [ Destrabar reserva ]
```
  B) Dejarlo igual que para el vendedor: "Para cambiar algo, pedí autorización." + botón "Pedí autorización".
```
      🔒 Reserva confirmada. Para cambiar algo, pedí autorización.
                                          [ Pedí autorización ]
```
