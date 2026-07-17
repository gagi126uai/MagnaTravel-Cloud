# Editar los datos del cliente desde su ficha — solapa "Datos" + aviso fiscal (espejo del operador)

> **Fecha:** 2026-07-17
> **Pantalla:** `CustomerAccountPage.jsx` (cuenta corriente del cliente).
> **Estado:** APROBADA por Gastón tal cual (sin preguntas: todo es espejo de decisiones ya firmadas
> para la ficha del OPERADOR). Ajuste del backend incorporado (candado solo del CUIT).
> **Gate UX:** cumplido. `frontend-senior` implementa esta spec al pie de la letra; cualquier desvío
> por costo técnico o regla de negocio se le repregunta a Gastón ANTES de desviarse.
> **Origen:** callejón sin salida reportado por Gastón (molesto): emitir una devolución dice
> "Completá la condición fiscal en la ficha del cliente para poder continuar", pero la ficha
> (rediseñada el 2026-07-16 con el extracto profesional) NO tenía forma de editar datos fiscales —
> solo el modal del listado de clientes los editaba. El mensaje mandaba a un lugar sin la acción.

---

## 0. Criterio de diseño

**Espejo exacto de la ficha del operador** (`SupplierAccountPage`), que ya tiene:
- solapa **"Datos"** con edición EN LÍNEA (`SupplierInlineEditForm`, decisión 2026-06-28 P6=A), y
- desde 2026-07-16, el **banner ámbar** "Faltan los datos fiscales de este operador." con botón
  "Completar datos" que abre esa solapa.

Ninguna decisión se inventó: todo sale de la guía y/o del patrón del operador ya aprobado. Siguen
valiendo "el modal me parece horrible" (todo EN LÍNEA), "el front no deduce, lo dice el backend"
(2026-07-03), y el patrón de candado (ADR-035 A: campo bloqueado + un cartel que explica el estado).

---

## 1. Layout final

Se agrega una **5ta solapa "Datos"** (última) y un **aviso ámbar de una línea** entre la foto de
saldo y la barra de solapas.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ ←  Fam. García        ✉ garcia@mail.com   ☎ 11-4444-5555   CUIT/DNI 20-…      │
│                                                          [ + Nuevo presupuesto ]│
├─────────────────────────────────────────────────────────────────────────────┤
│  SALDO DE LA CUENTA           (foto de saldo — spec 2026-07-16, sin cambios)  │
└─────────────────────────────────────────────────────────────────────────────┘

  ⚠ Faltan los datos fiscales de este cliente. Completá su condición      [Completar
     fiscal para poder facturar, anular y emitir devoluciones sin trabas.   datos]
      ↑ franja ámbar de UNA línea — SOLO si overview.hasPendingTaxData === true

  [ Reservas ] [ Estado de cuenta ] [ Facturación ] [ Datos bancarios ] [ Datos ]
                                                                          ═══════
