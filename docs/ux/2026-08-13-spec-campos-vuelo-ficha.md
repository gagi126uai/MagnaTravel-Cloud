# Spec UI — Aeropuertos y horarios en la ficha del Aéreo (FASE 1)

> **Fecha:** 2026-08-13 · **Autor:** `ux-ui-disenador` · **Para:** `frontend-senior`
> **Origen:** pedido textual del dueño (13/08): *"los horarios los puedo cargar yo, esa data la
> tengo, la puedo poner cuando cargo el servicio"*. El PDF de presupuesto (maqueta v2 firmada
> 13/08, una fila por tramo) necesita estos datos ESTRUCTURADOS; hoy la ficha solo tiene fechas y
> un texto libre.
> **Archivo:** `src/TravelWeb/src/features/reservas/inline-service/FlightInlineForm.jsx`
> (+ `ServiceInlineCard.jsx` para el estado inicial y el armado del payload).
> **Estado:** BORRADOR — 2 preguntas abiertas (P1 y P2 al final). El layout de acá es la
> RECOMENDACIÓN; si Gastón contesta distinto, cambia solo lo que dice cada pregunta.

**Reglas que respeta (citarlas en el brief de implementación):**
P-5 (la carga vive en línea, nunca ventana flotante) · P-9/P-10 (nada escondido en un tooltip; si
algo no se puede, el motivo se lee) · P-15 (ni un cartelito aclarativo nuevo: el label alcanza) ·
P-16 (un dato no se dice dos veces → por eso "Horarios y escalas" cambia de nombre) · B.3/B.5
(molde visual único, estándar firmado 2026-08-11) · guía UX Ronda 3 (2026-06-06): *"Vuelo se
identifica con UN SOLO CAMPO de búsqueda, no con campos estructurados separados
(origen/destino/aerolínea/nº de vuelo)… lo fino va en Más detalles"* · guía UX Ronda 7
(2026-06-06): *"Más detalles queda CERRADA por defecto"* · precedente D13 (spec 2026-08-10):
casillero amarillo = valor sugerido, se apaga al tocarlo.

---

## 0. Corrección al pedido: "vuelo directo" YA EXISTE

El tilde de vuelo directo **ya está construido** (spec 2026-08-12 §1): es el desplegable
**"¿Cómo es el vuelo?"** (Sin especificar / Directo / Con escala(s)) dentro de "+ Más detalles",
al lado de "Cabina", y ya viaja al backend (`isDirect`). **No se toca, no se duplica, no se
convierte en casillero de tilde.** Los 3 casilleros de equipaje también existen ya.

Queda por construir, entonces, solo esto:

| # | Qué | Dónde |
|---|---|---|
| 1 | Hora de salida y hora de llegada (ida) + hora de salida (vuelta) | zona principal, pegadas a la fecha (**P1**) |
| 2 | Aeropuerto y ciudad de salida / de llegada | "+ Más detalles", primer bloque |
| 3 | "Horarios y escalas" pasa a llamarse **"Escalas"** | "+ Más detalles", mismo lugar |

---

## 1. Zona principal — el renglón de fechas gana un renglón de horas debajo

Hoy el renglón es: `Ida · Vuelta · Pasajeros · Vuelo`. **No se agrega ninguna columna ni se mueve
nada.** Debajo de las dos primeras columnas (y solo debajo de esas dos) aparece una fila de
casilleros de hora, angostos, alineados con su fecha:

```
 📅 Ida            📅 Vuelta         👥 Pasajeros      Vuelo
┌───────────────┐ ┌───────────────┐ ┌──────────────┐ ┌──────────────────┐
│ 10/02/2026    │ │ 15/02/2026    │ │ 2            │ │ Internacional  ▾ │
└───────────────┘ └───────────────┘ └──────────────┘ └──────────────────┘
 Sale     Llega    Sale     Llega
┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐
│08:30 │ │11:45 │ │19:00 │ │  ⋯   │   ← el 4º casillero es la FASE 2
└──────┘ └──────┘ └──────┘ └──────┘      (llegada de la vuelta)
```

