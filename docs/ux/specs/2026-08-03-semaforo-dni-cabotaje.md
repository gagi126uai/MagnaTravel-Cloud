# Spec UX — Semáforo de DNI vencido para vuelos de cabotaje (2026-08-03)

> **Fuente:** `docs/ux/guia-ux-gaston.md` + patrones ya construidos y firmados.
> **Ya firmado por Gastón (no se rediscute):** marca de ámbito Nacional / Internacional / sin definir
> en el SERVICIO (default sin definir; sin definir no avisa) · chip SOLO en la reserva, solapa
> Pasajeros, junto al de pasaporte · siempre visible mientras la función esté prendida · UNA llave
> general en Configuración, apagada de fábrica · chip ROJO de un solo nivel, jamás freno, silencio si
> falta el vencimiento · textos: chip "DNI vencido para el viaje" / "DNI vencido"; texto largo
> "El DNI de este pasajero se vence antes del viaje. Para volar dentro del país piden DNI vigente
> (o pasaporte vigente)."
> **Estado:** A, B y D listos para construir salvo los puntos marcados PREGUNTA (P1..P4 al final).

---

## A) Chip en la fila del pasajero (solapa Pasajeros de la reserva)

**Patrón reusado, sin inventar nada:** el chip fijo de pasaporte firmado el 31/07 (mockup D2) —
`src/TravelWeb/src/features/reservas/lib/passportAlertChip.js` + su render en
`src/TravelWeb/src/features/reservas/components/PassengerList.jsx`, con el mismo tratamiento visual
que los chips de `ReservaStatusChips`. El chip de DNI es un **hermano gemelo** de ese: mismo lugar,
mismo tamaño, mismo color rojo (rose), mismo tooltip largo, misma regla de "si el motor no mandó
alerta, no hay chip".

**Cómo se arma (espejo exacto del de pasaporte):**

- Helper nuevo `dniAlertChip.js` (hermano de `passportAlertChip.js`), función `construirChipDni(pasajero, reserva)`.
- **El front NO calcula fechas** (igual que el de pasaporte): el nivel y el texto largo vienen
  calculados del motor en el DTO del pasajero. La única decisión del front es la misma que ya toma
  hoy: si la reserva tiene fechas de viaje cargadas (`reserva.endDate || reserva.startDate`) para
  elegir entre los dos textos cortos firmados.
- **Un solo nivel** (vencido). No hay versión ámbar "vence justo" como en pasaporte.
- Texto corto: `DNI vencido para el viaje` (con fechas de viaje) / `DNI vencido` (sin fechas).
- Color: el mismo rojo del chip "Pasaporte vencido" (rose-100 / rose-700 / borde rose-200, y su par
  oscuro). Ningún color nuevo.
- Tooltip (`title`): el texto largo que manda el motor; si no llegara, respaldo con el texto firmado
  "El DNI de este pasajero se vence antes del viaje. Para volar dentro del país piden DNI vigente
  (o pasaporte vigente)."
- Nivel desconocido o alerta ausente → **no se muestra nada** (mismo tratamiento conservador del
  helper de pasaporte).
- Solo en filas con **nombre cargado** (los renglones "— sin cargar" nunca llevan chip, igual que hoy).
- `data-testid`: `chip-dni-vencido-{index}`.

