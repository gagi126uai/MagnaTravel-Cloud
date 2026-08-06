# La ayuda invisible del tipo de cambio — spec de pantalla

> **Fecha:** 2026-08-06 · **Autor:** `ux-ui-disenador` · **Para:** Gastón (firma) → después `frontend-senior`
> **Estado:** ✅ **FIRMADA por Gastón el 2026-08-06** (eligió sobre maquetas HTML + multiple choice):
> **P1=A** (renglón gris: solo "Dólar oficial del D de mes.", muere la muletilla) ·
> **P2=A** (modo práctica: el casillero del TC NO EXISTE — el motor completa solo; tampoco el
> "≈ equivalente en pesos") · **P3=A** (número sobre el tope: se acomoda solo al máximo con la
> línea gris "En la factura entra hasta $X." — excepción a P-21 FIRMADA explícitamente; lo que
> el usuario quiso poner queda en el rastro interno) · **P4=A** (la brecha cobrado-vs-facturado
> vive en Reportes → solapa "Facturas en dólares", permiso reportes.view, exportable) ·
> **P5=A con ajuste del dueño: la palabra visible es "ARCA" (nombre actual del organismo),
> SOLO en facturación — "AFIP" se renombra a "ARCA" ahí, y ARCA/homologación/validación se
> barren de todo el resto** · **P6=A** (el "dólar para facturar" del inicio no se muestra en
> modo práctica). LISTA PARA CONSTRUIR.

---

## 0. El mandato que ordena todo esto

Palabras textuales del dueño (2026-08-05, en vivo):

> *"El sistema tiene que contemplar todo [pesos y dólares, cualquier condición fiscal]; diseñemos una
> ayuda para que esto no afecte a nadie y **ni se entere** el que opera el sistema, y al contador le
> cierren los números."*

Y antes, mirando la pantalla de facturar:

> la leyenda *"Dólar de prueba de ARCA…"* **no le gusta**; el número de práctica **preferiría que ni se
> muestre**.

De ahí salen las tres partes de esta spec: **A** el casillero del tipo de cambio, **B** dónde queda
guardada la diferencia entre lo que se cobró y lo que se facturó, **C** las palabras que desaparecen.

## 1. Qué problema resuelve, en criollo

Cuando vendés en dólares pasan tres cosas que hoy el vendedor **tiene que entender** y no debería:

1. **La factura en dólares lleva un tipo de cambio con techo.** No se puede declarar cualquier número:
   hay un máximo permitido por día. Si el vendedor escribe uno más alto, el comprobante **rebota** y él
   ve un error que no sabe arreglar.
2. **Mientras el sistema está en modo de práctica**, el número que hay que declarar es uno **de
   juguete**. Hoy la pantalla se lo explica con una leyenda larga. Es ruido puro.
3. **Casi siempre cobrás a un dólar y facturás a otro** (el del techo). Esa diferencia **existe, es
   normal**, y el contador la necesita ordenada. El vendedor **no** la necesita ver nunca.

La ayuda invisible = el sistema resuelve los tres, **sin ventanas, sin pasos nuevos, sin sermones**, y
deja el rastro ordenado en un lugar donde lo encuentra el contador.

## 2. De dónde sale cada decisión

| Decisión | Respaldo |
|---|---|
| El número viene precargado y editable; se pisa escribiendo encima | Guía **2026-07-13 P2=A** + spec firmada del 2026-08-05 |
| Nada de cartelitos que expliquen la mecánica | **P-15** + guía 2026-06-05 ("basta de formularios aclarativos") |
| El texto gris de abajo lo arma el motor, el front no deduce | **T-13** |
| Lo que solo INFORMA va gris y en una línea; el color se reserva para lo que pide hacer algo | Guía **2026-08-03 P11=A** |
| Nunca aparecen nombres internos ni jerga de la maquinaria | **T-5**, **P-17** |
| Un dato no se dice dos veces | **P-16** |
| El sistema sugiere, no decide | **P-21** ⚠️ *(la parte A4 pide una excepción firmada a esta regla — ver P3)* |

---

# PARTE A — El casillero del tipo de cambio al facturar en dólares

Vale **igual** para las dos pantallas que ya existen: *Emitir factura* dentro de la ficha de la reserva
(`EmitirFacturaInline.jsx`) y *Emitir factura* desde Pagos (`CreateInvoiceModal.jsx`). No hay pantalla
nueva, ni paso nuevo, ni ventana nueva. El casillero sigue donde está.

