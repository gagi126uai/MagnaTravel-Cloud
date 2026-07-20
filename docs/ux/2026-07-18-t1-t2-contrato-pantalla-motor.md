# Spec UX — Tandas 1 y 2 del plan de remediación del contrato pantalla ↔ motor

**Fecha**: 2026-07-18
**Autor**: ux-ui-disenador (diseña SOLO desde `docs/ux/guia-ux-gaston.md`)
**Insumos**: `docs/architecture/2026-07-18-contrato-pantalla-motor-PLAN-remediacion.md` (Tandas 1 y 2 + §6 firmadas) · `docs/architecture/2026-07-18-contrato-pantalla-motor-inventario.md` (filas C5, C4, C6, C3).
**Para**: frontend-senior (implementa al pie de la letra).

---

## Resumen para el orquestador (leer esto primero)

**Las dos tandas están 100% CUBIERTAS por la guía + patrones ya construidos y en uso. NO hay preguntas nuevas para Gastón.**

El hallazgo central de la auditoría de patrones (regla del proyecto: "auditar patrones UI existentes ANTES de crear UI nueva") es que **ni T1 ni T2 crean pantalla nueva**: las dos reusan cosas que ya existen, ya se ven bien, y ya están respaldadas por reglas firmadas de la guía. Concretamente:

- **T1 (pagar al proveedor):** la ficha de pago **YA tiene** el cartel rojo arriba de los botones que la guía manda (regla "cartel rojo arriba de los botones", 2026-06-05 Ronda 2) — hoy muestra un genérico solo porque el backend le manda un genérico. Cuando el backend deje de tragarse el mensaje real, **el cartel que YA está lo muestra solo, sin tocar el diseño**. Los 7 mensajes reales del backend son todos criollo-con-camino y NO se reescriben (Decisión C / D2 firmada por Gastón: "mejorar solo los rotos, no reescribir todo").
- **T2 (pedir autorización desde la ficha):** el modal para pedir autorización (`RequestApprovalModal`) **YA existe y lo usan 6 pantallas**, incluida Cobranzas → Movimientos, que hace exactamente esta acción. La ficha de reserva es la ÚNICA que no lo engancha. Copiar el mismo enganche = misma acción, misma experiencia. No hay cartel intermedio nuevo porque la pantalla de referencia (Movimientos) no tiene ninguno: el modal se abre directo.

Ambas tandas ya venían marcadas en el plan como **"Decisión del dueño: no"** (puro arreglo). Esta spec lo confirma contra la guía.

---

# TANDA 1 — "Pagar al proveedor" deja de tragarse los mensajes

## Qué cubre la guía (citado)

1. **Cómo se muestra un error de guardado en una ficha en línea → cartel rojo arriba de los botones, con la ficha intacta.**
   Guía, sección "Ficha de carga en línea F2 → Ronda 2" (2026-06-05):
   > *"Si falla Guardar: la ficha queda abierta con TODO lo cargado intacto + cartel rojo arriba de los botones (...); reintenta en el mismo botón. (Nunca se pierde lo cargado.)"*
   Y la regla se repite como estándar general del sistema para fichas en línea (misma sección, y otra vez en las specs de cobro/devolución). El pago al proveedor es una ficha en línea (`PagarProveedorInline`), así que le aplica tal cual.

2. **El pago al proveedor NO es ventana flotante: es ficha en línea.**
   Coherente con la regla dura "el modal me parece horrible → todo en línea" (Multimoneda P3, 2026-06-09; ADR-035 C, 2026-06-19). Por eso el mensaje va en un **cartel dentro de la ficha**, NO en un toast (globito que aparece y se va): un toast se pierde justo cuando el vendedor necesita leer con calma qué corregir en un formulario de varios campos.

3. **No reescribir los mensajes que ya están bien; mejorar solo los rotos.**
   Decisión C del plan + **D2 firmada por Gastón (2026-07-18)**:
   > *"NO reescribirlos todos (...): el sistema manda siempre un texto seguro, y mejoramos a mano sólo los pocos donde falta un botón de 'qué hacer' o donde hoy sale una palabra técnica."*