**Dónde va en la fila:** dentro de la misma línea del nombre (el contenedor que ya envuelve etiqueta +
nombre + chip de pasaporte, que ya hace salto de línea solo cuando no entra). No se agrega ninguna
columna ni fila nueva.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ (A)  ADULTO 1  JUAN PEREZ  [PASAPORTE VENCIDO PARA EL VIAJE] [DNI VENCIDO    │
│                            PARA EL VIAJE]                        ✎   🗑      │
│      DNI 20123456                                                            │
└──────────────────────────────────────────────────────────────────────────────┘
```

**Convivencia de los dos chips:** ver **P1** (no está cubierto por la guía).

**Lo que el chip NO hace (firmado):** no apaga ni condiciona ningún botón — ni "Marcar emitido", ni
Guardar, ni el paso de etapa. Es solo una marca a la vista.

**Nada de banner:** el dato NO se repite como cartel arriba de la ficha (guía 2026-07-05, respuesta 2B:
ningún dato se dice dos veces entre chip y banner). Tampoco entra en la campanita: eso no está firmado.

**Dependencia de motor (para el brief de backend, no es decisión de UX):** el DTO del pasajero tiene
que traer el nivel y el texto largo ya calculados, igual que hoy trae los de pasaporte, decidiendo
adentro con la llave de Configuración y la marca Nacional del servicio. Si la llave está apagada, o
ningún servicio de la reserva está marcado Nacional, o falta el vencimiento del DNI, el motor no manda
alerta y la pantalla no muestra nada.

---

## B) Campo de vencimiento del DNI en los datos del pasajero

**Patrón reusado:** el campo "Vencimiento pasaporte" que ya existe en `PassengerFormModal.jsx`
(casillero de fecha, opcional, sin ninguna leyenda aclaratoria — regla 2026-06-05 "nada de (opcional)
ni cartelitos").

- **Etiqueta:** `Vencimiento DNI`.
- **Tipo:** casillero de fecha, igual al de pasaporte. **Opcional siempre**, nunca frena Guardar.
- **Aparece SOLO cuando el tipo de documento elegido es DNI** (firmado). Con Pasaporte / Cédula / Otro
  no existe en pantalla. Al cambiar el tipo, el campo aparece/desaparece en el momento.
- **Dónde va:** ver **P2** (la guía no lo cubre).
- **Vacío = silencio**: sin fecha cargada no hay chip, no hay aviso, no hay nada (firmado).
- **Mini-formulario en línea** (`PasajeroInlineForm`, el que se abre con [Cargar] y el de red de
  seguridad al emitir): **NO lleva este campo**. Sigue el patrón existente — ahí tampoco vive
  "Vencimiento pasaporte" — y la guía 2026-06-15 (P4b) dice que ese formulario pide solo lo que falta
  para avanzar. El vencimiento del DNI se carga desde la ficha completa del pasajero.

---

## C) Marca Nacional / Internacional en el formulario de servicio

**Cómo es hoy el formulario:** la carga de servicios es en línea (`features/reservas/inline-service/`,
un formulario por tipo: Aéreo, Hotel, Traslado, Paquete, Asistencia). Cada uno tiene los campos
imprescindibles a la vista y un **"+ Más detalles" plegado por defecto** para lo secundario (guía
2026-06-05 y Ronda 7 2026-06-06: "Cabina" del aéreo, por ejemplo, vive ahí y es opcional).

**Forma del control (esto sí está cerrado por lo firmado):** una lista desplegable de 3 opciones, en
el mismo estilo que "Moneda" o "Cabina":

```
Vuelo:  [ Sin definir ▾ ]      ← opciones: Sin definir · Nacional (dentro del país) · Internacional
```

- Default: **Sin definir** (firmado). Sin definir no avisa nunca.
- Nunca obligatorio, nunca frena Guardar.
- **En la fila del servicio guardado (ServiceList) NO se agrega nada nuevo**: ninguna etiqueta,
  ninguna pelotita. No fue pedido (regla Ronda 7: nada que Gastón no haya pedido).

**En qué tipos de servicio aparece → P3. A la vista o dentro de "Más detalles" → P4.** Las dos cosas
faltan y son las que deciden si la función se usa o queda muerta; no las decido yo.

---

## D) Interruptor en Configuración

**Patrón reusado, idéntico:** Configuración → solapa **"Operativa y Caja"** → bloque
**"Funciones avanzadas"** (`OperationalFinanceSettingsTab.jsx`), donde ya viven "Tarifario que se arma
solo desde las ventas", "Avisos de próximos inicios" y "Comisiones a vendedores". Mismo recuadro
redondeado, mismo casillero de tilde a la izquierda, mismo título en negrita con su iconito, misma
línea de ayuda gris debajo.

```
FUNCIONES AVANZADAS
┌──────────────────────────────────────────────────────────────────────────────┐
│ [ ]  🪪  Avisar cuando el DNI de un pasajero esté vencido para un viaje       │
│          dentro del país                                                      │
│          En la solapa Pasajeros de la reserva, el pasajero cuyo DNI se vence  │
│          antes del viaje queda marcado en rojo. Para volar dentro del país    │
│          piden DNI vigente (o pasaporte vigente). Solo avisa; nunca frena     │
│          nada. Apagado, no se muestra ningún aviso.                           │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Título:** "Avisar cuando el DNI de un pasajero esté vencido para un viaje dentro del país"
  (texto del propio Gastón, tal cual).
