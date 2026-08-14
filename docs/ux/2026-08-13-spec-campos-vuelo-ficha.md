# Spec UI — Aeropuertos y horarios en la ficha del Aéreo (FASE 1)

> **Fecha:** 2026-08-13 · **Autor:** `ux-ui-disenador` · **Para:** `frontend-senior`
> **Origen:** pedido textual del dueño (13/08): *"los horarios los puedo cargar yo, esa data la
> tengo, la puedo poner cuando cargo el servicio"*. El PDF de presupuesto (maqueta v2 firmada
> 13/08, una fila por tramo) necesita estos datos ESTRUCTURADOS; hoy la ficha solo tiene fechas y
> un texto libre.
> **Archivo:** `src/TravelWeb/src/features/reservas/inline-service/FlightInlineForm.jsx`
> (+ `ServiceInlineCard.jsx` para el estado inicial y el armado del payload).
> **Estado:** FIRMADO. P1=B y P2=B (Gastón, 13/08 tarde). ⚠️ CORRECCIÓN Round 3 (14/08, orquestador):
> el Round 2 anotó mal la P1 — el mockup de §1 era la opción **A** (rechazada). La opción B que Gastón
> eligió (respuesta textual: «En "+ Más detalles"», con este dibujo en el preview que aceptó:
> `+ Más detalles ▾ / Horarios del vuelo / Sale ida · Llega ida · Sale vuelta · Llega vuelta`) pone los
> 4 horarios ADENTRO de "+ Más detalles", y la zona principal queda igual de corta que hoy.
> Ver **"Round 2 — respuestas firmadas y cuotas de hotel"** al final del
> documento: cierra P1/P2 con el detalle que cambia, y suma el plan de cuotas del Hotel (mismo
> pedido, misma fecha, otro form).

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
| 1 | Los 4 horarios (Sale/Llega, ida y vuelta) | **"+ Más detalles"**, bloque "Horarios del vuelo" (**P1=B**, corrección Round 3) |
| 2 | Aeropuerto y ciudad de salida / de llegada | "+ Más detalles", primer bloque |
| 3 | "Horarios y escalas" pasa a llamarse **"Escalas"** | "+ Más detalles", mismo lugar |

---

## 1. Los horarios van en "+ Más detalles" — bloque "Horarios del vuelo" (P1=B, corrección Round 3)

**La zona principal (`Ida · Vuelta · Pasajeros · Vuelo`) NO CAMBIA — queda igual de corta que hoy.**
Este era el mockup de la opción A, que Gastón NO eligió; se conserva solo la letra chica de los
casilleros (abajo). El lugar firmado es DENTRO de "+ Más detalles", como primer o segundo bloque
(pegado al de aeropuertos — juntos arman el tramo), con este dibujo (el preview exacto que Gastón
aceptó al elegir B):

```
+ Más detalles ▾
   Horarios del vuelo
   Sale ida  Llega ida  Sale vuelta  Llega vuelta
  ┌──────┐  ┌──────┐   ┌──────┐     ┌──────┐
  │08:30 │  │11:45 │   │19:00 │     │23:10 │   ← Round 2 (P2=B): los 4 se construyen JUNTOS
  └──────┘  └──────┘   └──────┘     └──────┘
```

- Casilleros `type="time"`, molde `INPUT_NORMAL` + label `LABEL_BASE` (los mismos que ya usa el
  form; no se inventa ninguna clase). Dos por columna, mitad y mitad.
- Labels: **"Sale"** y **"Llega"**, sin la palabra "hora" (la fecha de arriba ya da el contexto) y
  sin "(opcional)" (guía 2026-06-06: *"sin textos aclarativos ni (opcional)"*).
- **Ninguno es obligatorio.** Vacío = no informado; el PDF simplemente no muestra esa línea.
- Si no hay fecha de vuelta cargada, los dos casilleros de la columna "Vuelta" se muestran
  **apagados** (`disabled`, molde `disabled:bg-slate-50` que ya trae `INPUT_BASE`): sin fecha de
  vuelta no hay vuelta que horariar. Se prenden solos al cargar la fecha. Sin cartelito (P-15).
- **Round 2 (P2=B):** el 4º casillero ("Llega" de la vuelta) **ya no es una fase futura** — se
  construye junto con los otros tres, desde el arranque. El lugar dibujado acá es el definitivo
  (ver §8.2 para el campo del motor que lo recibe).
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
| Ida → Llega | Round 2 (P2=B): campo nuevo de llegada del tramo de ida — backend en curso. Ver §8.2. |
| Vuelta → Llega | Round 2 (P2=B): campo nuevo de llegada del tramo de vuelta — backend en curso. Ver §8.2. |

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