- Casilleros `type="time"`, molde `INPUT_NORMAL` + label `LABEL_BASE` (los mismos que ya usa el
  form; no se inventa ninguna clase). Dos por columna, mitad y mitad.
- Labels: **"Sale"** y **"Llega"**, sin la palabra "hora" (la fecha de arriba ya da el contexto) y
  sin "(opcional)" (guía 2026-06-06: *"sin textos aclarativos ni (opcional)"*).
- **Ninguno es obligatorio.** Vacío = no informado; el PDF simplemente no muestra esa línea.
- Si no hay fecha de vuelta cargada, los dos casilleros de la columna "Vuelta" se muestran
  **apagados** (`disabled`, molde `disabled:bg-slate-50` que ya trae `INPUT_BASE`): sin fecha de
  vuelta no hay vuelta que horariar. Se prenden solos al cargar la fecha. Sin cartelito (P-15).
- **Fase 2 encaja sin rediseñar:** el 4º casillero ("Llega" de la vuelta) ya tiene su lugar
  dibujado; hoy no existe (ver §5) y mañana se prende ahí mismo.
- Amarillo D13: las HORAS nunca se pintan de amarillo — el motor de la frase del buscador
  interpreta operador y fechas, no horarios (verificado en `inlineServiceFormHelpers.js`). No
  inventar una sugerencia que no existe.

## 2. "+ Más detalles" — bloque nuevo, PRIMERO de todo

Va **arriba de "Código de reserva (PNR)"**, porque describe el vuelo (como Cabina), no la gestión.
Cuatro casilleros de texto en una fila (bloque `sm:col-span-2` con grilla interna de 4):

```
+ Más detalles ▾
┌───────────────────────────────────────────────────────────────────────────┐
│  Aeropuerto de salida   Ciudad de salida    Aeropuerto de llegada   Ciudad de llegada
│ ┌──────┐               ┌────────────────┐  ┌──────┐                ┌────────────────┐
│ │ EZE  │               │ Buenos Aires   │  │ MIA  │                │ Miami          │
│ └──────┘               └────────────────┘  └──────┘                └────────────────┘
│
│  Código de reserva (PNR)        Números de ticket
│  … (todo lo que ya existe, sin moverse)
```

- Aeropuerto: texto de 3 letras, se pasa a MAYÚSCULAS solo mientras se tipea (mismo gesto que el
  PNR, que ya hace `toUpperCase()`), `maxLength=3`. Placeholder `EZE` / `MIA`.
- Ciudad: texto libre. Placeholder `Buenos Aires` / `Miami`.
- **Nunca la palabra "IATA"** en pantalla (es jerga; el modal viejo la usaba y ese form fue
  rechazado). El label en criollo + el placeholder alcanzan.
- Ninguno obligatorio. El PDF arma "EZE · BUENOS AIRES" solo con lo que haya: si falta la ciudad
  muestra el código, si falta el código muestra la ciudad, si no hay nada no muestra la línea
  (`QuoteBudgetPdfRules.BuildFlightAirportLabel`, ya construido).
- **No van a la vista** aunque el PDF los quiera: la guía (Ronda 3) prohíbe expresamente que el
  vuelo se identifique con casilleros de origen/destino en la parte principal.

## 3. "Horarios y escalas" pasa a llamarse "Escalas"

Sigue siendo el MISMO campo de texto libre, en el mismo lugar, con el mismo dato guardado
(`scheduleNotes`) — solo cambian label y placeholder, para que no compita con los casilleros de
hora nuevos (P-16: un dato no se dice dos veces):

| | Antes | Ahora |
|---|---|---|
| Label | Horarios y escalas | **Escalas** |
| Placeholder | `Ej: Sale 10:30 AEP · Escala 1h MDZ · Llega 15:20 IGR` | `Ej: Escala de 1h en Panamá · Cambia de avión` |

Lo ya cargado en ese campo **no se toca ni se migra**: sigue mostrándose tal cual.

## 4. Estados