- **Ayuda:** el párrafo del dibujo de arriba.
- **Apagada de fábrica** (firmado).
- **Se prende directo, sin ventanita de "¿seguro?"** (guía Ronda 6: la ventana de confirmación queda
  solo para lo fiscal), y se guarda con el "Guardar configuración" de la pantalla, como todas las demás.
- Sin casillero de días ni parámetros extra: es una sola llave.

---

## Estados de la pantalla

| Situación | Qué se ve |
|---|---|
| Llave apagada | Nada, en ningún lado (ni chip, ni campo distinto en el servicio: el selector de ámbito se puede seguir cargando, simplemente no dispara aviso). |
| Llave prendida, ningún servicio marcado Nacional | Ningún chip. |
| Llave prendida, servicio Nacional, pasajero sin vencimiento de DNI cargado | Ningún chip (silencio firmado). |
| Llave prendida, servicio Nacional, DNI que se vence antes del viaje | Chip rojo en la fila del pasajero. |
| Reserva sin fechas de viaje cargadas | Chip rojo con el texto corto "DNI vencido". |
| Cargando la reserva | Sin tratamiento propio: el chip llega con los datos de la reserva, como el de pasaporte. |

## Qué NO hay que hacer

- No apagar, esconder ni condicionar ningún botón por este chip.
- No agregar banner/cartel en la ficha ni sección en la campanita.
- No calcular vencimientos en el front.
- No inventar colores, tamaños ni un segundo nivel ámbar.
- No poner "(opcional)" ni leyendas explicativas en el formulario del pasajero.
- No mostrar nada nuevo en la fila del servicio de la lista.

---

# PREGUNTAS PARA GASTON

### Tema: la fila del pasajero, cuando falla el pasaporte Y el DNI
Contexto: hoy la fila ya puede mostrar el chip rojo de pasaporte. Ahora se suma el de DNI. Puede pasar
que un mismo pasajero tenga los dos vencidos.

**P1. Cuando un pasajero tiene vencidos el pasaporte y el DNI, ¿qué mostramos?**
  A) Los dos chips, uno al lado del otro, pasaporte primero (RECOMENDADO: cada chip dice una cosa
     distinta y el vendedor ve el problema completo de un vistazo)
```
     ADULTO 1  JUAN PEREZ  [PASAPORTE VENCIDO PARA EL VIAJE] [DNI VENCIDO PARA EL VIAJE]
```
  B) Los dos, pero el DNI primero (porque para el vuelo de cabotaje el que importa es el DNI)
```
     ADULTO 1  JUAN PEREZ  [DNI VENCIDO PARA EL VIAJE] [PASAPORTE VENCIDO PARA EL VIAJE]
```
  C) Uno solo: un chip que junte los dos
```
     ADULTO 1  JUAN PEREZ  [DOCUMENTOS VENCIDOS PARA EL VIAJE]
```

---

### Tema: dónde va el casillero del vencimiento del DNI
Contexto: en la ficha del pasajero, "Tipo documento" y "Número de documento" están arriba, en el
bloque **Identidad**; más abajo hay otro bloque, **Datos personales**, con Fecha de nacimiento,
Vencimiento pasaporte, Nacionalidad y Género.

