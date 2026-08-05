# El tipo de cambio viene sugerido al facturar en dólares — mini-spec de pantalla

> **Fecha:** 2026-08-05 · **Autor:** `ux-ui-disenador` · **Para:** `frontend-senior`
> **Estado:** ⚠️ **APLICA RECOMENDACIONES ELEVADAS A GASTÓN EN EL PAQUETE DEL DÍA.**
> La variante de justificación que se especifica acá es la **D-2c** (la justificación se pide solo
> cuando el usuario pisa el número sugerido), elevada a Gastón junto con el resto del paquete del
> 2026-08-05 y **todavía sin firma**. Las 4 preguntas del final tampoco están respondidas.
> **No se construye hasta que Gastón conteste**, salvo que el orquestador confirme lo contrario.

---

## 1. Qué cambia, en criollo

Hoy, cuando se factura en dólares, el vendedor tiene que **escribir el tipo de cambio de memoria** y
además **explicar siempre de dónde lo sacó**. A partir de este cambio, el número **ya viene puesto**
(el oficial del día, que el sistema busca solo) y **solo se pide la explicación si él lo pisa con otro
número**.

**Pantallas que se tocan (dos, ya existentes):**

| Pantalla | Archivo | Dónde |
|---|---|---|
| Emitir factura desde la ficha de la reserva | `src/TravelWeb/src/features/reservas/components/EmitirFacturaInline.jsx` | bloque "Moneda de la factura", parte de dólares (hoy líneas 1104-1151) |
| Emitir factura desde Pagos | `src/TravelWeb/src/components/CreateInvoiceModal.jsx` | mismo bloque (hoy líneas 499-569) |

**NO hay pantalla nueva, ni paso nuevo, ni ventana nueva.** El campo sigue donde está, con el mismo
vecino y el mismo botón de emitir.

## 2. De dónde sale cada decisión (nada inventado)

| Decisión | Respaldo |
|---|---|
| El número sugerido viene **ya escrito** y se pisa escribiendo encima | Guía de Gastón, **2026-07-13 P2=A** (sección "bloque de conversión de la multa") |
| Si no hay dato: casillero **vacío** + "Escribí el tipo de cambio a mano", **sin cartel de error** | Guía, **2026-07-13 P2=A** (misma respuesta) + spec de arquitectura del día, §9 (204) |
| La **fuente se resuelve sola** (oficial mientras no se toque, "a mano" al pisarlo). Nada de preguntarla | Guía, **2026-07-13 P2=A** ("no hay desplegable de fuente que preguntar") |
| La justificación se pide **solo cuando el número es "a mano"** | Precedente firmado en PROD: `ConfirmarMultaOperadorInline.jsx:771` (`fuenteTC === Manual && tipoCambioTocado`) + recomendación **D-2c** del paquete del día |
| Una sola línea gris debajo del campo, sin cartelitos extra | Guía, **2026-06-05** ("basta de formularios aclarativos") — regla **P-15** |
| El texto de esa línea lo **manda el motor**, el front no lo arma | **T-13** + contrato `GET /api/exchange-rates/suggestion` (campo `leyenda`) |
| El campo queda **siempre editable** | **P-21** |

**Diferencia declarada con el precedente:** en la pantalla de la multa, el texto gris lo arma el
front (`textoEstadoDolarBna`). Acá lo manda el motor (`leyenda`), porque **T-13** lo pide y porque el
origen del dato ahora puede ser ARCA o el respaldo del banco, cosa que el front no tiene por qué
adivinar. **La pinta que ve el usuario es idéntica**: misma posición, mismo gris chiquito, mismo tono.

---

## 3. Cómo se ve — los cinco momentos

### Momento A — el número llegó solo (caso normal, lo que va a pasar casi siempre)

```
┌─ MONEDA DE LA FACTURA ─────────────────────────────────────────────┐
│  ( ) Pesos (ARS)      (•) Dólares (USD)                            │
│ ─────────────────────────────────────────────────────────────────  │
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │    1.234,50  │                                                  │
│  └──────────────┘                                                  │
│  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos   │
│  a mano.                             ← texto del motor, gris chico │
└────────────────────────────────────────────────────────────────────┘
```
No se pide ninguna explicación: el usuario aceptó el número que le propuso el sistema.