## A1. Productivo, con número del día (lo que va a pasar casi siempre)

```
┌─ MONEDA DE LA FACTURA ─────────────────────────────────────────────┐
│  ( ) Pesos (ARS)      (•) Dólares (USD)                            │
│ ─────────────────────────────────────────────────────────────────  │
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │   1.234,50   │  ← ya viene puesto, se puede pisar               │
│  └──────────────┘                                                  │
│  Dólar oficial del 6 de agosto.          ← gris chico, UNA línea   │
└────────────────────────────────────────────────────────────────────┘
```

**Recomendación (P1): la leyenda queda en su mínimo — qué dólar es y de qué día. Punto.**
Se **saca** la muletilla que hoy va pegada: *"Si ponés otro número, lo tomamos a mano."* Eso es un
sermón sobre cómo funciona el sistema (P-15), y además ya se explica solo: si el vendedor pisa el
número, le aparece el renglón que le pide de dónde lo sacó.

No se pide ninguna explicación en este estado: aceptó el número que le propuso el sistema.

## A2. Productivo, sin número (fin de semana raro, primera carga, se cayó la consulta)

```
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │              │  ← vacío, escribible desde el primer segundo     │
│  └──────────────┘                                                  │
│  Escribí el tipo de cambio.                                        │
│                                                                    │
│  ¿DE DÓNDE SACASTE ESTE TIPO DE CAMBIO? *                          │
│  ┌───────────────────────────────────────────────────────────────┐ │
│  │ Cotización que me pasó el operador…                           │ │
│  └───────────────────────────────────────────────────────────────┘ │
```

**Cambio de texto:** hoy dice *"No tenemos el tipo de cambio del día. Escribí el tipo de cambio a
mano."* Se queda solo con **"Escribí el tipo de cambio."** — la primera mitad es el sistema
disculpándose por algo que al vendedor no le suma; la segunda mitad es la única instrucción útil.

**Sin cartel rojo, sin globito, sin toast.** No es una falla: es un caso previsto.

## A3. Modo de práctica — el número de juguete NO SE VE

**Recomendación (P2): el casillero del tipo de cambio directamente no se dibuja.**

```
┌─ MONEDA DE LA FACTURA ─────────────────────────────────────────────┐
│  ( ) Pesos (ARS)      (•) Dólares (USD)                            │
│ ─────────────────────────────────────────────────────────────────  │
│  (acá no hay nada: ni casillero, ni renglón gris, ni explicación)  │
└────────────────────────────────────────────────────────────────────┘

        … el resto de la pantalla, idéntico …

                              [ Emitir factura ]
```

- El vendedor elige dólares y emite. **El motor completa el tipo de cambio solo**, con el número que
  el comprobante de práctica exige, y por eso el comprobante **nunca rebota**.
- **No** se pide explicación (no hay número que pisar).
- **No** aparece ninguna palabra: ni "práctica", ni "prueba", ni "ARCA", ni "automático".
- **También se esconde el "≈ equivalente en pesos" del pie** mientras dure ese modo: sería un número
  de juguete puesto en pantalla como si fuera plata de verdad. (Ver P2, opción de la que puede
  disentir.)

*Por qué así y no un casillero apagado con la palabra "Automático":* un casillero gris y trabado es
una pregunta muda ("¿por qué no puedo tocar esto?") que el vendedor no puede contestar sin que
alguien le explique el modo de práctica — o sea, exactamente lo que el mandato pide evitar.

## A4. Escribe un número más alto que el techo — **acá vive la ayuda invisible**

Hoy: el vendedor escribe 1.500 (el dólar al que cobró de verdad), emite, y el comprobante **rebota**
con un error que no entiende. Eso es una interrupción de las peores: llega tarde, después de darle
"Emitir", y no dice qué hacer.

**Recomendación (P3): el número se acomoda solo al máximo que entra, apenas termina de escribir
(cuando sale del casillero), con UNA línea gris. Sin ventana, sin botón, sin frenar nada.**

Mientras escribe (no pasa nada, no salta nada):

```
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │   1.500,00   │  ← escribiendo, tranquilo                        │
│  └──────────────┘                                                  │
│  Dólar oficial del 6 de agosto.                                    │
```

Al salir del casillero (un parpadeo, sin recargar nada):

```
│  TIPO DE CAMBIO ($/USD) *                                          │
│  ┌──────────────┐                                                  │
│  │   1.235,50   │  ← quedó el máximo que entra en la factura       │
│  └──────────────┘                                                  │
│  En la factura entra hasta $ 1.235,50.                             │
└────────────────────────────────────────────────────────────────────┘
```

