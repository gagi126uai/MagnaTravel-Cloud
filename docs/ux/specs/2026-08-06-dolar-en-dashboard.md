# Cómo se ve el dólar en el dashboard — spec de diseño

> **✅ FIRMADA 2026-08-05.**
> Gastón contestó las 6 preguntas eligiendo sobre maquetas HTML (P2 la delegó en la
> investigación del rubro ERP contra SAP/NetSuite/Odoo/Business Central/Tango/Contabilium —
> patrón universal: el tipo de cambio fiscal es una regla del documento, jamás compite con
> el dato operativo en el inicio). Registro de las respuestas:
>
> | Pregunta | Respuesta | Nota |
> |---|---|---|
> | P1 | **A** | tira fina de una línea, molde exacto de `ReservaKPIs.jsx` |
> | P2 | **B** (por investigación ERP, delegada por el dueño) | un solo dólar (Banco Nación); "para facturar" no se pinta en el inicio |
> | P3 | **C** | cubierta por P2=B: el número de facturar no está en esta tira, no hace falta avisar que es de práctica acá |
> | P4 | **C** | euro/real detrás de "otras monedas ▾", solo si el lector los trajo con dato |
> | P5 | **B** | la misma tira para Admin y Vendedor (sin número fiscal, no hay nada que ocultar) |
> | P6 | **A** | fecha en gris al final ("al DD/MM" / "al DD/MM (sin actualizar)"); muere el badge verde/ámbar |
>
> Implementado por `frontend-senior`: componente `DolarBnaTira.jsx`, montado en
> `AdminDashboard.jsx` y `AgentDashboard.jsx` en lugar de `BnaUsdSellerRateCard.jsx`.
> `DolarParaFacturarCard.jsx` sigue desmontado (no se toca; el dato `dolarParaFacturar`
> sigue viajando en el DTO del dashboard sin pintarse).
>
> Fecha: 2026-08-06 · Autor: gate UX (`ux-ui-disenador`) · Pantallas: `AdminDashboard.jsx`,
> `AgentDashboard.jsx` · Componentes de hoy: `BnaUsdSellerRateCard.jsx` (montado),
> `DolarParaFacturarCard.jsx` (desmontado el 2026-08-05 por decisión del dueño).

---

## 1. Por qué existe esta spec

La noche del **2026-08-05** salieron a producción **dos tarjetas grandes** en el dashboard
("Dólar Banco Nación (venta)" + "Dólar para facturar (ARCA)" con un badge ámbar de aviso de
prueba) **sin maqueta firmada**. Gastón las desaprobó completamente: **"es feo"**. Se desmontó la
de ARCA; quedó la del Banco Nación sola.

**Diagnóstico (hipótesis a validar con él, es la base de la recomendación P1):** el problema no
fue el dato, fue el **tamaño y el protagonismo**. El dólar es un dato de **referencia** (mirás el
número y seguís trabajando), y hoy ocupa una tarjeta grande con **6 recuadros adentro**
(3 de moneda + 3 de información) + un badge de estado, arriba de todo, antes que los números del
negocio. Dos de esas tarjetas, una al lado de la otra, empujaban las ventas del mes hacia abajo.

Además, tal como está hoy **choca con reglas ya firmadas**:

| Lo que hay hoy | Regla firmada con la que choca |
|---|---|
| Badge de color ("Actualizado" verde / "Dato desactualizado" ámbar / "Dólar de prueba" ámbar) para un dato que no pide hacer nada | **Guía 2026-08-03, P11=A** (primera regla de "Colores y estilo"): *el aviso que PIDE HACER ALGO va con color; el que SOLO INFORMA va gris y en una sola línea.* |
| Línea de ayuda debajo del título ("Para cotizarle al cliente. Es el del mostrador del banco.") | **P-15** (nada de cartelitos aclarativos) y la regla general 2026-06-05 (*"si un campo necesita explicación, el diseño está mal"*) |
| Recuadros "Fecha publicada / Hora publicada / Fuente: Banco Nacion" | **P-15** otra vez: tres recuadros para decir de dónde salió un número que no se discute |
| "Dolar vendedor" / "Euro vendedor" / "Real vendedor" | **T-5 / P-17** (nada de palabras internas): "vendedor" acá es la jerga bancaria de *tipo vendedor*, no el vendedor de la agencia. Un usuario lee "vendedor" y piensa en su empleado. |

**Lo que SÍ está firmado y sirve de molde:** la **tira fina de una línea** del listado de Reservas
(**guía 2026-08-03, P2=A**, ya construida en `ReservaKPIs.jsx`): un renglón con rótulo chiquito en
mayúsculas + número grande, separado por `│`, importes con las monedas separadas por `·`.