### Momento B — el usuario pisó el número con otro

```
┌─ MONEDA DE LA FACTURA ─────────────────────────────────────────────┐
│  ( ) Pesos (ARS)      (•) Dólares (USD)                            │
│ ─────────────────────────────────────────────────────────────────  │
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │    1.300,00  │   ← lo escribió él                               │
│  └──────────────┘                                                  │
│  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos   │
│  a mano.                                                           │
│                                                                    │
│  ¿DE DÓNDE SACASTE ESTE TIPO DE CAMBIO? *   ← aparece recién ahora │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ Cotización que me pasó el operador…                           │ │
│  └───────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────┘
```

### Momento C — no hay dato del día (respuesta 204 / se cayó la consulta)

```
┌─ MONEDA DE LA FACTURA ─────────────────────────────────────────────┐
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │              │   ← vacío                                        │
│  └──────────────┘                                                  │
│  No tenemos el tipo de cambio del día. Escribí el tipo de cambio   │
│  a mano.                                                           │
│                                                                    │
│  ¿DE DÓNDE SACASTE ESTE TIPO DE CAMBIO? *   ← se pide siempre acá  │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │                                                               │ │
│  └───────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────┘
```
**Sin cartel rojo, sin globito de error, sin toast.** No es una falla: es un caso previsto.

### Momento D — mientras busca (dura un parpadeo)

```
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │              │                                                  │
│  └──────────────┘                                                  │
│  Buscando el tipo de cambio del día…                               │
```
El campo se puede escribir igual desde el primer momento (nunca se apaga).

### Momento E — el dato es de otro día (fin de semana, feriado, respaldo)

```
│  ┌──────────────┐                                                  │
│  │    1.234,50  │                                                  │
│  └──────────────┘                                                  │
│  Dólar oficial del 4 de agosto. Si ponés otro número, lo tomamos   │
│  a mano.                    ← mismo gris de siempre, sin color     │
```
**Recomendación (P1 abajo): no se cambia el color ni se agrega ícono.** La leyenda del motor ya dice
de qué día es el número; ponerle amarillo lo convertiría en una alarma que no es (P-15).

---

## 4. Reglas de comportamiento (lo que tiene que hacer el front)

1. **Cuándo se consulta.** Solo cuando la moneda elegida es **dólares**. Con pesos no se consulta
   nada y la pantalla queda exactamente como hoy. Si el usuario pasa de dólares a pesos y vuelve, se
   vuelve a consultar.
2. **Con qué fecha.** Con la **fecha de emisión del comprobante**, que en estas dos pantallas es
   **hoy en hora de Argentina**. (Ninguna de las dos deja elegir otra fecha de emisión.)
3. **Se precarga solo mientras el usuario no lo toque.** Si llega la sugerencia y el campo está sin
   tocar, se escribe. Si el usuario ya escribió algo, **nunca se le pisa lo suyo**. Si la sugerencia
   desaparece y él no había tocado nada, el campo se vacía (mismo criterio que el precedente,
   `ConfirmarMultaOperadorInline.jsx:352-357`).
4. **El número se escribe TAL CUAL vino.** No redondear, no formatear, no truncar decimales antes de
   ponerlo en el casillero ni antes de mandarlo. El motor decide si el número es "el oficial" o "a
   mano" comparándolo **exacto**: si el front lo redondea, la factura queda marcada "a mano" sin que
   nadie la haya tocado, y le pide al usuario una explicación que no corresponde.
5. **Cuándo aparece la explicación.** Aparece —y es obligatoria— cuando se cumple una de estas dos:
   - el número escrito **es distinto** del sugerido; **o**
   - **no hubo sugerencia** (Momento C: no hay número oficial que aceptar).

   Se compara **el número contra el número**, no "si tocó la tecla": si el usuario borra y vuelve a
   escribir el mismo número sugerido, el campo desaparece. Es la misma regla exacta que aplica el
   motor, así la pantalla nunca pide algo que el motor no va a exigir (T-13).