- **No** se le pide explicación: el número que quedó **no lo eligió él**, se lo puso el sistema.
- **No** hay cartel amarillo, ni rojo, ni "¿seguro?". Es gris: solo informa (P11=A).
- **No** se toca nada más de la pantalla: el botón de emitir sigue disponible, el equivalente en pesos
  se recalcula solo.
- **El número que él quiso poner (1.500) queda guardado en el rastro interno.** No se muestra en
  ningún lado del formulario. Sirve para la Parte B y para auditoría.

⚠️ **Esto necesita una excepción firmada:** la regla **P-21** dice que el sistema *sugiere, no decide,
y nunca pisa lo que el usuario ya cargó*. Acá lo pisa. Lo hago explícito en la **P3** para que Gastón
decida: es la única forma de que "ni se entere" y de que la factura no rebote.

## A5. Reglas de comportamiento (lo que tiene que hacer la pantalla)

1. **Solo con dólares.** Con pesos no se consulta nada y la pantalla queda exactamente como hoy.
2. **Se precarga mientras el usuario no lo toque.** Si él ya escribió algo, nunca se le pisa lo suyo
   —salvo el caso A4, que es el techo—.
3. **El número se manda tal cual vino.** No redondear, no reformatear: el motor compara **exacto**
   para saber si el vendedor aceptó la sugerencia o puso lo suyo.
4. **La explicación se pide** cuando (a) el número escrito es distinto del sugerido **y entra dentro
   del techo**, o (b) no hubo número sugerido (A2). **Nunca** cuando el sistema acomodó al techo (A4)
   ni en modo de práctica (A3).
5. **Aparece y desaparece en vivo** mientras escribe. Si el campo de explicación se esconde, su texto
   se descarta y no viaja.
6. **Nada traba la pantalla.** Si la consulta tarda, falla o el usuario no tiene permiso, se comporta
   como A2 (vacío + "Escribí el tipo de cambio"). Nunca un cartel rojo ni un mensaje técnico.
7. **El techo lo dice el motor**, no lo calcula la pantalla (T-13). El front recibe "hasta cuánto
   entra" junto con la sugerencia; jamás le suma $1 a nada por su cuenta.

## A6. Textos exactos

| Dónde | Texto |
|---|---|
| Rótulo | `Tipo de cambio ($/USD) *` (queda como está) |
| Gris, con número | `Dólar oficial del 6 de agosto.` / `Dólar Banco Nación del 5 de agosto.` (lo arma el motor) |
| Gris, buscando | `Buscando el tipo de cambio…` |
| Gris, sin número | `Escribí el tipo de cambio.` |
| Gris, acomodado al techo | `En la factura entra hasta $ 1.235,50.` |
| Rótulo de la explicación | `¿De dónde sacaste este tipo de cambio? *` |
| Ayuda dentro de la explicación | `Cotización que me pasó el operador, dólar de la web del banco…` |

## A7. Qué NO hay que hacer

- ❌ Ninguna ventana, paso, solapa ni acordeón nuevo.
- ❌ Ningún cartel de color en este bloque: el único texto es gris de una línea.
- ❌ No apagar el casillero en modo productivo (P-21).
- ❌ No nombrar la maquinaria: ni "ARCA", ni "homologación", ni "validación", ni "tope fiscal", ni
  "modo prueba", ni el número de un error.
- ❌ No pedirle explicación por un número que el sistema le acomodó.
- ❌ No tocar el camino de pesos: con pesos, las dos pantallas quedan idénticas a hoy.

---

# PARTE B — Dónde queda la diferencia entre lo cobrado y lo facturado

**Qué es, en criollo:** cobraste US$ 1.000 a $1.500 (te entraron $1.500.000) pero la factura se emitió
al dólar del techo, $1.234,50 ($1.234.500). Esos **$265.500 de diferencia son reales y normales**: el
contador tiene que verlos ordenados, mes por mes, para cerrar los libros. El vendedor **no**.

**Recomendación (P4): una solapa nueva en Reportes, llamada "Facturas en dólares".**
Reportes ya está detrás de su propio permiso: el vendedor común **no la ve**. No se toca el extracto de
la reserva ni el del cliente (P-16: un dato no se dice dos veces, y esos extractos son la superficie
del vendedor).