---

## 2. Diseño propuesto (recomendación — sujeto a P1..P6)

**Una tira fina, gris, de una sola línea, debajo del encabezado del dashboard y ARRIBA de las
tarjetas de KPI.** Mismo molde visual que la tira firmada del listado de Reservas.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Dashboard                                     [Nuevo presupuesto]  [CRM]    │
│  Cómo viene tu agencia de un vistazo.                                        │
└──────────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────────┐
│  DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 05/08   │
└──────────────────────────────────────────────────────────────────────────────┘

┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ Ventas del  │ │ Margen      │ │ Cobros      │ │ Saldo       │   ← sin cambios
│ Mes         │ │ Bruto       │ │ Clientes    │ │ Pendiente   │
└─────────────┘ └─────────────┘ └─────────────┘ └─────────────┘
```

Detalle fino de la tira propuesta:

- Fondo gris muy claro, borde suave, **una sola línea** (en celular puede partirse en dos).
- Rótulo chiquito en mayúsculas (gris) + número grande en negrita (color de texto normal,
  **sin verde ni ámbar**: no pide hacer nada — P11=A).
- Separador `│` entre los dos números, igual que la tira de Reservas.
- Al final, en gris chiquito, **la fecha del dato**: `al 05/08`.
- **No es clickeable, no lleva botón, no se puede refrescar a mano.** Se actualiza solo.

### Los cuatro estados de la tira

**1) Todo normal (dato de hoy):**
```
  DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 05/08
```

**2) El dato del banco quedó viejo (`isStale`):**
```
  DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 02/08 (sin actualizar)
```
Todo en gris, **sin ámbar**: no hay nada que hacer, solo hay que saberlo (P11=A).

**3) El número de facturar es el de práctica (`esDePrueba = true`):**
```
  DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50 (de práctica)   al 05/08
```
Una palabra en gris pegada al número, no un badge gritón. La palabra **"homologación" no se
escribe nunca** (T-5, P-17): en criollo es "de práctica".

**4) No hay dato (el lector no trajo nada):**
```
  DÓLAR BANCO NACIÓN  sin dato hoy   │   PARA FACTURAR  $ 1.496,50    al 05/08