6. **Aparece y desaparece en vivo**, mientras escribe, sin recargar nada.
7. **Si el campo de explicación se esconde, su texto se descarta** y **no se manda**. Nunca viaja una
   explicación junto a un número que es el oficial.
8. **El botón de emitir** sigue apagado, como hoy, hasta que haya un tipo de cambio válido; y además
   hasta que esté escrita la explicación **solo en los casos del punto 5**.
9. **El pedido no traba la pantalla:** si tarda, si falla la red, o si el usuario no tiene permiso
   para consultar, se comporta igual que el Momento C (campo vacío + "escribilo a mano"). **Nunca**
   un cartel rojo, un toast ni un mensaje técnico.
10. **Consultas rápidas seguidas:** se conserva la espera corta (300 ms) y las dos protecciones contra
    respuestas que llegan tarde que ya tiene el hook del precedente (`useBnaUsdRateForDate` →
    generalizado a `useTipoCambioSugerido(moneda, fecha, {enabled})`). No se reinventa.
11. **Lo que se manda al guardar:** moneda, número de tipo de cambio y —solo si aplica el punto 5— la
    explicación. **Dejan de mandarse** el origen y la fecha del tipo de cambio que los dos formularios
    hoy inventan: eso lo resuelve el motor (§8.3 de la spec de arquitectura del día).

## 5. Textos exactos (nada de jerga)

| Dónde | Texto |
|---|---|
| Rótulo del campo | `Tipo de cambio ($/USD) *` (queda como está) |
| Línea gris, con sugerencia | **la que manda el motor**, tal cual, sin retocar |
| Línea gris, buscando | `Buscando el tipo de cambio del día…` |
| Línea gris, sin dato (204/error) | `No tenemos el tipo de cambio del día. Escribí el tipo de cambio a mano.` |
| Rótulo de la explicación | `¿De dónde sacaste este tipo de cambio? *` (copiado del precedente en PROD) |
| Ayuda dentro del casillero de la explicación | `Cotización que me pasó el operador, dólar de la web del banco…` |

- La explicación es **un renglón** (no un cuadro de dos líneas), hasta 500 caracteres, igual que en la
  pantalla de la multa.
- La línea gris se anuncia a los lectores de pantalla como estado (`role="status"`), como en el
  precedente; los rótulos siguen atados a su campo.

## 6. Qué NO hay que hacer

