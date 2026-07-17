# T5 — Resolver devoluciones VIEJAS de servicios cancelados (sub-estado "sin factura guardada")

Fecha: 2026-07-17. Autor: `ux-ui-disenador`. Estado: **APROBADA POR GASTÓN (2026-07-17).** Las 5 preguntas
fueron respondidas con la opción recomendada en TODAS (P1=A, P2=A, P3=A, P4=A, P5=A). La sección final
**"SPEC APROBADA PARA IMPLEMENTAR"** es la que sigue `frontend-senior` al pie de la letra.

> **Qué es esto, en criollo.** Es el rediseño del sub-estado "trabado" del panel `DEVOLUCIÓN · SERVICIO
> CANCELADO`. Aparece SOLO en casos VIEJOS: servicios que se cancelaron ANTES de que el sistema empezara a
> guardar (2026-07-16) a qué factura correspondía cada renglón. En esos casos el sistema no sabe contra qué
> factura ni por cuánto emitir la devolución, así que se lo tiene que decir el back-office: de qué factura
> sale cada devolución, por cuánto, y por qué. Con eso el sistema puede emitir la nota de crédito.
>
> **Esta spec REEMPLAZA** el formulario pelado que se agregó el 2026-07-16 (`t5-resolver-legacy` en
> `PartialCreditNoteEmissionPanel.jsx`, líneas 185-207: desplegable "Factura destino" + casillero "Monto
> bruto" + "Motivo"). Ese formulario resultó confuso al probarlo (Gastón: "no te indica qué hay que poner,
> no se entiende nada") y solo contemplaba UN servicio pendiente.
>
> **NO reabre** nada de la spec del caso normal (`docs/ux/2026-07-15-t5-pantalla-confirmar-nc-parcial.md`):
> una vez que una devolución vieja queda resuelta, se emite con EXACTAMENTE ese flujo (¿Seguro? + estados
> emitiendo/emitida/rechazada). Sigue valiendo todo lo del paso de multa (2026-07-08), la bandeja pasiva
> (2026-07-10 T4) y las tres reglas duras de multimoneda (2026-06-09).

---

## El problema que destapó Gastón (2026-07-16, en vivo)

Su reserva tenía DOS servicios cancelados esperando devolución, del mismo operador y en monedas distintas:
un hotel de **US$ 700** y una excursión de **$ 720.000**. La pantalla:

1. **No decía cuál servicio estaba resolviendo** → mezcló los dos y tecleó `700000` (juntó los US$ 700 con
   los pesos).
2. **Solo soportaba UN servicio pendiente** (coincide con el guard de backend `INV-T5-RESOLVE-STATE`: "Solo
   se puede resolver una devolución parcial pendiente de un único servicio").
3. **No explicaba qué es "monto bruto" ni para qué sirve el motivo.**

---

## Lo que YA está decidido por la guía (no se pregunta, se aplica)

Cada punto cita la regla que lo respalda en `docs/ux/guia-ux-gaston.md`.

1. **Vive ARRIBA de la ficha, como aviso accionable, siempre visible, todo EN LÍNEA** (nunca ventana
   flotante, salvo el "¿Seguro?" de emitir). — 2026-07-15 P1=A + regla dura del modal (2026-06-09 P3).
2. **La moneda la manda LA FACTURA.** Un servicio en dólares SOLO se resuelve contra una factura en
   dólares; uno en pesos, contra una en pesos. Nunca se suma, nunca se convierte, nunca aparece la frase
   "diferencia de cambio". — 2026-06-09 (tres reglas duras). De acá sale el **filtro del desplegable de
   facturas por la moneda del servicio**.
3. **Formato de la opción de factura:** `Factura B 0001-00012345 — US$ 900` (número + moneda + monto). —
   2026-07-10 P5 + obra 2026-07-16 (`servicePublicIds`/`currency` en `reserva.invoices[]`).
4. **Sugerencias precargadas en amarillo, editables** (patrón del tarifario). — 2026-06-05. Se usa para el
   monto sugerido (`LineSaleAmount`).
5. **Vocabulario:** "servicio cancelado" (nunca "anulado"); en pantalla de facturación se pueden usar
   "devolución"/"nota de crédito"/"factura". — 2026-07-08 glosario + 2026-07-15.
6. **Si falla guardar:** el formulario queda con TODO lo cargado intacto + cartel rojo arriba de los
   botones; reintenta en el mismo botón. Nunca se pierde lo cargado. — Ronda 2 (2026-06-06).
7. **Ningún mensaje deriva a un rol que el usuario ya es** ("hablá con administración"). — 2026-07-08.
8. **Permiso `cobranzas.invoice_annul`** para ver y usar este paso (el mismo de la anulación total y de la
   bandeja). Sin permiso, no aparece el formulario de resolver. — 2026-07-15 (contrato §11).
9. **Una vez resuelta, cada devolución se emite con el flujo del caso normal** (¿Seguro? + emitiendo /
   emitida / rechazada, auto-refresco que no atrapa). — 2026-07-15 §4.
10. **Nada de jerga, IDs internos, enums ni texto crudo de error de AFIP** fuera del renglón del rechazo. —
    2026-07-08 (8 reglas de voz) + gate data-exposure.

---

## Decisiones nuevas de Gastón (2026-07-17)

- **P1=A** — Lista completa de servicios pendientes a la vista, cada uno con su botón **Resolver** por fila.
- **P2=A** — SÍ va la línea explicativa arriba (excepción puntual a "nada de aclaraciones", justificada por
  ser un caso raro de datos viejos y por el pedido explícito de Gastón).
- **P3=A** — Casillero **"¿Cuánto se le devuelve al cliente?"**, precargado con el precio de venta
  (`LineSaleAmount`), editable, con línea de ayuda.
- **P4=A** — Casillero **"¿Por qué corresponde esta factura y este monto?"**, obligatorio, con línea de ayuda.
- **P5=A** — Cartel neutro cuando no hay factura de la moneda del servicio.

---

## 1. La lista de devoluciones pendientes (estado "hay servicios viejos por resolver")

Vive en el mismo lugar que el panel del caso normal: arriba de la ficha, en la tira de avisos accionables
(2026-07-15 P1=A). En vez del formulario pelado, muestra la lista completa con progreso.

```
┌────────────────────────────────────────────────────────────────────────────┐
│ 💳  Faltan resolver 2 devoluciones de servicios cancelados      0 de 2 listas│
│                                                                              │
│ Estos servicios se cancelaron cuando el sistema todavía no guardaba a qué    │
│ factura correspondía cada uno. Decinos de qué factura sale cada devolución   │
│ y por cuánto, y el sistema la emite.                                         │
│                                                                              │
│ ┌──────────────────────────────────────────────────────────────────────┐   │
│ │ Hotel Maitei (Posadas) · Turismo Cardozo            US$ 700  [Resolver]│  │
│ ├──────────────────────────────────────────────────────────────────────┤   │
│ │ Excursión Cataratas · Turismo Cardozo             $ 720.000  [Resolver]│  │
│ └──────────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────┘
```

**Contenido, de arriba a abajo (todo lo da el backend, el front no deduce):**

1. **Título con contador:** `Faltan resolver {N} devoluciones de servicios cancelados` a la izquierda, y a
   la derecha el progreso `{resueltas} de {N} listas`. Con una sola devolución pendiente, singular:
   `Falta resolver 1 devolución de un servicio cancelado`.
2. **Línea explicativa (P2=A), texto EXACTO:**
   *"Estos servicios se cancelaron cuando el sistema todavía no guardaba a qué factura correspondía cada
   uno. Decinos de qué factura sale cada devolución y por cuánto, y el sistema la emite."*
3. **Una fila por servicio cancelado pendiente**, cada una con:
   - **Nombre del servicio** (ej. "Hotel Maitei (Posadas)") · **operador** (ej. "Turismo Cardozo").
   - **Moneda + monto de venta** a la derecha (`US$ 700` / `$ 720.000`), con `formatCurrency` y las reglas
     de multimoneda (pesos y dólares SIEMPRE separados, jamás sumados).
   - **Botón `Resolver`** al final de la fila. Con permiso `cobranzas.invoice_annul`; sin permiso, la fila
     se muestra igual pero sin botón (solo lectura).
4. Las filas ya resueltas quedan en la lista con su marca "Resuelto ✓" (ver §3), no desaparecen: el
   back-office ve de un vistazo qué falta y qué ya está.

> **Multi-moneda en la lista:** aunque haya un servicio en pesos y otro en dólares, NUNCA se muestra un
> total mezclado ni un subtotal general. Cada fila lleva su propia moneda y punto. (2026-06-09.)

---

## 2. Resolver una devolución (formulario en línea por fila)

Al tocar **[Resolver]** de una fila, se abre EN LÍNEA justo debajo de esa fila (nunca ventana flotante). El
nombre del servicio y su moneda quedan SIEMPRE a la vista arriba del formulario, para que no se mezcle con
otro servicio.

```
│ Hotel Maitei (Posadas) · Turismo Cardozo            US$ 700  [Resolver]│
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │ Resolver la devolución de:  Hotel Maitei (Posadas) — US$ 700      │ │
│  │                                                                  │ │
│  │ ¿De qué factura sale esta devolución?                            │ │
│  │   [ Elegí la factura en dólares…                            ▾ ]  │ │
│  │   Solo aparecen facturas en dólares: la moneda la manda la       │ │
│  │   factura.                                                        │ │
│  │                                                                  │ │
│  │ ¿Cuánto se le devuelve al cliente?                               │ │
│  │   [ US$ 700 ]                                                     │ │
│  │   Es el total de este servicio en la factura, con impuestos      │ │
│  │   incluidos. Te lo sugerimos por el precio de venta; corregilo   │ │
│  │   solo si en la factura fue otro número.                          │ │
│  │                                                                  │ │
│  │ ¿Por qué corresponde esta factura y este monto?                 │ │
│  │   [__________________________________________________________]  │ │
│  │   Queda registrado para explicar esta devolución.                │ │
│  │                                                                  │ │
│  │                            [ Cancelar ]  [ Guardar esta devolución ] │ │
│  └──────────────────────────────────────────────────────────────────┘ │
```

**Campos, en orden (textos EXACTOS):**

1. **Encabezado del formulario (solo lectura):** `Resolver la devolución de: {nombre del servicio} — {moneda}{monto de venta}`.
   Ej.: `Resolver la devolución de: Hotel Maitei (Posadas) — US$ 700`. Es el ancla que le dice al usuario
   cuál está resolviendo.

2. **¿De qué factura sale esta devolución?** — desplegable **filtrado por la moneda del servicio**
   (reusa `ElegirFacturaDestinoInline` + formato `Factura B 0001-00012345 — US$ 900`, 2026-07-10 P5). Solo
   lista facturas activas de esa moneda.
   - Placeholder según moneda: `Elegí la factura en dólares…` / `Elegí la factura en pesos…`.
   - Línea de ayuda debajo: *"Solo aparecen facturas en {dólares|pesos}: la moneda la manda la factura."*
   - Si la reserva tiene UNA sola factura de esa moneda, viene pre-elegida (coherente con la obra
     2026-07-16 "factura pre-elegida").

3. **¿Cuánto se le devuelve al cliente?** (P3=A) — casillero numérico **precargado con `LineSaleAmount`**
   del renglón (marcado en amarillo como sugerencia, patrón tarifario 2026-06-05), editable. Muestra la
   moneda del servicio al lado (`US$` / `$`), nunca deja elegir moneda.
   - Línea de ayuda EXACTA: *"Es el total de este servicio en la factura, con impuestos incluidos. Te lo
     sugerimos por el precio de venta; corregilo solo si en la factura fue otro número."*
   - Validación: mayor a 0. Si el backend rechaza por tope de saldo de la factura (`INV-T5-EMIT-CAP` u
     `INV-T5-RESOLVE-*`), se muestra el mensaje neutro que devuelva el backend (ver §5), sin jerga.

4. **¿Por qué corresponde esta factura y este monto?** (P4=A) — texto, **obligatorio**, con el mínimo que
   ya exige el backend (unas palabras).
   - Línea de ayuda EXACTA: *"Queda registrado para explicar esta devolución."*

5. **Botones:** `Cancelar` (cierra el formulario de esa fila, no pierde nada de las demás) · **`Guardar
   esta devolución`** (deshabilitado hasta tener factura elegida + monto > 0 + motivo cargado). Se bloquea
   mientras se procesa el guardado, para no guardar dos veces.

> **Solo se resuelve de a uno.** El formulario trabaja sobre UN servicio por vez. El backend debe permitir
> resolver uno de varios pendientes (ver "Qué necesita del backend"). El resto de las filas queda intacto.

---

## 3. Después de guardar una devolución (queda resuelta, lista para emitir)

Al guardar, esa fila pasa a **Resuelto ✓** con el detalle de contra qué factura y por cuánto, y aparece su
bloque "listo para emitir" del §2 de la spec del 2026-07-15 (con su `¿Seguro?` y sus estados
emitiendo/emitida/rechazada). El contador de arriba sube: `1 de 2 listas`.

```
┌────────────────────────────────────────────────────────────────────────────┐
│ 💳  Faltan resolver 2 devoluciones de servicios cancelados      1 de 2 listas│
│  …línea explicativa…                                                          │
│ ┌──────────────────────────────────────────────────────────────────────┐   │
│ │ Hotel Maitei (Posadas) · Turismo Cardozo                               │  │
│ │ Resuelto ✓  ·  Factura B 0001-00012345  ·  US$ 700     [ Emitir la dev.]│ │
│ ├──────────────────────────────────────────────────────────────────────┤   │
│ │ Excursión Cataratas · Turismo Cardozo             $ 720.000  [Resolver]│  │
│ └──────────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────┘
```

- **Cada devolución se emite por separado.** Como son de monedas distintas, son dos notas de crédito
  distintas: JAMÁS una sola. (Una NC es de una sola moneda — 2026-06-09.)
- **Emitir** dispara el flujo YA aprobado del 2026-07-15: aparece el "¿Seguro? Una vez emitida no se puede
  deshacer." → `Sí, emitir` → estados **emitiendo (ámbar) → emitida (verde, con número + Ver/Descargar PDF
  + Enviar al cliente) → rechazada (rojo, motivo de AFIP + Reintentar)**. No se rediseña acá: se reusa tal
  cual.
- El servicio ya estaba tachado "Cancelado" en la lista de servicios; la factura sigue viva por el resto.

**Todo resuelto y emitido:** cuando las N devoluciones quedaron emitidas, el panel entero desaparece de la
tira de avisos accionables (ya no hay nada pendiente que hacer) — igual que el panel del caso normal cuando
llega a "emitida". No queda un cartel vacío ocupando lugar.

---

## 4. Estado sin factura de esa moneda (P5=A)

Si un servicio está en una moneda para la que la reserva NO tiene ninguna factura activa, no se puede
resolver: no hay contra qué emitir. En vez del desplegable vacío, esa fila muestra un cartel neutro.

```
│ Hotel Maitei (Posadas) · Turismo Cardozo            US$ 700  [Resolver]│
│  ┌──────────────────────────────────────────────────────────────────┐ │
│  │ Resolver la devolución de:  Hotel Maitei (Posadas) — US$ 700      │ │
│  │                                                                  │ │
│  │ No encontramos una factura en dólares en esta reserva. Revisá     │ │
│  │ que la factura de este servicio exista antes de emitir la         │ │
│  │ devolución.                                                       │ │
│  └──────────────────────────────────────────────────────────────────┘ │
```

- Texto EXACTO (moneda según el servicio): *"No encontramos una factura en {dólares|pesos} en esta
  reserva. Revisá que la factura de este servicio exista antes de emitir la devolución."*
- **No** deriva a "administración" ni a ningún rol (2026-07-08). **No** ofrece elegir una factura de otra
  moneda (regla dura: la moneda la manda la factura). El botón "Guardar" no aparece en este estado.

---

## 5. Estados que el front debe contemplar (checklist de implementación)

- **Cargando:** trae la lista de devoluciones pendientes (spinner sutil estándar).
- **Vacío / no aplica:** no hay devoluciones viejas por resolver → el panel no aparece (nada ocupa el lugar).
  Si el sistema YA sabía la factura y el monto (caso normal), NO entra por acá: va por la spec del 2026-07-15.
- **Lista con pendientes:** §1.
- **Resolviendo una fila (formulario abierto):** §2. Con validación en vivo del botón "Guardar".
- **Sin factura de esa moneda:** §4.
- **Guardado OK:** la fila pasa a "Resuelto ✓ · Factura … · monto" + botón "Emitir la devolución"; contador
  sube. §3.
- **Error del server al guardar** (recuperable): el formulario de esa fila queda con TODO lo cargado intacto
  (factura, monto, motivo) + cartel rojo arriba de los botones: *"No se pudo guardar. Revisá la conexión y
  probá de nuevo."* Reintenta en el mismo botón. Nunca se pierde lo cargado. (Ronda 2, 2026-06-06.)
- **Error de guarda del backend** (tope de saldo, moneda incoherente, estado inválido): se muestra el
  mensaje neutro en criollo que devuelva el backend (`t5ErrorMessage`), sin IDs/enums/stack. Ej. tope de
  saldo → "El saldo de la factura no alcanza para este monto; revisalo." (texto final del backend).
- **Emitir → emitiendo / emitida / rechazada:** reusa §4 de la spec 2026-07-15 (auto-refresco, no atrapa).
- **Sin permiso `cobranzas.invoice_annul`:** las filas se ven (solo lectura) pero sin botón "Resolver" ni
  "Emitir"; nunca aparece el formulario.

Cada estado con **selector estable (`data-testid`) observable**, sin sleeps arbitrarios (para QA). Sugeridos:
`t5-resolver-list`, `t5-resolver-row-{index}`, `t5-resolver-form`, `t5-resolver-empty-currency`,
`t5-resolver-saved`, `t5-resolver-guard-message`.

---

## 6. Qué NO hay que hacer

- **NO** ventana flotante en ningún paso de resolver (todo EN LÍNEA en la ficha). La ÚNICA ventana permitida
  sigue siendo el "¿Seguro?" de emitir (spec 2026-07-15, P2=B).
- **NO** ofrecer una factura de otra moneda para un servicio (la moneda la manda la factura).
- **NO** sumar ni mezclar pesos con dólares en la lista, ni mostrar un total general.
- **NO** mostrar "diferencia de cambio", "CAE", "RG 4540", Id interno, enum ni texto de código.
- **NO** resolver los dos servicios en una sola nota de crédito (una NC por moneda; cada devolución es su
  propia NC).
- **NO** dejar el casillero del monto vacío y sin ayuda (era el problema original): siempre precargado con
  el precio de venta + la línea de ayuda.
- **NO** perder lo cargado ante un error recuperable.
- **NO** derivar a "administración" ni a otro rol en el estado sin factura.
- **NO** poner botones de acción en la fila de la bandeja "Comprobantes por resolver" (sigue pasiva, link a
  la ficha — 2026-07-10 P7/P8/P9). Todo esto de resolver vive en la FICHA.

---

## Qué necesita del backend (dependencia, para los implementadores)

Esta pantalla no se puede construir sin estos tres cambios de backend. NO son decisiones de UX; van al
equipo de backend/dominio.

1. **Aflojar el guard `INV-T5-RESOLVE-STATE`** ("Solo se puede resolver una devolución parcial pendiente de
   un único servicio") para permitir **resolver UNO de VARIOS pendientes**. Hoy bloquea el caso real de
   Gastón (dos servicios cancelados esperando devolución). El endpoint de resolver debe recibir CUÁL
   servicio/renglón se está resolviendo, y dejar los demás pendientes intactos.
2. **Exponer, por cada devolución vieja pendiente, los datos de la fila:** nombre del servicio, operador,
   moneda y **`LineSaleAmount`** del renglón (como valor sugerido del monto). Hoy el panel no tiene con qué
   armar la lista ni con qué precargar el monto.
3. **Filtro de facturas por la moneda del servicio:** el front ya recibe `reserva.invoices[]` con `currency`
   (obra 2026-07-16); el desplegable filtra por la moneda del servicio que se está resolviendo. Confirmar
   que el endpoint de resolver **valida en el server** que la factura elegida sea de esa moneda (no confiar
   en el filtro del front — regla de seguridad: la corrección de negocio la garantiza el backend).

Además, mantener el circuito de emisión ya existente (`resolvePartialCreditNote` → estado "listo para
emitir" → `emitPartialCreditNote`) por cada servicio resuelto.

---

## SPEC APROBADA PARA IMPLEMENTAR (Gastón, 2026-07-17) — resumen ejecutable

Respuestas: **P1=A** (lista completa a la vista, "Resolver" por fila) · **P2=A** (línea explicativa, texto
exacto) · **P3=A** ("¿Cuánto se le devuelve al cliente?" precargado con precio de venta + ayuda) · **P4=A**
("¿Por qué corresponde esta factura y este monto?" obligatorio + ayuda) · **P5=A** (cartel neutro sin
factura de esa moneda).

- **Reemplaza** `t5-resolver-legacy` (formulario pelado) por la **lista de §1** + **formulario por fila de
  §2**.
- **Reusa** para emitir: el flujo completo de la spec 2026-07-15 (¿Seguro? + emitiendo/emitida/rechazada).
- **Reusa** `ElegirFacturaDestinoInline` + formato `Factura B 0001-00012345 — US$ 900`, filtrado por moneda.
- **Textos exactos:** los de §1 (línea explicativa), §2 (encabezado, tres preguntas y sus ayudas, botones) y
  §4 (sin factura de esa moneda).
- **Componente:** `src/TravelWeb/src/features/cancellations/components/PartialCreditNoteEmissionPanel.jsx`
  (sub-estado BLOCKED). Extraer el formulario por fila a un subcomponente si crece; mantener la lógica pura
  de validación separada, con tests.
- **Depende de backend:** ver sección "Qué necesita del backend" (aflojar guard + exponer datos por renglón
  + validar moneda en server).