```
Reportes
┌──────────────┬───────────────────┬─────────────────────┬────────────────────────┐
│ Ventas y     │ Finanzas y Deudas │ Facturas en dólares │ Inteligencia Analítica │
│ Margen       │                   │   ▲ NUEVA           │                        │
└──────────────┴───────────────────┴─────────────────────┴────────────────────────┘

Período: [ Este Mes ▾ ]                                        [ Exportar Excel ]

┌──────────┬────────────────────────┬──────────────┬──────────┬────────────┬──────────────┬──────────────┬─────────────┐
│ FECHA    │ COMPROBANTE            │ CLIENTE      │    US$   │ TC FACTURA │ PESOS DE LA  │    PESOS     │ DIFERENCIA  │
│          │                        │              │          │            │   FACTURA    │   COBRADOS   │             │
├──────────┼────────────────────────┼──────────────┼──────────┼────────────┼──────────────┼──────────────┼─────────────┤
│ 06/08/26 │ Factura B 0001-00012   │ Pérez, Juan  │  1.000   │  1.234,50  │  1.234.500   │  1.500.000   │  + 265.500  │
│          │ R-1042 Cancún          │              │          │            │              │              │             │
│ 04/08/26 │ Factura A 0001-00011   │ Gómez SRL    │    450   │  1.230,00  │    553.500   │    553.500   │      —      │
│ 02/08/26 │ Factura B 0001-00009   │ Díaz, Ana    │  2.100   │  1.228,00  │  2.578.800   │      —       │      —      │
│          │ R-1038 Madrid          │              │          │            │              │ (sin cobros) │             │
├──────────┴────────────────────────┴──────────────┴──────────┴────────────┴──────────────┴──────────────┴─────────────┤
│                                                            TOTAL DEL PERÍODO   4.366.800    2.053.500     + 265.500  │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Reglas de esta solapa:**

- Una fila **por factura emitida en dólares** del período elegido (mismos períodos que ya usa Reportes:
  Este Mes / Mes Anterior / Trimestre / Año / Todo).
- **Pesos cobrados** = la plata que efectivamente entró imputada a esa factura, al dólar de cada cobro.
  Si todavía no cobró nada, `—`.
- **Diferencia** = pesos cobrados − pesos de la factura. Si da cero o todavía no hay cobros, `—`.
- **Todo gris, sin semáforos.** Esta pantalla solo informa (P11=A). Nada de rojo por una diferencia:
  la diferencia no es un error.
- **Exportar Excel** reusa el botón que la pantalla ya tiene arriba; el archivo lleva las mismas
  columnas, que es lo que el contador se lleva.
- El número de comprobante y el de reserva son **links** a donde ya viven (misma costumbre del extracto
  del cliente, 2026-07-16 P4).
- **Solo lectura.** Cero botones de acción por fila.
- **Sin cobros todavía = no es un pendiente**: no se pinta, no genera aviso, no va a la campanita.

**Lo que además queda guardado y no se muestra en ninguna pantalla:** el número que el vendedor quiso
poner cuando el sistema lo acomodó al techo (A4), y de dónde salió cada tipo de cambio. Vive en el
rastro interno/auditoría, para que si alguna vez hay que explicar una factura, la explicación exista.

---

# PARTE C — Palabras que desaparecen de la vista del que opera

Estas palabras **no pueden aparecer** en ninguna pantalla, renglón, cartel, campanita, PDF ni mensaje
al cliente que vea el que opera el sistema (T-5, P-17):

`homologación` · `práctica` · `prueba` (referida al modo) · `validación` · `rechazo de validación` ·
`10240` (ni ningún número de error) · `MonCotiz` · `cotización oficial ARCA` · `WSFEv1` · `entorno`

**Alcance concreto de este barrido (lo que hay que revisar):**

1. La leyenda de práctica que hoy manda el motor a la pantalla de facturar → **muere** (en ese modo ya
   no hay casillero que leyendear, A3).
2. El "dólar para facturar" de la pantalla de inicio, cuando el número es de práctica → ver **P6**.
3. Cualquier mensaje de rechazo del comprobante que hoy repita el texto crudo del organismo → se
   muestra el motivo en criollo, sin número de error. *(No cambia P-13: el texto del motor se muestra
   tal cual; lo que cambia es que el motor no le pase jerga.)*

⚠️ **Ojo con una palabra que SÍ está firmada:** "AFIP" aparece hoy en la pantalla de facturación por
decisión tuya de 2026-06-24 (*"La factura quedó en proceso en AFIP"*), y la regla P-17 dice que el
término fiscal **vive solo ahí**. Antes de barrer "ARCA/AFIP" de todos lados necesito tu palabra:
ver **P5**.

---

## Estados de las pantallas tocadas (lo que el que implemente tiene que cubrir)

| Estado | Qué se ve |
|---|---|
| Cargando la sugerencia | Casillero vacío escribible + `Buscando el tipo de cambio…` |
| Con sugerencia | A1 |
| Sin sugerencia | A2 (+ explicación obligatoria) |
| Modo de práctica | A3 (sin casillero) |
| Por encima del techo | A4 (acomodado + una línea gris) |
| Sin permiso para consultar | Igual que A2. Nunca un cartel de permiso denegado |
| Reportes → Facturas en dólares, vacío | `No hay facturas en dólares en este período.` en gris, sin dibujo ni botón |
| Reportes → Facturas en dólares, cargando | El mismo esqueleto gris que ya usan las otras solapas |
| Reportes → Facturas en dólares, error | El mismo aviso que ya usan las otras solapas de Reportes |

---

## PREGUNTAS PARA GASTON

### Tema: el tipo de cambio cuando facturás en dólares
Contexto: hoy, al facturar en dólares, te aparece el número puesto y un renglón que te explica cosas.
Queremos dejarlo en lo mínimo y que el sistema resuelva solo los casos raros, sin molestarte.

---

**P1. Debajo del casillero, ¿qué querés que diga?**

  A) **Solo qué dólar es y de qué día** (recomendada)
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial del 6 de agosto.
```
  B) **Nada**, el número solo
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
```
  C) **Como está hoy** (con la aclaración de qué pasa si lo cambiás)
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.234,50   │
  └──────────────┘
  Dólar oficial de hoy (6 de agosto). Si ponés otro número, lo tomamos a mano.
```
  *Por qué recomiendo A:* de qué día es el dólar **sí** te importa (un lunes es el del viernes, y eso
  cambia la plata). Que "si lo pisás lo tomamos a mano" es una explicación de cómo funciona el sistema:
  eso ya se ve solo, porque apenas lo pisás te aparece el renglón que te pregunta de dónde lo sacaste.