4. **Cada rechazo dice qué hacer, sin jerga.**
   Reglas transversales del plan (heredadas de la guía): "cada rechazo dice QUÉ HACER AHORA en criollo" + "cero IDs/GUIDs, cero enums, cero nombres internos".

5. **Avisar ANTES cuando se puede (pre-chequeo).**
   La guía premia el pre-chequeo en varios lugares (capabilities que apagan botones, "Próximos inicios" que avisa solo). Filtrar el selector de servicio por moneda y avisar "esta reserva no tiene servicios de este proveedor" antes de enviar es aplicar ese mismo criterio.

**Conclusión T1:** todo lo que pide la tanda está cubierto. No hay decisión de diseño nueva.

## Auditoría del patrón vigente (lo que ya está construido)

En `PagarProveedorInline.jsx` **ya existe** el cartel correcto, arriba de los botones (línea ~970):

```
role="alert"  ·  fondo rosa (bg-rose-50 / borde rose-200)  ·  data-testid="pago-error"
```

Hoy ese cartel muestra `getApiErrorMessage(error, "No se pudo guardar el pago...")`. El problema **no es el cartel** — es que el backend (`SuppliersController.AddSupplierPayment` / `UpdateSupplierPayment`) atrapa la excepción y devuelve un genérico fijo, tirando a la basura el texto real. Cuando el backend propague el mensaje real (como ya hace `DeleteSupplierPayment` en el mismo controller), **el cartel que ya está lo muestra solo**. El front del cartel NO se toca.

## Spec de pantalla — ficha "Registrar pago al proveedor"

### Estado HOY (el callejón — peor caso #1 del inventario)

```
┌─ Registrar pago al proveedor ──────────────────────────── [x] ┐
│                                                                │
│  Proveedor: Despegar Mayorista                                 │
│  Reserva:   R-1042 · Fam. García                               │
│  Servicio:  [ Hotel Maitei (US$) ▾ ]   Monto: [ $ 50.000  ]    │
│  Método:    [ Transferencia ▾ ]        Fecha:  [ 18/07/2026 ]  │
│                                                                │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⓧ No se pudo registrar el pago al proveedor.            │  │  ← genérico
│  └────────────────────────────────────────────────────────┘  │     sin causa
│                                    [ Cancelar ] [ Confirmar ]  │
└────────────────────────────────────────────────────────────────┘
```

El vendedor pagó $ 50.000 (pesos) contra un hotel que está en dólares. El backend lo sabe exacto, pero el cartel no lo dice. No tiene NINGUNA pista de qué corregir.

### Estado NUEVO (mismo cartel, ahora con el mensaje real)

```
┌─ Registrar pago al proveedor ──────────────────────────── [x] ┐
│                                                                │
│  Proveedor: Despegar Mayorista                                 │
│  Reserva:   R-1042 · Fam. García                               │
│  Servicio:  [ Hotel Maitei (US$) ▾ ]   Monto: [ $ 50.000  ]    │
│  Método:    [ Transferencia ▾ ]        Fecha:  [ 18/07/2026 ]  │
│                                                                │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⓧ La moneda del pago no coincide con la del costo del   │  │  ← mensaje
│  │   servicio.                                              │  │     REAL del
│  └────────────────────────────────────────────────────────┘  │     backend
│                                    [ Cancelar ] [ Confirmar ]  │
└────────────────────────────────────────────────────────────────┘
```

- **Todo lo cargado queda intacto.** La ficha no se cierra ni se limpia. El vendedor corrige y reintenta en el mismo botón "Confirmar".
- **El cartel es rosa/rojo, arriba de los botones, `role="alert"`.** Idéntico al de hoy: solo cambia el texto que le llega.
- **Nada de toast.** El mensaje se queda a la vista mientras el vendedor corrige.

### Mejor todavía: avisar ANTES de enviar (los 2 pre-chequeos de la tanda)