---

## Round 2 (2026-08-13, tarde) — respuestas firmadas y cuotas de hotel

> Cierra P1/P2 de este documento y agrega un pedido nuevo del dueño hecho el mismo día: un plan de
> cuotas para el Hotel. Distinto form (`HotelInlineForm.jsx`), mismas reglas del molde firmado.
> **Reglas que respeta:** P-15 (nada de cartelitos: si el label alcanza, no se explica) · P-16 (un
> dato no se dice dos veces: por eso "Valor por cuota" NO trae su propio selector de moneda — usa
> la moneda que el servicio ya tiene) · B.3 (una sola escala de campos/redondeo en toda la ficha:
> los dos casilleros nuevos usan `LABEL_BASE`/`INPUT_NORMAL`/`MoneyInput`, cero clases nuevas).

### §8.1 — P1=B: horarios DENTRO de "+ Más detalles" (corregido en Round 3)

⚠️ La versión anterior de esta sección decía que "el mockup de §1 ya era la opción B" — ERROR: ese
mockup era la opción **A** (horas pegadas a la fecha, a la vista), que el dueño NO eligió. La
respuesta real y textual del dueño fue **«En "+ Más detalles"»**, cuyo preview mostraba el bloque
"Horarios del vuelo" adentro del acordeón, con la parte principal igual de corta que hoy. El §1 ya
quedó corregido con el dibujo firmado. La letra chica de los casilleros (type="time", labels
"Sale"/"Llega", vuelta apagada sin fecha, sin amarillo D13) se conserva tal cual.

### §8.2 — P2=B: se construye COMPLETO, no hay "fase 2"

El dueño pidió los 4 horarios (Ida-Sale, Ida-Llega, Vuelta-Sale, Vuelta-Llega) **desde el arranque**
— no una entrega parcial. Esto cambia lo que decía la Fase 1 original:

1. **Muere la limitación "sin llegada".** El mockup de §1 ya no tiene un 4º casillero "de adorno"
   (`⋯`, apagado, "es la fase 2"): los 4 se cargan igual, mismo molde, misma fila.
2. **El backend deja de ser el cuello de botella descripto en §5.** El dueño avisó que ya se están
   agregando las columnas de llegada por tramo. Con el mismo patrón de nombres que
   `OutboundDepartureTime`/`ReturnDepartureTime` (ya existen), lo esperable es
   `OutboundArrivalTime`/`ReturnArrivalTime` (`TimeOnly`, uno por tramo) — **frontend-senior debe
   confirmar el nombre exacto contra el modelo/request real antes de programar**, esta spec no
   inventa el nombre de una columna que no verificó en código.
3. **El bug de `ArrivalTime` (§5, sigue vigente) queda esquivado para este caso puntual:** como
   ahora hay un campo dedicado por tramo, la hora de llegada de Ida/Vuelta **nunca** se manda por
   `ArrivalTime` (que sigue significando "fecha de vuelta", ver §5) — va por su columna nueva. El
   bug en sí (que `ArrivalTime` esté mal usado en otro lado del motor) sigue siendo una deuda
   aparte, fuera del alcance de esta spec.
4. **Aeropuertos/ciudad (§2) y "Escalas" (§3) no cambian.** Siguen en "+ Más detalles", sin tocar —
   el dueño no pidió nada distinto ahí.

**Tabla de campos, versión final:**

| Casillero de pantalla | Campo del motor |
|---|---|
| Ida → Sale | `OutboundDepartureTime` (ya existe) |
| Ida → Llega | `OutboundArrivalTime` (nuevo, backend en curso — confirmar nombre) |
| Vuelta → Sale | `ReturnDepartureTime` (ya existe) |
| Vuelta → Llega | `ReturnArrivalTime` (nuevo, backend en curso — confirmar nombre) |

**Qué NO hacer (agregado a §6):** no mandar la hora de llegada por `ArrivalTime` "mientras se
espera" al backend — si la columna nueva todavía no está lista, el casillero de "Llega" se guarda
vacío (como cualquier campo opcional sin cargar), nunca se reusa un campo que ya significa otra
cosa.

### §8.3 — Hotel: plan de cuotas ("6 CUOTAS 280 USD")

**Origen:** pedido textual del dueño, mismo día — quiere poder anotar, por servicio de Hotel, en
cuántas cuotas se puede pagar y de cuánto es cada una (dato informativo, para que el PDF de
presupuesto lo muestre — no reemplaza ni recalcula el precio de venta ya cargado arriba).