```
La tira **no desaparece** (que se vaya un renglón entero desordena la pantalla y hace dudar al
usuario); el número que falta dice "sin dato hoy" en gris. Si **ninguno** de los dos tiene dato,
ahí sí la tira entera no se dibuja (un renglón que solo dice "no sé nada" es ruido).

### Qué NO hay que hacer (explícito, para el frontend)

- **NO** tarjeta grande, **NO** recuadros adentro, **NO** ícono de banco, **NO** badge de color.
- **NO** línea de ayuda ("Para cotizarle al cliente…", "El que ARCA acepta…") — P-15.
- **NO** las palabras "vendedor", "divisa", "homologación", "BNA" sueltas, "ARCA" como badge,
  "scraper", "snapshot", "fuente" (T-5, P-17).
- **NO** hora ni "Última consulta: 05/08/2026 14:22" (era información de mantenimiento del
  sistema, no del negocio). Alcanza con la fecha del dato.
- ~~**NO** botón de refrescar ni link.~~ **ADENDA (2026-08-05 tarde, orden verbal de Gaston
  mirando el dashboard EN VIVO — pisa esta línea)**: SÍ hay un botón "actualizar" al final del
  renglón, fantasma gris con ícono chico y la palabra al lado (P-10), sin color (P11=A: no pide
  decisión). Motivo: el lector del BNA estuvo roto un mes en silencio y el dueño quiere poder
  pedir el dato en el momento. Al tocarlo dice "buscando…" y la tira se refresca sola; si la
  búsqueda falla, la tira queda como estaba (sin cartel rojo).
- **NO** verde para "actualizado": no hay nada que festejar; el color se reserva para lo que pide
  acción (P11=A).
- Formato argentino de plata y fecha (**P-2**); si alguna hora se mostrara, hora argentina (**T-14**).

### Datos del motor que usa (verificados en los componentes de hoy)

| Dato | De dónde sale | Uso en la tira |
|---|---|---|
| Dólar Banco Nación venta | `dashboard.bnaUsdSellerRate.value` | número 1 |
| ¿Quedó viejo? | `bnaUsdSellerRate.isStale` | agrega "(sin actualizar)" |
| Fecha publicada | `bnaUsdSellerRate.publishedDate` | "al 05/08" |
| Euro / Real | `bnaUsdSellerRate.euroValue` / `realValue` (pueden venir vacíos) | **según P4** |
| Dólar para facturar | `dolarParaFacturar.value` | número 2 (**según P2**) |
| Fecha de ese dólar | `dolarParaFacturar.rateDate` | "al 05/08" |
| ¿Es el de práctica? | `dolarParaFacturar.esDePrueba` | agrega "(de práctica)" |

**Nada de esto pide trabajo del motor:** los dos datos ya viajan en la respuesta del dashboard.
Es solo cómo se dibujan.

---

## 3. Preguntas abiertas — sin estas respuestas NO se construye

Van en el bloque de abajo, tal cual se le mandan a Gastón. Cada una tiene una recomendación única
y el porqué. Mientras no estén contestadas, **el dashboard queda como está hoy** (la tarjeta del
Banco Nación sola, la de ARCA desmontada).

Qué cambia con cada respuesta:

- **P1** define el formato (tira / tarjeta / barra de arriba). Es la decisión madre: si contesta
  B o C, esta spec se rehace entera sobre ese formato.
- **P2** define si en la tira van uno o dos números.
- **P3** define cómo se avisa que el de facturar es el de práctica.
- **P4** define si aparecen euro y real.
- **P5** define qué ve el vendedor (hoy los dos dashboards muestran exactamente lo mismo).
- **P6** define cómo se muestra la actualidad del dato (fecha en gris / badge / nada).

---

## 4. Bloque de preguntas (listo para reenviar)

## PREGUNTAS PARA GASTON

### Tema: el dólar en la pantalla de inicio (dashboard)
Contexto: anoche salieron dos tarjetas grandes con el dólar y no te gustaron. La sospecha es que
el problema fue el **tamaño**: es un dato para mirar de reojo y ocupaba más lugar que las ventas
del mes. Antes de tocar nada, queremos que elijas mirando.

---

**P1. ¿Cómo querés ver el dólar en la pantalla de inicio?**

  **A) Un renglón fino y gris arriba de los números del mes** ← *recomendada*
  Ocupa una línea, no compite con nada, lo mirás de reojo y seguís.
```
  ┌──────────────────────────────────────────────────────────────────────┐
  │  Dashboard                              [Nuevo presupuesto]  [CRM]   │
  └──────────────────────────────────────────────────────────────────────┘
    DÓLAR BANCO NACIÓN  $ 1.515,00  │  PARA FACTURAR  $ 1.496,50   al 05/08
  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
  │ Ventas   │ │ Margen   │ │ Cobros   │ │ Saldo    │
  │ del Mes  │ │ Bruto    │ │ Clientes │ │ Pendiente│
  └──────────┘ └──────────┘ └──────────┘ └──────────┘
```
  *Por qué la recomendamos: es el mismo renglón fino que ya firmaste arriba del listado de
  Reservas ("Reservas activas │ Por cobrar │ Vendido"), así que la pantalla habla un solo idioma.*

  **B) Una tarjeta chica más, del mismo tamaño que las otras, en la fila de siempre**
  El dólar pasa a ser una tarjeta igual a las demás, ni más grande ni más chica.
```
  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
  │ Ventas   │ │ Margen   │ │ Cobros   │ │ Saldo    │ │ Dólar    │
  │ del Mes  │ │ Bruto    │ │ Clientes │ │ Pendiente│ │ $1.515,00│
  └──────────┘ └──────────┘ └──────────┘ └──────────┘ └──────────┘
```

  **C) Arriba de todo, en la barra del sistema — se ve desde CUALQUIER pantalla**
  No vive en el inicio: vive en la barra de arriba, al lado de la campanita, siempre a mano.
```
  ┌──────────────────────────────────────────────────────────────────────┐
  │ MagnaTravel      Dólar $ 1.515,00        🔔     Gastón ▾             │
  ├──────────────────────────────────────────────────────────────────────┤
  │  Dashboard                                                           │
  │  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐                │
```

  **D) Que no esté en el inicio.** El que factura ya ve el tipo de cambio en la propia pantalla
  de facturar; el que cotiza lo busca aparte.

---

**P2. En ese renglón, ¿qué números querés ver?**
Son dos cosas distintas: el del **Banco Nación** (el del mostrador, el que usás para cotizarle al
cliente) y el **de facturar** (el único que la AFIP acepta en una factura en dólares).

  **A) Los dos, uno al lado del otro** ← *recomendada*
```
    DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 05/08
```
  *Por qué: son distintos y esa diferencia se te va a la ganancia. Verlos juntos evita la sorpresa
  de facturar a un número que no era el que cotizaste.*

  **B) Solo el del Banco Nación**
```
    DÓLAR BANCO NACIÓN  $ 1.515,00                                     al 05/08
