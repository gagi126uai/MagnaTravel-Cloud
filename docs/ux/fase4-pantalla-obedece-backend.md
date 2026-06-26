# Fase 4 — "La pantalla obedece al backend" — ESPECIFICACIÓN FINAL

> Documento del agente `ux-ui-disenador`. Fecha: 2026-06-26. **Estado: CERRADO — listo para implementar.**
> Fuente única: `docs/ux/guia-ux-gaston.md`. Las 3 decisiones de UX nuevas las respondió Gastón el
> 2026-06-26 (ver guía, sección "Fase 4 ... 2026-06-26"). `frontend-senior` sigue esta spec al pie;
> cualquier desvío por costo técnico o regla de negocio se repregunta a Gastón ANTES (no se decide solo).
> El agente UX NO escribe código.

---

## Principio rector de toda la Fase 4

**La pantalla NO inventa reglas: lee la verdad del backend.** Cero lógica de negocio re-deducida en el
front. Donde hoy el front calcula "facturado", "se puede anular", "se puede borrar" o un monto de plata,
debe **consumir la capacidad/dato que el backend ya expone**. Esto NO cambia el aspecto aprobado de las
pantallas: las hace coherentes con el backend. (Estándar frontend: "Avoid duplicating backend business
rules in frontend code" / "Do not assume response fields that were not verified".)

---

## PARTE 1 — Items de fidelidad (sin cambio visible; corregir de dónde lee el front)

### F4-1. "Emitir factura" y vouchers: derivar de `invoicingStatus`, no de un `FullyInvoiced` local
- **Qué se ve:** igual que hoy (lo ya aprobado en ADR-037 y 2026-06-22). El chip de facturación dice
  `Sin facturar` / `Facturada en parte` / `Facturada total` (wording guía ADR-037, respuesta 1A).
- **Dato del backend que consume:** `reserva.invoicingStatus` (`NotInvoiced` / `PartiallyInvoiced` /
  `FullyInvoiced`) para el chip; **la capacidad de "Facturar" la da el backend** (capability), NO una
  regla local. Para vouchers, "totalmente facturada" = `invoicingStatus === 'FullyInvoiced'`.
- **Regla de visibilidad del botón "Emitir factura" / "Facturar":**
  - Visible y habilitado cuando el backend lo habilita (Confirmada / En viaje / Finalizada — ADR-037).
  - **Si `invoicingStatus === 'FullyInvoiced'` en una reserva activa → el botón DESAPARECE** (ver F4-6,
    regla unificada 2026-06-26). El chip "Factura: Facturada total" explica el porqué.
- **Vouchers cuando está totalmente facturada / en estado congelado:** ver/reimprimir/descargar SÍ;
  emitir/anular NO (guía 2026-06-22 punto 1). Esto se deriva de `invoicingStatus` + capabilities, no de
  un flag local.
- **Sacar del front:** cualquier variable/constante `FullyInvoiced` calculada localmente. Reemplazar por
  lectura de `invoicingStatus`.
- **data-testid sugerido:** `chip-facturacion`, `btn-emitir-factura`.

### F4-2. Botón "Anular reserva" lee la capacidad de ANULAR (no `canCancel`)
- **Qué se ve:** un único botón **"Anular reserva"** (texto idéntico en cabecera y solapas — guía 2026-06-25).
- **Dato del backend que consume:** la capacidad de **anular** (`capabilities.canAnnul.allowed` — el campo
  real que exponga el backend para "deshacer el viaje"). **NO** `canCancel` (que en el vocabulario duro
  es "saldar/pagar el total", cosa distinta — ADR-036 punto 1).
- **Visibilidad:** el botón aparece según los 4 casos de la guía 2026-06-25; **NO aparece** si la reserva
  es terminal (Perdida/Anulada/Esperando reembolso) ni si está En viaje. En pre-venta no es "Anular":
  es "Perdida" (⊗).
- **Si la capacidad de anular no existe en el backend:** NO inventarla en el front. Es dependencia
  técnica → backend la expone. (Repreguntar a Gastón NO corresponde: la regla de cuándo se puede anular
  ya está decidida; lo que falta es que el backend la publique.)
- **data-testid sugerido:** `btn-anular-reserva`.

### F4-3. `canDelete`: el front NO lee una capacidad fantasma
- **Qué se ve:** "Eliminar" (🗑) **solo en Cotización/Presupuesto sin pagos**; en reserva con plata viva
  el botón "Eliminar" **no aparece**, solo "Anular" (guía ciclo de vida 2026-06-08 + ADR-036 P3=A).
- **Dato del backend que consume:** la capacidad de eliminar que exponga el backend
  (`capabilities.canDelete.allowed`). **Hoy el front la lee y el backend NO la manda → eso es el bug.**
  Solución (decisión técnica de backend, no de UX): **o el backend expone `canDelete`, o el front la deja
  de leer y deriva la visibilidad de la regla ya decidida** (etapa pre-venta + sin pagos). Recomendado:
  que el backend la exponga, para mantener el principio "el front no deduce".
- **Importante:** el front NO debe asumir un campo que el backend no devuelve (estándar API). Mientras el
  campo no exista, el botón se rige por la regla decidida; no se lee `undefined`.
- **data-testid sugerido:** `btn-eliminar-reserva`.

### F4-4. Una sola verdad de plata: todos los carteles leen `porMoneda` del backend
- **Qué se ve:** los mismos carteles ya aprobados; lo que se corrige es que **el mismo concepto muestre
  el mismo número en todos lados**.
- **Dato del backend que consume:** `reserva.porMoneda` (un saldo por cada moneda que la reserva usa) y
  el extracto del Estado de Cuenta (`account-statement`). **Ningún cartel recalcula plata por su cuenta.**
- **Reglas duras que se respetan (multimoneda 2026-06-09):** monedas SIEMPRE separadas, NUNCA sumadas ni
  convertidas; nunca "diferencia de cambio"; una sola moneda se ve como hoy.
- **Aclaración para no confundir bug con feature:** "Saldo a Cobrar" (solo lo confirmado) y "de $X
  presupuestado" (chiquito debajo) son DOS cifras legítimamente distintas y ambas decididas (ciclo de
  vida #5 + multimoneda P1). El bug a eliminar es cuando **la misma cifra** (ej. el saldo a cobrar)
  aparece con dos valores distintos en dos carteles. Todos deben tomar `porMoneda`.
- **data-testid sugerido:** `saldo-a-cobrar-por-moneda`, `saldo-presupuestado`.

### F4-5. KPI "Falta facturar": separado por moneda, nunca un número mezclado
- **Qué se ve:** la tarjeta de "Falta facturar" muestra **las dos monedas, una sobre otra** (layout de
  tarjeta multimoneda, guía reportes 2026-06-10). Nunca un solo número que reste ARS y USD.
```
   FALTA FACTURAR
   $ 320.000
   US$ 1.150
```
  Si solo hay una moneda, un solo número (como hoy).
- **Dato del backend que consume:** el monto pendiente de facturar **por moneda** que ya expone el backend.
  El front NO resta monedas distintas.
- **data-testid sugerido:** `kpi-falta-facturar` (con un sub-nodo por moneda: `kpi-falta-facturar-ars`,
  `kpi-falta-facturar-usd`).

---

## PARTE 2 — Las 3 decisiones nuevas de Gastón (2026-06-26)

### F4-6. Regla UNIFICADA de botones: "ya cumplido" se ESCONDE; "bloqueado" va gris (P2=A)
- **Decisión (guía Fase 4 punto 2):** dos motivos distintos, dos tratamientos:
  - **Acción YA CUMPLIDA** en una reserva activa (no queda nada por hacer; ej. "Emitir factura" cuando
    `invoicingStatus === 'FullyInvoiced'`) → **el botón DESAPARECE.** El chip de al lado explica.
  - **Acción BLOQUEADA por estado terminal / permiso / candado** → sigue ADR-035 A: **botón gris** sin
    motivo por botón + UN cartel arriba que explica el estado. En estados de solo lectura, los botones de
    escritura se ocultan (2026-06-22).
- **Cómo se ve (ejemplo, reserva Confirmada ya facturada total):**
```
   [El cliente aceptó]   [Anular reserva]          Pago: Pagada · Factura: Facturada total
   ↑ NO aparece "Emitir factura": ya está todo facturado (acción cumplida → se esconde)
```
- **Implementación (qué decide cada cosa):**
  - "Ya cumplido" = el backend dice que la acción no aplica porque está hecha. Para facturar:
    `invoicingStatus === 'FullyInvoiced'` (o la capability de facturar viene no-disponible **por estar
    completa**). En ese caso: **no renderizar el botón.**
  - "Bloqueado por estado/permiso" = la capability viene `allowed:false` por estado terminal o falta de
    permiso → si es estado de solo lectura, ocultar botones de escritura; si es una acción puntual no
    permitida en estado activo, **gris** sin motivo (el cartel de arriba explica).
- **Qué NO hacer:** NO mostrar "Emitir factura" en gris en una reserva ya facturada total. NO poner texto
  de motivo pegado al botón.
- **data-testid sugerido:** los botones mantienen su testid; el test verifica **ausencia** del nodo
  `btn-emitir-factura` cuando `invoicingStatus === 'FullyInvoiced'`.

### F4-7. KPI "Cobrado este mes": solo plata nueva real + línea chica de saldo a favor (P1=B)
- **Decisión (guía Fase 4 punto 1):** el número grande = **solo plata nueva que entró de verdad**,
  separada por moneda. **El saldo a favor aplicado de otra reserva NO suma al número grande.** Debajo, una
  **línea chica** lo informa, para que nada parezca perdido.
- **Cómo se ve (mes con saldo a favor aplicado):**
```
   COBRADO ESTE MES
   $ 1.250.000  ·  US$ 3.400
   + $ 150.000 aplicados de saldo a favor
```
- **Cómo se ve (mes sin saldo a favor aplicado):**
```
   COBRADO ESTE MES
   $ 1.250.000  ·  US$ 3.400
   (sin línea chica: no hubo saldo a favor aplicado)
```
- **Texto exacto de la línea chica:** `+ $ {monto} aplicados de saldo a favor` (un renglón por moneda si
  hubo en las dos; multimoneda separado, nunca sumado). La línea **solo aparece** si en el mes hubo saldo
  a favor aplicado (monto > 0).
- **Dato del backend que consume:** el "cobrado del mes" debe venir **filtrando la plata real que entró**
  (los puentes de saldo a favor son `AffectsCash = false` y NO deben contar en el número grande). El monto
  de saldo a favor aplicado del mes, por moneda, se muestra en la línea chica. **El front NO recalcula:**
  consume los dos datos ya separados del backend. (Si el backend hoy entrega un solo total mezclado, eso
  es dependencia técnica → backend separa "plata real" de "saldo a favor aplicado". No se decide en front.)
- **Estilo:** número grande = estilo de KPI actual; línea chica = texto secundario gris, tipografía
  chica (mismo tratamiento que "de $X presupuestado").
- **data-testid sugerido:** `kpi-cobrado-mes` (número grande, con sub-nodos por moneda) y
  `kpi-cobrado-mes-saldo-favor` (la línea chica; ausente si no hay saldo aplicado).

### F4-8. Factura A: avisar y FRENAR antes de emitir si el cliente no corresponde (P3=A)
- **Decisión (guía Fase 4 punto 3):** antes de emitir una **Factura A**, si el cliente no es del tipo que
  corresponde (no es Responsable Inscripto), el **cartel de confirmación** (el "¿seguro?" de emitir, el de
  H2 2026-06-24) **muestra un aviso claro y NO deja seguir** hasta corregir. No se manda a AFIP para que
  vuelva rechazada.
- **Dónde se ve:** dentro del flujo de emisión de factura ya aprobado (`EmitirFacturaInline.jsx` + el
  cartel de confirmación de H2). Es un **pre-chequeo** que ocurre al apretar "Emitir factura", ANTES del
  paso de confirmación normal.
- **Cómo se ve (cuando el cliente no corresponde):**
```
   ⚠ Este cliente no es Responsable Inscripto.
     No corresponde Factura A. Revisá el tipo de
     comprobante o la condición del cliente.

                                    [ Volver ]
```
  - **Texto exacto:** `Este cliente no es Responsable Inscripto. No corresponde Factura A. Revisá el tipo
    de comprobante o la condición del cliente.`
  - **Único botón:** `Volver` (no hay "Sí, emitir": está frenado). El vendedor corrige el tipo de
    comprobante o la condición del cliente y reintenta. Nunca se pierde lo cargado (regla Ronda 2 2026-06-06).
- **Cuando el cliente SÍ corresponde:** el flujo sigue como hoy → cartel de confirmación normal de H2
  ("Una vez emitida no se puede eliminar; solo se corrige o anula con una Nota de Crédito." · Volver /
  Sí, emitir).
- **Dato del backend que consume:** la **condición de IVA del cliente / si corresponde Factura A** la
  resuelve el backend. El front solo lee ese veredicto y muestra el aviso. **El front NO implementa la
  regla fiscal** (qué condición de IVA habilita qué comprobante): eso lo confirma el área contable/fiscal;
  el front muestra el aviso con el dato que el backend devuelva.
- **Alcance / límite:** Gastón decidió SOLO el aviso de pantalla. La correctitud fiscal de fondo va por
  `arca-tax-expert-argentina` / contador. Si el backend aún no expone el veredicto, es dependencia técnica
  (no se inventa en el front, no se vuelve a preguntar a Gastón).
- **data-testid sugerido:** `aviso-factura-a-no-corresponde`.

---

## Estados a contemplar (estándar frontend, todos los items de plata/acción)
- **Cargando:** los KPIs y carteles muestran su placeholder de carga; no parpadean números viejos.
- **Vacío:** sin movimientos de plata → chip de pago "Sin movimientos" (guía 2026-06-24); KPI en 0 por
  moneda real (sin "US$ 0" fantasma — ADR-035 B).
- **Error del servidor:** mensaje claro en criollo, sin internos del sistema (regla vigente); no se
  tragan errores en silencio.
- **Sin permiso de costos (`cobranzas.see_cost`):** no se muestran costos ni deuda al operador en ninguna
  moneda; lo que el cliente debe/pagó SÍ se ve (regla general 2026-06-05 + multimoneda 2026-06-09).

## Qué NO hay que hacer (resumen)
- NO recalcular plata, "facturado", "se puede anular/borrar" en el front: leer la verdad del backend.
- NO sumar ni convertir monedas distintas en ningún KPI ni cartel.
- NO contar el saldo a favor aplicado dentro de "Cobrado este mes" (número grande).
- NO mostrar "Emitir factura" en gris cuando ya está facturada total: se esconde.
- NO mostrar texto de motivo pegado a cada botón.
- NO mandar una Factura A a AFIP cuando el cliente no corresponde: frenar antes con el aviso.
- NO asumir campos del backend que no estén verificados; si falta una capacidad, es dependencia técnica
  de backend, no una regla a inventar ni una pregunta a Gastón (la regla de negocio ya está decidida).