---

**P2. Mientras el sistema está en modo de práctica, el número que hay que usar es uno de juguete. Dijiste
que preferís que ni se muestre. ¿Cómo lo hacemos?**

  A) **El casillero no aparece** (recomendada) — elegís dólares y emitís; el número lo pone el sistema solo
```
  ┌─ MONEDA DE LA FACTURA ──────────────────────┐
  │  ( ) Pesos (ARS)      (•) Dólares (USD)     │
  └─────────────────────────────────────────────┘

                              [ Emitir factura ]
```
  B) **El casillero aparece apagado**, con una palabra
```
  TIPO DE CAMBIO ($/USD)
  ┌──────────────┐
  │  Automático  │   ← gris, no se puede tocar
  └──────────────┘
```
  C) **Aparece con el número puesto**, sin ninguna leyenda
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │     980,00   │   ← el número de juguete, sin explicación
  └──────────────┘
```
  *Por qué recomiendo A:* es la única que cumple "ni se entere". La B deja una pregunta muda ("¿por qué
  no puedo tocarlo?") y la C te muestra un número que **no es plata de verdad** al lado de un total que
  sí lo es. **Ojo, con la A también se esconde el renglón "≈ equivalente en pesos" del pie** mientras
  dure ese modo, por lo mismo. Si querés que ese renglón se quede igual, decímelo.

---

**P3. Si escribís un dólar más alto del que la factura admite (ponele 1.500 cuando el máximo del día es
1.235,50), hoy el comprobante te REBOTA después de darle Emitir. ¿Qué preferís que pase?**

  A) **El sistema lo acomoda solo al máximo, con un renglón gris, y seguís** (recomendada)
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.235,50   │   ← lo acomodó solo al salir del casillero
  └──────────────┘
  En la factura entra hasta $ 1.235,50.
                              [ Emitir factura ]   ← no se frena nada
```
  B) **Te ofrece emitir en pesos**, con un clic
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.500,00   │
  └──────────────┘
  Con ese dólar la factura tiene que ir en pesos.   [ Pasar a pesos ]
```
  C) **Te frena** hasta que lo bajes vos
```
  TIPO DE CAMBIO ($/USD) *
  ┌──────────────┐
  │   1.500,00   │
  └──────────────┘
  ⚠ El máximo de hoy es $ 1.235,50. Corregilo para poder emitir.
                              [ Emitir factura ]   ← apagado