```

  **C) El del Banco Nación siempre; el de facturar solo aparece cuando es distinto**
```
    (día normal)   DÓLAR BANCO NACIÓN  $ 1.515,00                      al 05/08
    (día distinto) DÓLAR BANCO NACIÓN  $ 1.515,00  │  PARA FACTURAR  $ 1.496,50
```

---

**P3. El "dólar para facturar" a veces es el de PRÁCTICA (mientras las facturas están en modo
prueba, no es el número real). ¿Cómo querés que se avise?**

  **A) Una palabrita en gris pegada al número** ← *recomendada*
```
    DÓLAR BANCO NACIÓN  $ 1.515,00  │  PARA FACTURAR  $ 1.496,50 (de práctica)   al 05/08
```
  *Por qué: ya firmaste que lo que solo informa va en gris y en una línea, y el color se guarda
  para lo que te pide hacer algo. Un cartel amarillo grande para algo que no tenés que resolver
  es justo lo que ensucia la pantalla.*

  **B) El cartelito amarillo de siempre, pero chiquito, al lado**
```
    DÓLAR BANCO NACIÓN  $ 1.515,00  │  PARA FACTURAR  $ 1.496,50  [⚠ de práctica]  al 05/08
```

  **C) Cuando es de práctica, ese número no se muestra**
```
    DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  —          al 05/08
```

---

**P4. El lector del Banco Nación a veces trae también el EURO y el REAL. ¿Los querés ver?**

  **A) No, nunca en el inicio** ← *recomendada*
```
    DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 05/08
```
  *Por qué: hoy el sistema vende, cobra y factura en pesos y dólares nada más. Un euro que no usás
  para nada alarga el renglón y te hace leer de más todos los días. El día que vendas en euros,
  se agrega.*

  **B) Sí, siempre los tres**
```
    DÓLAR $ 1.515,00  ·  EURO $ 1.660,00  ·  REAL $ 268,00   │  PARA FACTURAR $ 1.496,50
```

  **C) El dólar siempre; euro y real escondidos detrás de un "ver más"**
```
    DÓLAR BANCO NACIÓN  $ 1.515,00  │  PARA FACTURAR  $ 1.496,50   al 05/08   [otras monedas ▾]
                                                                    ┌────────────────────────┐
                                                                    │ EURO  $ 1.660,00       │
                                                                    │ REAL  $   268,00       │
                                                                    └────────────────────────┘
```

---

**P5. El vendedor (que no factura) ¿ve lo mismo que vos?**
Hoy las dos pantallas de inicio muestran exactamente lo mismo.

  **A) El vendedor ve SOLO el del Banco Nación; vos ves los dos** ← *recomendada*
```
  VOS (administración)
    DÓLAR BANCO NACIÓN  $ 1.515,00   │   PARA FACTURAR  $ 1.496,50    al 05/08

  EL VENDEDOR
    DÓLAR BANCO NACIÓN  $ 1.515,00                                    al 05/08
```
  *Por qué: el vendedor cotiza, no factura. El número de facturar no le cambia nada y le mete una
  duda ("¿con cuál cotizo?") que no tiene por qué tener.*

  **B) Los dos ven exactamente lo mismo**

  **C) El vendedor no ve ningún dólar en su inicio**

---

**P6. Cuando el dato del banco quedó viejo (el banco no publicó, o el sistema no pudo leer),
¿cómo querés que se note?**

  **A) La fecha del dato, en gris, al final del renglón** ← *recomendada*
```
    (al día)   DÓLAR BANCO NACIÓN  $ 1.515,00                        al 05/08
    (viejo)    DÓLAR BANCO NACIÓN  $ 1.515,00                        al 02/08 (sin actualizar)
```
  *Por qué: con la fecha ya sabés si sirve o no, sin que la pantalla te grite. Y sigue el mismo
  criterio: gris para lo que solo informa.*

  **B) Un cartelito de color, como está hoy** ("Actualizado" verde / "Dato desactualizado" ámbar)

  **C) Que no diga nada: el número y listo**

---

## 5. Después de las respuestas

1. Las 6 respuestas se escriben como reglas en `docs/ux/guia-ux-gaston.md`
   (secciones "Colores y estilo" y una sección nueva "Pantalla de inicio (dashboard)").
2. Esta spec se actualiza con la variante elegida y se marca **FIRMADA**.
3. Recién ahí `frontend-senior` construye: baja `BnaUsdSellerRateCard` y `DolarParaFacturarCard`
   del dashboard y monta lo firmado. Los componentes viejos quedan en el repo hasta el deploy y
   después se borran (son código, no datos de negocio: la regla "nada se borra" es de documentos
   del negocio, no de archivos de código).
4. El OK final es de Gastón mirando la pantalla real en producción, como siempre.