- **Vacío (alta):** los 3 casilleros de hora vacíos; los 2 de la vuelta apagados hasta que haya
  fecha de vuelta. Los 4 de aeropuerto/ciudad vacíos, dentro de "Más detalles" cerrado.
- **Edición:** "+ Más detalles" se abre solo si alguno de los campos nuevos tiene valor (agregar
  origen/destino a la condición `tieneDetallesExistentes` que ya existe).
- **Error del servidor / recuperable:** sin tratamiento propio — es el mismo Guardar de la ficha,
  que ya conserva lo tipeado.
- **Sin permiso:** no aplica (ninguno de estos campos es plata).

## 5. Dependencia BLOQUEANTE de backend (leerla antes de programar)

Hallazgo verificado en el código, **no es un detalle**: hoy `ServiceInlineCard.buildPayload`
manda `arrivalTime = fecha de VUELTA + "T00:00:00"`. O sea, el casillero del motor donde iría la
**hora de llegada del avión** está ocupado por la **fecha de vuelta**. El PDF lee ese mismo campo
como "llegada del tramo" para calcular duración y el "+1" rojo.

Consecuencia directa: **si la hora de salida se guarda dentro de `DepartureTime`, todo vuelo de
ida y vuelta va a imprimir en el PDF una duración absurda ("111h 30m") y un "+1"**, porque el
único freno que hoy lo evita (`LooksLikeMissingSchedule`) solo actúa cuando las DOS puntas están
en 00:00.

Por eso esta spec, en fase 1, manda las horas por los campos que el backend ya creó para
exactamente este caso ("ida y vuelta cargados como una sola línea"):

| Casillero de pantalla | Campo del motor (ya existe) |
|---|---|
| Ida → Sale | `OutboundDepartureTime` (`TimeOnly`) |
| Vuelta → Sale | `ReturnDepartureTime` (`TimeOnly`) |
| Ida → Llega | **no tiene dónde guardarse hoy** → ver **P2** |
| Vuelta → Llega | fase 2 (no existe el campo) |

Tareas que quedan para el backend/arquitecto (fuera del alcance del front):
1. Que el PDF use `OutboundDepartureTime`/`ReturnDepartureTime` para el texto "Sale 08:30hs"
   (hoy los ignora a propósito, ver la nota en `QuoteBudgetPdfRules.cs:203`).
2. Apagar duración y "+1" mientras `ArrivalTime` siga significando "fecha de vuelta" — es un bug
   latente que ya existe, independiente de esta obra.

## 6. Qué NO hacer

1. No agregar un tilde nuevo de "vuelo directo": ya existe el desplegable "¿Cómo es el vuelo?".
2. No sacar ni convertir "Escalas" (ex "Horarios y escalas") en campos estructurados: sigue texto
   libre, solo cambia el nombre.
3. No poner aeropuerto/ciudad en la zona principal (guía Ronda 3).
4. No escribir "IATA", "código IATA" ni ninguna sigla técnica en labels o placeholders.
5. No pintar de amarillo los casilleros de hora: nadie los sugiere.
6. No hacer obligatorio ninguno de estos campos, ni validar que la llegada sea posterior a la
   salida con un cartel: si el dato es raro, el PDF lo omite (regla espejo ya firmada).
7. No mandar la hora dentro de `DepartureTime`/`ArrivalTime` hasta que se resuelva §5.

## 7. Supuestos aplicados (no son preguntas)

- **"Sale"/"Llega" en vez de "Hora de salida"/"Hora de llegada":** entran en columnas angostas y
  la fecha de arriba ya da el contexto. Si Gastón quiere el texto largo, es cambiar dos strings.
- **Bloque de aeropuertos primero dentro de "Más detalles":** mismo criterio que "Estrellas del
  hotel" (spec 2026-08-12 §2): lo descriptivo antes que lo operativo (PNR/ticket).
- **Casilleros de la vuelta apagados sin fecha de vuelta:** es un apagado obvio, sin motivo escrito
  (P-9 aplica a acciones del motor, no a un casillero que depende de otro casillero de al lado).