```
  *Por qué recomiendo A:* es la única que no te interrumpe. Y **la diferencia no se pierde**: el sistema
  guarda que vos cobraste a 1.500 y el contador la ve ordenada (pregunta P4).
  **Te aviso algo importante, porque va contra una regla tuya:** vos firmaste que *"el sistema sugiere,
  no decide, y nunca pisa lo que el usuario ya cargó"*. Con la A, **el sistema te pisa el número**. Yo
  creo que acá corresponde la excepción (es un techo, no una opinión: ese número no entra), pero la
  excepción **la firmás vos**.

---

### Tema: dónde ve el contador la diferencia entre lo que cobraste y lo que facturaste
Contexto: cobrás a un dólar y facturás a otro (el del techo). Esa diferencia es normal y el contador la
necesita ordenada; el vendedor no la tiene que ver nunca.

**P4. ¿Dónde la ponemos?**

  A) **Una solapa nueva en Reportes: "Facturas en dólares"** (recomendada) — el vendedor no entra ahí
```
  Reportes  [Ventas y Margen] [Finanzas y Deudas] [Facturas en dólares] [Inteligencia]
  Período: [Este Mes ▾]                                   [ Exportar Excel ]
  FECHA     COMPROBANTE           CLIENTE     US$    TC      PESOS FACT.  PESOS COBR.  DIFERENCIA
  06/08/26  Factura B 0001-00012  Pérez Juan  1.000  1.234,50  1.234.500   1.500.000    + 265.500
                                                       TOTAL   4.366.800   2.053.500    + 265.500
```
  B) **Un renglón más en el estado de cuenta de cada reserva**
```
  Factura B 0001-00012 · US$ 1.000 · TC 1.234,50 ............... $ 1.234.500
  Diferencia por el dólar cobrado ..............................  + $ 265.500
```
  C) **En ningún lado a la vista**: queda solo guardado y se saca un archivo cuando el contador lo pide
```
  (nada en pantalla)     Configuración → "Bajar planilla para el contador"
```
  *Por qué recomiendo A:* el contador entra una vez por mes, elige el período y se lleva la planilla; no
  tiene que abrir reserva por reserva. La B le mete un renglón raro al vendedor en la pantalla que más
  mira (y vos ya firmaste que el estado de cuenta se lee como un extracto limpio). La C funciona, pero
  nadie encuentra lo que no se ve.

---

### Tema: palabras que no queremos más en pantalla

**P5. "AFIP/ARCA" aparece hoy en la pantalla de facturar ("La factura quedó en proceso en AFIP"), y eso
lo firmaste vos. ¿Lo dejamos?**

  A) **Se queda solo ahí**, donde nombra el trámite real; se barren "práctica", "homologación",
     "validación" y los números de error de todas las pantallas (recomendada)
```
  ✓ La factura quedó en proceso en AFIP. Te avisamos apenas salga.
```
  B) **Se saca también "AFIP"** de todos lados
```
  ✓ La factura quedó en proceso. Te avisamos apenas salga.
```
  *Por qué recomiendo A:* "AFIP" es una palabra que el vendedor de una agencia entiende y usa todos los
  días; "homologación" o "validación 10240", no. Sacar AFIP de la pantalla de facturar deja al usuario
  sin saber a quién le está pidiendo la factura.

---

**P6. En la pantalla de inicio hay (o iba a haber) un "dólar para facturar". Cuando el sistema está en
modo de práctica, ese número también es de juguete. ¿Qué hace?**

  A) **No se muestra**: queda solo el dólar del banco (recomendada)
```
  Dólar Banco Nación  $ 1.245,00   (6 de agosto)
```
  B) **Se muestra con una palabrita gris al lado**
```
  Dólar Banco Nación  $ 1.245,00  │  Para facturar  $ 980,00  (de práctica)
```
  C) **Se muestra igual**, sin aclarar nada
```
  Dólar Banco Nación  $ 1.245,00  │  Para facturar  $ 980,00
```
  *Por qué recomiendo A:* es coherente con lo que decidas en la P2 (si el número de juguete no se ve al
  facturar, menos todavía en la portada). La B usa justo la palabra que no querés y la C te muestra un
  número falso al lado de uno real, que es peor.
  *(Nota: esto **corrige** la recomendación que te había hecho ayer para la pantalla de inicio, donde te
  proponía la B. Con tu mandato de hoy, la A es la que corresponde.)*

---

**Recordá que siempre podés responder "otra cosa" y contarme cómo lo querés.**
