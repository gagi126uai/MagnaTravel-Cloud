# Cambio 4 — Pasajeros: alta y edición pasan del modal a EN LÍNEA

> Estado: **APROBADO por Gastón (2026-07-05)** — respondió P9-A y P10-A (las recomendadas).
> Sin preguntas abiertas. Listo para frontend-senior.
> Reglas en la guía: sección "Pasajeros: alta y edición EN LÍNEA (muere el modal) (2026-07-05)".
> Decisión cerrada de Gastón (2026-07-05): sacar el modal, todo en línea. Componentes:
> `PassengerFormModal.jsx` (a jubilar), `PasajeroInlineForm.jsx` (a extender), lista de pasajeros de la reserva.

## Qué dice la guía que aplica (esto NO se pregunta)

- **El modal me parece horrible → todo en línea.** Regla dura repetida: carga de servicios (2026-06-05
  propuesta C), cobro (2026-06-09 P3), cancelación (ADR-035 C), pago a proveedor (2026-06-27),
  cuentas bancarias (2026-06-28). Los pasajeros entran en la misma familia. El modal de nombres al
  avanzar ya murió (2026-06-15 P3).
- **Ya existe el mini-formulario en línea** `PasajeroInlineForm`, que aparece **debajo**, nunca en
  ventana flotante (2026-06-15 P4b), y los renglones vacíos "Adulto 1 — sin cargar" con botón
  **[Cargar]** (2026-06-15 P9). Ese es el molde a reusar.
- **Una sola forma para crear y editar** (patrón validado en servicios, 2026-06-06): la misma ficha
  en línea sirve para alta y para edición, con los datos puestos cuando editás.
- **Doble autocompletado: SE CONSERVA en el inline** (guía 2026-06-23, "Pasajero reutilizable",
  línea :612). La regla dice textual que al agregar un pasajero **"en PassengerFormModal / mini-form"**
  hay que **sugerir pasajeros que ya viajaron antes (base propia, no solo padrón AFIP) y autocompletar**.
  O sea: las **dos** fuentes —base histórica propia + padrón AFIP— van en el formulario en línea.
  **No es una pregunta: la guía ya lo decidió.** El inline debe traer:
  - búsqueda en la base propia al tipear nombre o documento (con su lista de sugerencias), y
  - el botón de lupa del padrón AFIP en el campo documento (manual, como hoy en el modal),
  - más el aviso de duplicado si el mismo documento ya está en la reserva (dedup suave, ya decidido).

## Decisiones tomadas (Gastón, 2026-07-05)

- **P9-A:** editar un pasajero abre la **misma fila** en el lugar (patrón "confirmar costo en la misma
  fila"), no una ficha aparte. El alta (botón "Agregar Pasajero" / "[Cargar]") abre la misma ficha vacía.
- **P10-A:** a la vista **solo nombre + tipo y N° de documento**; el resto (fecha de nacimiento,
  nacionalidad, teléfono, email, género, notas) detrás de **"Más detalles"**, cerrado por defecto.

## Diseño final aprobado

**Edición en la misma fila** + **a la vista solo nombre y documento, el resto en "Más detalles"**.
Ejemplo:

```
Solapa Pasajeros (lista):
┌──────────────────────────────────────────────────────────────────────────────┐
│ Pasajeros                                    2 de 3 nombres cargados            │
├──────────────────────────────────────────────────────────────────────────────┤
│ 👤 García, Juan       DNI 30.111.222                        [Editar] [Borrar]  │
│ 👤 García, Ana        DNI 31.333.444                        [Editar] [Borrar]  │
│ 👤 Menor 1 — sin cargar                                     [ Cargar ]         │
│                                                        [ + Agregar Pasajero ]  │
└──────────────────────────────────────────────────────────────────────────────┘

Al tocar [Editar] en "García, Juan" (P9-A: la fila se abre en el lugar):
┌──────────────────────────────────────────────────────────────────────────────┐
│ 👤 Editar pasajero                                                             │
│  [ Nombre y apellido            ]  [ DNI ▾ ] [ N° documento 🔍 ]              │
│  Más detalles ▾                                                                │
│  [ Guardar ]  [ Cancelar ]                                                     │
└──────────────────────────────────────────────────────────────────────────────┘

Al tocar [+ Agregar Pasajero] o [Cargar]: la MISMA ficha, vacía, aparece debajo del renglón.
Al tipear nombre/documento (doble autocompletado, ya decidido en la guía :612):
│  [ Garc|                        ]                                              │
│  ┌── Pasajeros de viajes anteriores ─────────────────────────────┐            │
│  │ García, Juan · DNI 30.111.222                                 │            │
│  │ García, Ana  · DNI 31.333.444                                 │            │
│  └───────────────────────────────────────────────────────────────┘            │
│   … y el botón 🔍 del padrón AFIP sigue en el campo documento.                 │

Con "Más detalles ▾" abierto (los campos que hoy están en el modal):
│  [ Fecha nac. ] [ Nacionalidad ] [ Teléfono ] [ Email ] [ Género ▾ ] [ Notas ]│
```

## Estados de la pantalla

- **Guardando:** el botón muestra "Guardando…" con spinner y no deja doble envío (ya está en el inline).
- **Error al guardar:** la ficha queda abierta con todo lo cargado + error en línea (no toast que
  haga perder datos) — regla Ronda 2 2026-06-06, ya implementada en el inline.
- **Duplicado:** aviso suave "Ese documento ya está en la reserva" (dedup, ya decidido 2026-06-23).
- **Solo lectura (estados terminales):** en Perdida / Anulada / En viaje / Finalizada los botones
  Agregar / Editar / Borrar pasajero se ocultan (ADR-035 A-ter, 2026-06-19). La lista queda informativa.

## Qué NO hay que hacer

- NO abrir ninguna ventana flotante para alta ni edición (muere `PassengerFormModal` como modal).
- NO perder el doble autocompletado (base propia + padrón AFIP): la guía manda conservarlo (:612).
- NO decidir por cuenta propia qué campos son obligatorios/visibles: eso lo fija Gastón en P10.
- NO perder el aviso de duplicado ni la etiqueta de "sin cargar" de los renglones.

## Dependencia técnica (no es decisión de UX)

- El `PasajeroInlineForm` hoy solo maneja nombre + documento (+ fecha para asistencia) y **no** tiene
  el doble autocompletado. Hay que portarle la búsqueda histórica + la lupa del padrón + los campos de
  "Más detalles" que salgan de P10. La lógica de búsqueda ya existe (`pasajeroSearchLogic.js`,
  usada por el modal); se reusa, no se reescribe.