- ❌ **No** agregar ninguna ventana, paso, solapa ni acordeón nuevo.
- ❌ **No** apagar el campo del tipo de cambio en ningún momento (P-21: es una sugerencia, no una orden).
- ❌ **No** mostrar toast, cartel rojo ni "reintentar" cuando no hay dato.
- ❌ **No** nombrar en la pantalla de dónde salió el número por cuenta propia (ni "ARCA", ni "BNA
  vendedor divisa", ni "AFIP oficial"): eso lo dice la leyenda del motor y nada más.
- ❌ **No** poner un cartelito que explique la mecánica ("el sistema busca el dólar todos los días a
  tal hora…"). P-15.
- ❌ **No** inventar un aviso de "este número está muy lejos del oficial" (ver P4).
- ❌ **No** tocar el chip del pie `Factura en USD — TC: $…` ni el `≈ equivalente en pesos`: quedan
  igual, y ahora se llenan solos porque el número ya viene puesto.
- ❌ **No** tocar el camino de pesos: con pesos, las dos pantallas se ven y funcionan exactamente
  como hoy.

## 7. Nota para el que implemente (deuda que arrastran estas dos pantallas)

Los dos formularios hoy muestran, arriba de los campos, la franja azul
**"Fuente del TC: BNA vendedor divisa (dólar del día hábil anterior)"** — y **hoy eso no es verdad**:
la muestran igual aunque el número lo escriba el usuario a mano. Con este cambio, quién resolvió el
número lo dice la leyenda del motor. **La recomendación es sacarla** (ver P2), pero **no se saca
hasta que Gastón conteste.**

---

## PREGUNTAS PARA GASTON

### Tema: el tipo de cambio ya viene puesto cuando facturás en dólares
Contexto: cuando emitís una factura en dólares, el sistema ahora te pone solo el tipo de cambio
oficial del día. Vos lo podés cambiar siempre. Faltan cuatro detalles de cómo se ve.

---

**P1. Cuando el número que trae es de otro día (porque hoy es sábado, feriado, o todavía no salió el
de hoy), el renglón gris de abajo te lo dice. ¿Alcanza con eso o querés que además se pinte?**

  A) **Alcanza el renglón gris** (recomendada) — se ve igual que siempre, solo cambia la fecha del texto.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 4 de agosto. Si ponés otro número, lo tomamos a mano.
```
  B) **Amarillo, para que salte a la vista** que el número no es de hoy.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  ⚠ Ojo: es el dólar del 4 de agosto, no el de hoy.      ← amarillo
```
  *Por qué recomiendo A:* tu regla es "nada de cartelitos de más"; que el dólar sea del último día
  hábil es lo NORMAL un lunes o después de un feriado, no un problema. Si lo pintamos de amarillo,
  en poco tiempo nadie lo mira.

---

**P2. Hoy, arriba del casillero, hay una franja celeste que dice "Fuente del TC: BNA vendedor divisa
(dólar del día hábil anterior)". Con el número que ahora viene solo y su renglón explicativo, ¿la
sacamos?**

  A) **Sacarla** (recomendada) — queda el casillero con el número puesto y el renglón gris abajo.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos a mano.
```
  B) **Dejarla como está**, arriba del casillero.
```
  ┌──────────────────────────────────────────────────────────┐
  │ ⓘ Fuente del TC: BNA vendedor divisa (dólar del día      │
  │   hábil anterior)                                        │
  └──────────────────────────────────────────────────────────┘
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos a mano.
```
  *Por qué recomiendo A:* dice dos veces lo mismo, y encima **hoy miente**: esa franja aparece igual
  aunque el número lo hayas escrito vos a mano. El renglón gris de abajo dice la verdad siempre.

---

**P3. En la pantalla de facturar desde Pagos hay, debajo, un renglón que dice "Fecha del TC que se
registrará: 05/08/2026 14:30". ¿Sigue?**

  A) **Sacarlo** (recomendada) — el renglón gris de arriba ya dice de qué día es el dólar.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos a mano.
```
  B) **Dejarlo** debajo, como está hoy.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos a mano.
  Fecha del TC que se registrará: 05/08/2026 14:30
```
  *Por qué recomiendo A:* son dos fechas juntas diciendo casi lo mismo, y confunden (una es la del
  dólar, la otra la de cuándo se guardó). Con la de arriba te alcanza.

---

**P4. En la pantalla de la multa aprobaste un avisito amarillo: si escribís un dólar muy lejos del
oficial, te dice "revisalo" (no te frena). ¿Lo traemos también a facturar?**

  A) **Sí, mismo avisito** cuando el número escrito se va lejos del oficial.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   2.500,00   │
  └──────────────┘
  ⚠ El dólar que pusiste está muy lejos del oficial. Revisalo.
  ¿DE DÓNDE SACASTE ESTE TIPO DE CAMBIO? *
```
  B) **No por ahora** (recomendada) — al facturar solo aparece el pedido de explicación.
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   2.500,00   │
  └──────────────┘
  Dólar oficial del 5 de agosto. Si ponés otro número, lo tomamos a mano.
  ¿DE DÓNDE SACASTE ESTE TIPO DE CAMBIO? *
```
  *Por qué recomiendo B:* "muy lejos" es un número que nunca se terminó de definir (hoy está puesto
  un 20% provisorio en la pantalla de multas). Y acá, cuando pisás el número, ya te pedimos que
  escribas de dónde lo sacaste: ese freno solo alcanza para atajar un error de tipeo. Si querés,
  lo sumamos después.

---

**Recordá que siempre podés responder "otra cosa" y contarme cómo lo querés.**