**P2. El casillero "Vencimiento DNI" (que aparece solo si el tipo es DNI), ¿dónde lo ponemos?**
  A) Pegado al número de documento, en Identidad (RECOMENDADO: aparece justo donde el vendedor acaba
     de elegir "DNI", no salta un dato lejos de donde tocó)
```
     IDENTIDAD
     Nombre completo *  [___________________________]
     Tipo documento     [ DNI ▾ ]
     Número de doc. *   [ 20123456 ]
     Vencimiento DNI    [ dd/mm/aaaa ]
```
  B) Abajo, en Datos personales, al lado de "Vencimiento pasaporte" (los dos vencimientos juntos)
```
     DATOS PERSONALES
     Fecha nacimiento [__]  Vencimiento DNI [__]  Vencimiento pasaporte [__]  Nacionalidad [__]
```

---

### Tema: en qué servicios se marca "Nacional / Internacional"
Contexto: el aviso es para vuelos dentro del país. Pero la marca es del servicio, y hay 5 tipos de
servicio (aéreo, hotel, traslado, paquete, asistencia).

**P3. ¿En qué formularios de servicio aparece la marca Nacional / Internacional?**
  A) Solo en el Aéreo (RECOMENDADO: el DNI vencido molesta al volar; en un hotel o un traslado no
     cambia nada, y así no engordamos 4 formularios al pepe)
```
     AÉREO:  Ruta/aerolínea · Operador · Ida · Vuelta · Pax · Costo · Venta · Moneda · [Vuelo: Sin definir ▾]
     HOTEL / TRASLADO / PAQUETE / ASISTENCIA:  sin cambios
```
  B) En el Aéreo y en el Paquete (un paquete puede ser todo dentro del país y llevar vuelos adentro)
```
     AÉREO:   ... [Vuelo: Sin definir ▾]
     PAQUETE: ... [Destino: Sin definir ▾]
     Resto:   sin cambios
```
  C) En los cinco tipos, con la misma marca para todos
```
     TODOS:  ... [Ámbito: Sin definir ▾]
```

---

### Tema: cuánto se ve la marca en el formulario del servicio
Contexto: tu regla de siempre es que el formulario muestre solo lo imprescindible y lo secundario
quede escondido detrás de "+ Más detalles" (que arranca cerrado). El problema: si la marca queda
escondida, casi nadie la va a poner, y como "sin definir no avisa", el aviso de DNI prácticamente
nunca va a aparecer.

**P4. La lista desplegable "Vuelo: Nacional / Internacional / Sin definir", ¿dónde la ponemos?**
  A) A la vista, en la misma línea de las fechas y pasajeros (RECOMENDADO: si no se ve, no se marca,
     y sin marca el aviso no existe; es un solo desplegable chico)
```
     Ida [__/__/__]   Vuelta [__/__/__]   Pasajeros [ 2 ]   Vuelo [ Sin definir ▾ ]
     Costo [____]     Venta [____]        Moneda [ ARS ▾ ]
     + Más detalles
```
  B) Escondida dentro de "+ Más detalles", como la Cabina (el formulario queda más limpio, pero hay
     que acordarse de abrirlo)
```
     Ida [__/__/__]   Vuelta [__/__/__]   Pasajeros [ 2 ]
     Costo [____]     Venta [____]        Moneda [ ARS ▾ ]
     − Menos detalles
        PNR [____]  Nº ticket [____]  Cabina [ Sin especificar ▾ ]  Vuelo [ Sin definir ▾ ]
```
  C) A la vista, pero solo cuando el vuelo todavía está "Sin definir" (una vez marcado, se va a
     "Más detalles" y el formulario queda limpio)
```
     Ida [__/__/__]   Vuelta [__/__/__]   Pasajeros [ 2 ]   Vuelo [ Sin definir ▾ ]   ← mientras no se marque
     Ida [__/__/__]   Vuelta [__/__/__]   Pasajeros [ 2 ]                              ← ya marcado: pasa a "Más detalles"
```