**Archivo:** `src/TravelWeb/src/features/reservas/inline-service/HotelInlineForm.jsx`.

**Dónde van los dos casilleros nuevos:** dentro de **"+ Más detalles"** (plegado por defecto),
como una fila propia, **entre "Estrellas del hotel" y "Confirmación del operador"**. Mismo
criterio que ya usa "Estrellas" (comentario del propio código, línea ~840): *"lo descriptivo antes
que lo operativo (PNR/dirección)"* — un plan de cuotas es descriptivo del servicio, no una gestión
con el operador, así que va en ese primer grupo, no mezclado con Confirmación/Dirección.

**Por qué NO van en la fila de Precios (zona principal, siempre visible):** esa fila ya tiene
Costo/Venta/Moneda, que SÍ participan del cálculo del total (noches × habitaciones × precio). El
plan de cuotas es un dato aparte, opcional, que no toca esa cuenta — meterlo ahí infla la fila
principal con dos casilleros que no todos los hoteles usan (P-15: si no hace falta siempre, no va
siempre a la vista).

```
+ Más detalles ▾
┌────────────────────────────────────────────────────────────────────┐
│  Estrellas del hotel            Confirmación del operador
│ ┌────────────────┐              ┌──────────────────────────┐
│ │ 4 estrellas  ▾ │              │ CONF-8842                │
│ └────────────────┘              └──────────────────────────┘
│
│  Cuotas                         Valor por cuota
│ ┌────────────────┐              ┌──────────────────────────┐
│ │ 6              │              │ US$ 280,00                │
│ └────────────────┘              └──────────────────────────┘
│
│  Dirección
│ ┌──────────────────────────────────────────────────────────────────┐
│ │ …                                                                 │
│ └──────────────────────────────────────────────────────────────────┘
└────────────────────────────────────────────────────────────────────┘
```

**Molde (reusa lo que ya existe, cero clases nuevas):**

| Casillero | Tipo | Molde igual a | Detalle |
|---|---|---|---|
| **Cuotas** | entero, texto con `inputMode="numeric"` | "Habitaciones" / "Pasajeros" | `sanitizarCantidadPositiva` (mismo helper, nunca deja tipear signo ni coma) · `LABEL_BASE` + `INPUT_NORMAL` · placeholder `Ej: 6` |
| **Valor por cuota** | `MoneyInput` | "Venta por noche" | `LABEL_BASE` + `INPUT_NORMAL` (nunca `INPUT_SUGERIDO`: no hay sugerencia del sistema acá) |

- **Sin selector de moneda propio** (P-16): el monto se entiende en la misma moneda que ya eligió
  el servicio (`form.currency`, el selector "Moneda" de la fila de Precios). No se pregunta dos
  veces.
- **Los dos son opcionales**, ninguno lleva asterisco ni validación cruzada contra el total. Si
  Cuotas queda vacío o en 0, el PDF simplemente no imprime la línea de cuotas (mismo espejo que ya
  rige para Estrellas: "si no hay dato, no se inventa nada").
- **No participan del cálculo de Venta total.** Son solo texto informativo para el PDF — la cuenta
  real sigue siendo noches × habitaciones × precio (línea 229-234 de este mismo archivo). No se
  agrega ninguna validación tipo "cuotas × valor = total": el dueño puede anotar un plan que sume
  distinto al contado (es común: cuotas con recargo) y el sistema no lo corrige ni lo avisa.
- Se abre solo "+ Más detalles" si alguno de los dos tiene valor (sumar `installmentsCount` /
  `installmentAmount` — o como se terminen llamando — a la condición `tieneDetallesExistentes` que
  ya existe en el archivo, línea ~242).

**Dependencia de backend (fuera del alcance de esta spec):** hoy `HotelInlineForm` no tiene estos
dos campos en el `form` ni el payload los manda — hace falta que el motor los reciba y los guarde
(dos campos nuevos, probablemente `int?` y `decimal?`, ambos opcionales). Nombre exacto y si el
Hotel es el único tipo de servicio que los necesita (¿Paquete también?) quedan para
`backend-dotnet-senior`/`software-architect`, no es una decisión de UX.

### §8.4 — Una pregunta, solo si hace falta

No quedó ninguna pregunta pendiente: tanto la ubicación de los horarios de vuelo (P1/P2, ya
firmadas) como la ubicación de las cuotas de hotel se resolvieron con el molde existente y los
precedentes ya firmados en esta misma spec (Estrellas del hotel, D13, P-15/P-16/B.3). Si al ver la
pantalla real Gastón quiere las cuotas en otro lugar, es mover dos casilleros — no rediseñar nada.