```

- El aviso ámbar calca al del operador (`supplier-missing-tax-condition-banner`): franja
  `rounded-xl border-amber-200 bg-amber-50`, ícono `AlertTriangle`, texto en negrita + explicación,
  y a la derecha el botón "Completar datos".
- "Completar datos" hace `setActiveTab("datos")` (misma página, abre la solapa nueva).
- La solapa "Datos" va **última**, después de "Datos bancarios" (mismo orden relativo que en el
  operador, donde "Datos" cierra la fila de solapas).

---

## 2. La solapa "Datos" (formulario en línea, sin ventana)

Edición EN LÍNEA dentro de la solapa, nunca ventana flotante. Reemplaza al modal del listado
(`CustomerFormModal`) como lugar para editar **desde la ficha**; el modal del listado puede seguir
existiendo para el alta (esto no lo toca).

**Campos a la vista** (subconjunto del operador; el cliente NO tiene moneda por defecto, plazo de
pago, "cómo trabaja", ajuste por el dólar ni comportamiento con multas → **no hay "Más detalles"**):

```
┌── Datos del cliente ──────────────────────────────────────────────┐
│  Nombre completo *                                                 │
│  [ Fam. García                                              ]      │
│                                                                    │
│  Documento / Pasaporte              CUIT / DNI                     │
│  [ 30111222              ]          [ 20-30111222-3    ] [🔍 AFIP] │
│                                                                    │
│  Condición fiscal (AFIP) *                                         │
│  [ Consumidor Final                                    ▾ ]         │
│      Responsable Inscripto · Monotributo · Exento · Cons. Final    │
│                                                                    │
│  Email                              Teléfono                       │
│  [ garcia@mail.com       ]          [ 11-4444-5555     ]           │
│                                                                    │
│  Dirección                                                         │
│  [ Av. Corrientes 1234, CABA                           ]           │
│                                                                    │
│  [x] Cliente activo — aparece en buscadores; inactivo mantiene     │
│      su historial                                                  │
│  ─────────────────────────────────────────────────────────────    │
│                                              [ Guardar cambios ]    │
└────────────────────────────────────────────────────────────────────┘
```

**Orden de campos:** Nombre completo\* → Documento/Pasaporte → CUIT/DNI (+ botón búsqueda AFIP) →
Condición fiscal\* → Email → Teléfono → Dirección → Cliente activo.

**Notas técnicas (fidelidad al modelo del cliente, no son decisiones de UX):**
- Datos del cliente por `GET /api/customers/{id}`: `taxId`, `taxConditionId`, `documentType`,
  `documentNumber`, `email`, `phone`, `address`, `isActive`.
- La condición fiscal usa `taxConditionId` (entero): **1** = Responsable Inscripto · **6** =
  Monotributo · **4** = Exento · **5** = Consumidor Final (default 5). NO usa el string
  `taxCondition` del operador.
- El botón de búsqueda AFIP sobre el CUIT (que ya existe en `CustomerFormModal`) se conserva en
  línea: ayuda a completar los datos fiscales, que es el objetivo de esta pantalla.
- Únicos campos obligatorios para guardar: **Nombre completo** y **Condición fiscal**.

---

## 3. El candado del CUIT cuando el cliente ya tiene facturas (SOLO el CUIT)

Regla fiscal: un cliente con comprobantes ya emitidos no puede cambiar su **CUIT** (los comprobantes
salieron con ese CUIT). **La condición fiscal SIGUE SIEMPRE editable** — solo el CUIT se bloquea.
Que el candado aplique lo decide el BACKEND: `overview.taxIdLocked` (bool). El front lo lee, no lo
calcula ("el front no deduce, lo dice el backend", 2026-07-03; fase 4 "la pantalla obedece al
backend"). Tratamiento visual = patrón del candado (ADR-035 A / fase 4: el campo se muestra
deshabilitado, no se esconde).

Cuando `taxIdLocked === true`:

```
│  Documento / Pasaporte              CUIT / DNI                     │
│  [ 30111222              ]          [ 20-30111222-3  ] 🔒 (gris)   │
│                                       (botón AFIP también gris)    │
│                                                                    │
│  🔒 El CUIT no se puede cambiar acá (los comprobantes ya salieron   │
│     con ese CUIT); si el titular cambió de CUIT, registrá un        │
│     cliente nuevo.                                                  │
│                                                                    │
│  Condición fiscal (AFIP) *          ← SIEMPRE editable             │
│  [ Responsable Inscripto                             ▾ ]           │
```

- Se deshabilitan **solo** el campo CUIT/DNI y el botón de búsqueda AFIP. **La condición fiscal, el
  documento/pasaporte, el contacto, la dirección y el activo siguen editables**, y "Guardar cambios"
  funciona para esos.
- **Una** sola línea explicativa debajo del CUIT, en criollo, texto EXACTO:
  *"El CUIT no se puede cambiar acá (los comprobantes ya salieron con ese CUIT); si el titular cambió
  de CUIT, registrá un cliente nuevo."*
- Sin jerga, sin IDs, sin "hablá con administración" (regla 2026-07-08).

---

## 4. El aviso ámbar "Faltan los datos fiscales" (espejo del operador)

- **Cuándo aparece:** cuando `overview.hasPendingTaxData === true` (bool que expone
  `CustomerAccountOverviewDto`). Es el MISMO veredicto que hoy traba la emisión de la devolución. El
  front solo lo pinta; **no se enciende con `!taxConditionId`** (el cliente siempre defaultea a
  Consumidor Final, así que esa condición nunca dispararía).
- **Dónde vive:** franja de una línea entre la foto de saldo y la barra de solapas. Es solo
  informativo: **no bloquea nada** de esta pantalla (igual que en el operador).
- **Texto EXACTO:** *"**Faltan los datos fiscales de este cliente.** Completá su condición fiscal
  para poder facturar, anular y emitir devoluciones sin trabas."*
- **Botón:** "Completar datos" → `setActiveTab("datos")`.
- Cuando el cliente queda completo (el backend deja de marcar `hasPendingTaxData` tras guardar y
  recargar el overview), el aviso desaparece solo.

Con esto se cierra el callejón: la devolución sigue avisando "completá la condición fiscal", y ahora
ese mismo aviso (banner) + el botón llevan a un lugar donde SÍ se completa.

---

## 5. Estados de la pantalla

| Estado | Qué se ve |
|--------|-----------|
| **Datos fiscales completos** (`hasPendingTaxData === false`) | Sin banner ámbar. Solapa "Datos" con todo editable (salvo el CUIT si `taxIdLocked`). |
| **Datos fiscales incompletos** (`hasPendingTaxData === true`) | Banner ámbar arriba + "Completar datos". La solapa "Datos" abre lista para completar la condición fiscal. |
| **CUIT bloqueado** (`taxIdLocked === true`) | Solo el CUIT + botón AFIP deshabilitados + línea explicativa (§3). Condición fiscal y el resto, editables. |
| **Guardando** | Botón "Guardando…", deshabilitado, no permite doble envío. |
| **Guardado OK** | Aviso de éxito: **"Datos del cliente guardados correctamente."** Se recarga el overview (para que el encabezado y el banner reflejen los datos nuevos). |
| **Error al guardar** | La ficha queda abierta con TODO lo cargado intacto + cartel rojo arriba de los botones (**"No se pudo guardar. Revisá la conexión y probá de nuevo."**); se reintenta en el mismo botón (guía Ronda 2, 2026-06-06). Si el backend devuelve un motivo de negocio (ej. CUIT inválido), se muestra en criollo, sin IDs/enums/texto de código. |
| **Sin permiso de editar** (`clientes.edit` falso) | Solapa "Datos" en solo lectura: campos deshabilitados, sin botón "Guardar cambios". |

---

## 6. Qué NO hacer

- **No** ventana flotante para editar (todo en línea).
- **No** encender el banner con `!taxConditionId`, ni calcular en el front si "faltan datos
  fiscales" o si el CUIT está bloqueado: son veredictos del backend (`hasPendingTaxData` /
  `taxIdLocked`).
- **No** bloquear la condición fiscal: el candado es SOLO del CUIT.
- **No** meter en la solapa "Datos" del cliente los campos del operador (moneda por defecto, plazo
  de pago, cómo trabaja, ajuste por el dólar, comportamiento con multas). El cliente no los tiene.
- **No** mostrar IDs/GUID, el entero crudo de `taxConditionId`, ni texto de error técnico: siempre
  en criollo.
- **No** derivar a "administración" en el mensaje del candado (regla 2026-07-08).
- **No** perder lo cargado ante un error recuperable.

---

## 7. Datos del backend (ya expuestos, no son decisiones de UX)

1. `CustomerAccountOverviewDto.hasPendingTaxData` (bool) → enciende/apaga el banner ámbar.
2. `CustomerAccountOverviewDto.taxIdLocked` (bool) → deshabilita SOLO el CUIT + botón AFIP.
3. `GET /api/customers/{id}` → `taxId`, `taxConditionId`, `documentType`, `documentNumber`, `email`,
   `phone`, `address`, `isActive`. El PUT de edición del cliente (el que ya usa el modal del
   listado) se reutiliza desde la solapa en línea; sin endpoint nuevo. Al guardar, el front recarga
   el overview.

---

## 8. Resumen para implementadores

- Archivo: `src/TravelWeb/src/features/customers/pages/CustomerAccountPage.jsx` — agregar la solapa
  `{ key: "datos", label: "Datos", count: null }` al final del array de solapas + su bloque de
  contenido, y el banner ámbar entre la foto de saldo y la barra de solapas (condicionado por
  `overview.hasPendingTaxData`).
- Componente nuevo sugerido: `DatosClienteTab.jsx` (o `CustomerInlineEditForm.jsx`), calcado de
  `SupplierInlineEditForm` de `SupplierAccountPage.jsx`, recortado a los campos del cliente y con el
  candado atado a `taxIdLocked` (solo CUIT).
- Banner: calcar el bloque `supplier-missing-tax-condition-banner` (SupplierAccountPage
  ~1718-1737), cambiando la condición a `overview.hasPendingTaxData` y el texto por el del §4.
- El PUT reusa el endpoint que ya usa `CustomerFormModal`; no duplicar la validación de negocio en
  el front (solo obligatoriedad de Nombre + Condición fiscal para habilitar Guardar).
- Gate final obligatorio: `data-exposure-reviewer` (que no se filtre enum/ID/error técnico) +
  `frontend-reviewer` (que cumpla esta spec y la guía).