Dos de los 7 casos se pueden atrapar antes de que el vendedor apriete "Confirmar":

**(a) Moneda del pago ≠ moneda del servicio** — el selector de servicio **filtra** y solo lista los servicios cuya moneda coincide con la del pago. Si el vendedor cambia la moneda del pago, la lista se recalcula sola. Así el caso ni se puede armar.

```
Moneda del pago:  ( • US$ )  ( ○ pagar en otra moneda )
Servicio:  [ Hotel Maitei (US$)   ▾ ]     ← solo aparecen servicios en US$
           (los servicios en $ no se listan mientras el pago sea US$)
```

**(b) La reserva no tiene servicios de este proveedor** — si el vendedor elige una reserva sin ningún servicio de ese proveedor, aparece una línea de aviso en la ficha (mismo lugar donde ya salen las validaciones de la ficha, ej. "El monto tiene que ser mayor a 0") **antes** de habilitar "Confirmar":

```
Reserva:  [ R-1055 · Fam. Pérez ▾ ]
          Esta reserva no tiene servicios de este proveedor para imputar el pago.   ← aviso previo
```

Estos dos pre-chequeos NO agregan pantalla nueva: reusan el mismo mecanismo de validación en línea que la ficha ya tiene (`setErrorGuardar` / mensajes de campo). El resto de los casos (que dependen de estado que el front no tiene a mano) caen al cartel rojo con el mensaje real.

## Los 7 mensajes del backend — validados contra la guía

Todos vienen de `SupplierService.cs`, todos son **criollo-con-camino**, ninguno tiene jerga ni enums ni nombres internos. Por Decisión C / D2 firmada, **NINGUNO se reescribe**. Se listan para que frontend-senior confirme que el texto que llega es exactamente este:

| # | Cuándo salta | Texto real (se propaga tal cual) | Veredicto UX |
|---|---|---|---|
| 1 | Pago imputado a reserva Y anticipo a la vez | "Un pago no puede imputarse a una reserva y marcarse como anticipo a cuenta a la vez." | OK. Criollo, dice qué está mal. Además ya está pre-chequeado (son excluyentes en la ficha). |
| 2 | Imputar a un servicio sin indicar la reserva | "Para imputar el pago a un servicio hay que indicar también su reserva." | OK. Dice exactamente qué falta. Ya pre-chequeado. |
| 3 | La reserva no tiene servicios de ese proveedor | "La reserva no tiene servicios de este proveedor para imputar el pago." | OK. **Se le suma el pre-chequeo (b) de arriba** para avisar antes del clic. |
| 4 | La moneda del pago no matchea ninguna deuda de ese proveedor en la reserva | "El pago no coincide con ninguna moneda de la deuda de este proveedor en la reserva." | OK. Dice qué está mal; el camino (cambiar la moneda) es el propio selector. |
| 5 | Moneda del pago ≠ moneda del costo del servicio elegido | "La moneda del pago no coincide con la del costo del servicio." | OK. **Se le suma el pre-chequeo (a): el selector filtra por moneda.** |
| 6 | El cargo del operador ya se pagó (anti doble pago) | "Ese cargo del operador ya se pagó. Si el pago anterior era incorrecto, eliminalo primero." | OK. Criollo-con-camino ejemplar. Ya pre-chequeado. |
| 7 | La liquidación no cubre el monto/moneda completo del cargo | "Para liquidar el cargo, el pago debe cubrir su monto completo en la misma moneda." | OK. Dice la condición exacta. Ya pre-chequeado. |

**Ninguno necesita reescritura.** Si al implementar aparece alguno con acento faltante ("también", "eliminalo") o un enum colado que la auditoría no vio, se corrige la ortografía/limpieza en el momento (no es cambio de diseño), pero el contenido queda.

## Punto lateral de la tanda: botón "Nueva factura" del proveedor

La tanda también apaga "Nueva factura" cuando el operador **factura directo al cliente** (no genera cuenta por pagar de la agencia). Esto está cubierto por precedente de la guía, **no es decisión nueva**:

- Regla ADR-036 punto 4 (2026-06-21, P3=A): *"En una reserva con plata viva, el botón 'Eliminar' simplemente NO aparece"* — precedente firmado de **esconder** un botón cuando la acción **estructuralmente nunca aplica** a esa entidad (no agrisarlo con explicación suelta).
- Un operador que factura directo al cliente NUNCA tiene cuenta por pagar → "Nueva factura" nunca aplica → **se esconde**, igual que "Eliminar" cuando hay plata viva. La propia sección de cuenta por pagar de ese operador tampoco tiene sentido para él.

**Spec:** para operadores con facturación directa al cliente, el botón "Nueva factura" **no se muestra** (no gris con cartelito). Coherente con P3=A. Si igual explota por carrera, el cartel de la ficha muestra el mensaje real ("Este operador factura directo al cliente y sus servicios no generan una cuenta por pagar de la agencia.").

## Estados de la ficha de pago (checklist frontend)

| Estado | Qué se ve |
|---|---|
| Cargando datos | Lo de hoy (no cambia). |
| Guardando | Botón "Confirmar" → "Guardando…", deshabilitado (anti doble envío). Lo de hoy. |
| Validación previa (moneda/reserva) | Aviso en línea + "Confirmar" no habilita hasta corregir. |
| Error de negocio del backend | **Cartel rojo arriba de los botones con el mensaje REAL** + ficha intacta + reintento en el mismo botón. |
| Error de red | Mismo cartel, con el fallback de siempre ("No se pudo guardar el pago. Revisá la conexión y probá de nuevo."). |
| Éxito | "Pago registrado." + se cierra la ficha (lo de hoy). |

## Qué NO hay que hacer (T1)

- NO cambiar el estilo, la posición ni el color del cartel de error: ya es el correcto.
- NO usar toast para el error de guardado de esta ficha.
- NO reescribir los 7 mensajes del backend (solo limpieza de acento/enum si apareciera).
- NO agrisar "Nueva factura" con cartelito para el operador directo: se esconde.
- NO cerrar la ficha ni perder lo cargado cuando el guardado falla.

---

# TANDA 2 — Pedir autorización desde la ficha de reserva

## Qué cubre la guía (citado)

1. **El affordance "Pedí autorización" es un patrón aprobado de la ficha.**
   Guía, "Ventanas emergentes y avisos" (2026-07-05, respuesta 4B) y ciclo de vida punto 1 (2026-06-08):
   > *"franja explicativa arriba ('🔒 Reserva confirmada. Para cambiar algo, pedí autorización.') + botón 'Pedí autorización' a la derecha."*
   O sea: pedir autorización DESDE la ficha ya es un concepto que Gastón aprobó. Lo que falta es enganchar ese pedido a la acción de anular/emitir comprobante.

2. **Misma acción = misma experiencia, venga de donde venga.**
   Guía, "El paso de multa vive en la ficha" (2026-07-08, madrugada), principio 1:
   > *"UNA sola experiencia (...), venga de donde venga."*
   Y "Estados congelados" (2026-06-22) trata la ficha y Movimientos con las mismas reglas de comprobante. Anular un comprobante desde la ficha tiene que comportarse igual que anularlo desde Cobranzas → Movimientos.

3. **Ningún mensaje deja al usuario sin salida ni lo manda a buscar solo otra pantalla.**
   Guía, mismo bloque 2026-07-08, principio 3: *"Ningún mensaje deriva a un rol que el usuario ya es"* / la resolución vive donde el usuario está parado. Hoy la ficha dice "necesitás autorización" y lo obliga a saber que existe OTRA pantalla (Movimientos) donde sí se puede pedir. Eso es el callejón (peor caso #2 del inventario).

**Conclusión T2:** el patrón y los principios están cubiertos. No hay decisión de diseño nueva.

## Auditoría del patrón vigente (lo que ya está construido)

`RequestApprovalModal` (`features/approvals/components/RequestApprovalModal.jsx`) es un modal **ya en producción**, usado por **6 pantallas** (Movimientos, Historial, Pendientes, Facturación de Cobranzas, Reconciliación de NC, y el paso de multa). Se abre así, siempre igual:

- El hook `useFinanceActions` intenta la acción; si el backend responde "esto necesita autorización" (409 con `requiresApproval`), llama `onApprovalRequired(...)`.
- La página guarda ese contexto (`setApprovalContext(...)`) y renderiza el modal.
- El modal pide **un motivo (mínimo 10 caracteres)**, muestra "Sobre: {qué comprobante}" y avisa "Un Administrador o Colaborador va a revisar la solicitud. Te notificamos cuando se resuelva."
- Al enviar: "Solicitud enviada. El back-office la va a revisar." y se cierra.

**La ficha de reserva (`ReservaDetailPage`) NO engancha nada de esto.** Su `handleVoidReceipt` ya tiene el mismo diálogo de confirmación que Movimientos ("Anular comprobante · Sí, anular"), pero en el `catch` solo hace `showError(...)` — muestra el texto "necesitás autorización" y ahí muere, sin botón.

## Spec de pantalla — anular comprobante desde la ficha

### Estado HOY (el callejón — peor caso #2)

Vendedor sin permiso, en la solapa Estado de Cuenta de la ficha:

```
Cobro del 15/07 · $ 80.000 · Transferencia          [ Anular comprobante ]

  (click)  →  "¿Anular comprobante? El pago sigue vigente."  [ Sí, anular ]
  (Sí)     →  ┌──────────────────────────────────────────────────────┐
              │ ⓧ Esta acción requiere autorización previa del       │   ← toast que
              │   Administrador o Colaborador.                        │      aparece y se va
              └──────────────────────────────────────────────────────┘
                          ↑ y no hay NADA para pedirla desde acá
```

### Estado NUEVO (mismo comportamiento que Cobranzas → Movimientos)

```
Cobro del 15/07 · $ 80.000 · Transferencia          [ Anular comprobante ]

  (click)  →  "¿Anular comprobante? El pago sigue vigente."  [ Sí, anular ]   (igual que hoy)
  (Sí)     →  el backend pide autorización  →  se abre EL MISMO modal de siempre:

              ┌─ 🛡 Solicitar aprobación ─────────────────────── [x] ┐
              │    Anulación de comprobante                          │
              │                                                      │
              │  ┌────────────────────────────────────────────────┐ │
              │  │ SOBRE                                          │ │
              │  │ Comprobante del cobro $ 80.000 · 15/07         │ │
              │  └────────────────────────────────────────────────┘ │
              │                                                      │
              │  Motivo (mínimo 10 caracteres)                       │
              │  ┌────────────────────────────────────────────────┐ │
              │  │ Explicá por qué necesitás esta autorización.   │ │
              │  └────────────────────────────────────────────────┘ │
              │  ⓘ Un Administrador o Colaborador va a revisar la    │
              │    solicitud. Te notificamos cuando se resuelva.     │
              │                          [ Cancelar ] [ Enviar ]     │
              └──────────────────────────────────────────────────────┘

  (Enviar) →  "Solicitud enviada. El back-office la va a revisar."
```

### Detalle del flujo (idéntico a Movimientos)

1. El diálogo de confirmación ("Anular comprobante · Sí, anular") **se mantiene igual** — ya está en la ficha y ya es igual al de Movimientos.
2. Si el backend responde que hace falta autorización (409 `requiresApproval`), en vez del toast de error **se abre el `RequestApprovalModal`** con los datos que el propio backend manda (`requestType`, `entityType`, `entityId`).
3. El "Sobre:" del modal identifica el comprobante en criollo (ej. "Comprobante del cobro $ 80.000 · 15/07"), sin IDs ni códigos.
4. Al enviar, el vendedor ve la confirmación del modal. El reintento de "Anular" lo hace él cuando el admin apruebe (no es automático) — igual que en Movimientos.
5. **Si el error NO es de autorización** (otra causa de negocio), sigue mostrándose como hoy con su mensaje real.

### ¿Hay cartel intermedio? NO.

La pregunta del brief ("el texto del cartel intermedio si lo hay") tiene respuesta clara desde la auditoría: **la pantalla de referencia (Cobranzas → Movimientos) no tiene ningún cartel intermedio.** El flujo es: confirmar → el backend pide autorización → el modal se abre directo. Meter un cartel intermedio en la ficha rompería la regla "misma acción = misma experiencia" (2026-07-08). Por eso: **sin cartel intermedio; el modal se abre directo**, exactamente como en Movimientos.

### Alcance: los dos comprobantes de la ficha

El mismo enganche va en los dos handlers de comprobante de la ficha:
- **Anular comprobante** (`handleVoidReceipt`) — el caso del peor #2.
- **Emitir comprobante** (`handleIssueReceipt`) — mismo patrón, por si emitir también pide autorización. Coherencia total.

## Coherencia ficha ↔ Cobranzas → Movimientos (tabla de paridad)

| Paso | Cobranzas → Movimientos (referencia) | Ficha de reserva (nuevo) |
|---|---|---|
| Diálogo de confirmación | "Anular comprobante · Sí, anular" | Igual (ya existe) |
| Backend pide autorización | Abre `RequestApprovalModal` directo | **Igual (esto es lo que se agrega)** |
| Cartel intermedio | No hay | No hay |
| Motivo | Obligatorio, mínimo 10 caracteres | Igual (mismo modal) |
| Confirmación final | "Solicitud enviada. El back-office la va a revisar." | Igual (mismo modal) |
| Reintento | Manual, cuando el admin aprueba | Igual |

## Estados de la acción (checklist frontend)

| Estado | Qué se ve |
|---|---|
| Sin permiso, dispara autorización | Confirmación → modal de solicitar aprobación (directo). |
| Con permiso | La acción se ejecuta normal (no aparece el modal). |
| Error de negocio (no autorización) | Toast con el mensaje real de siempre (lo de hoy). |
| Motivo < 10 caracteres | El modal no deja enviar (ya lo controla el propio modal). |
| Solicitud ya enviada hace poco (429) | El modal muestra su aviso de cooldown (ya lo maneja el modal). |
| Éxito | "Solicitud enviada. El back-office la va a revisar." |

## Qué NO hay que hacer (T2)

- NO crear un modal nuevo: se reusa `RequestApprovalModal` tal cual.
- NO agregar cartel intermedio: el modal se abre directo (paridad con Movimientos).
- NO cambiar el diálogo de confirmación de "Anular comprobante": ya es igual al de Movimientos.
- NO tocar backend: ya manda el 409 estructurado correcto.
- NO derivar al usuario a "andá a Cobranzas → Movimientos": la resolución vive donde está parado (principio 2026-07-08).

---

# PREGUNTAS PARA GASTON

**Ninguna.**

Las dos tandas están completamente cubiertas por reglas ya firmadas de la guía y por patrones que ya están construidos y en uso en el sistema:

- **T1** aplica la regla "cartel rojo arriba de los botones" (2026-06-05, Ronda 2) sobre la ficha en línea que YA lo tiene, y no reescribe mensajes por la Decisión C / **D2 que Gastón ya firmó** (2026-07-18: "mejorar solo los rotos"). El botón "Nueva factura" se esconde por precedente P3=A (2026-06-21: esconder botón que estructuralmente no aplica).
- **T2** reusa el `RequestApprovalModal` que Gastón ya usa en Cobranzas → Movimientos y otras 5 pantallas, respaldado por el affordance "Pedí autorización" (2026-07-05, 4B) y el principio "misma experiencia venga de donde venga" (2026-07-08).

No se inventan preguntas para llenar huecos que no existen. Si al implementar aparece un mensaje del backend con jerga o un enum colado que la auditoría no detectó, frontend-senior lo marca y se trae acá antes de dejarlo pasar (no se decide solo).
