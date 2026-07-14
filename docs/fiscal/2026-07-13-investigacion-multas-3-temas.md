# Investigación fiscal — Multas de operador trasladadas al cliente (3 temas)

Fecha: 2026-07-13
Contexto: ADR-044 (multas por operador). Al anular un viaje, el mayorista cobra una multa. La agencia se la traslada al cliente vía **nota de débito electrónica** asociada a la factura original. Hoy la agencia es Monotributo (Factura C), pero el producto MagnaTravel se vende y debe soportar Responsable Inscripto (Factura A/B). La multa puede estar en USD. El sistema hoy **bloquea** la emisión automática de esa ND cuando la agencia es RI porque no hay firma sobre la alícuota de IVA.

Método: investigación en fuentes oficiales/doctrina argentina. Etiquetas: **[VERIFICADO]** = confirmado en fuente citable; **[INTERPRETACIÓN RAZONABLE]** = criterio fundado pero sin norma explícita para el caso exacto; **[NO VERIFICADO - RIESGO]** = no confirmado, decisión de riesgo.

> Aclaración de alcance: esto NO reemplaza a un contador matriculado. Es investigación para que Gaston decida con información. Todo lo etiquetado como riesgo residual queda listado al final.

---

## TEMA 1 — IVA de la multa trasladada al cliente (agencia Responsable Inscripta)

### Conclusión corta

La multa de cancelación **pura**, trasladada tal cual del operador al cliente, es de **naturaleza indemnizatoria (resarcitoria)** y, como tal, **NO está alcanzada por el IVA** en Argentina: no hay una contraprestación de un servicio de la agencia detrás de ella. En cambio, **todo cargo de gestión PROPIO** que la agencia agregue por administrar la cancelación **SÍ está gravado al 21%** (para RI), porque eso sí es contraprestación de un servicio.

Por lo tanto, la ND que traslada la multa debería llevar el concepto de la multa como **no gravado / no alcanzado**, y — si existe — el cargo de gestión propio como **gravado 21%** (línea separada). Esto es una **[INTERPRETACIÓN RAZONABLE]** con riesgo residual (ver abajo), no una certeza cerrada.

### Fundamento con fuentes

1. **Indemnizaciones y cláusula penal NO están gravadas en IVA (doctrina y dictámenes argentinos).**
   Las indemnizaciones por daños carecen del elemento **onerosidad / contraprestación** que el IVA exige para gravar un "servicio" (art. 3 e inc. e ap. 21 de la Ley de IVA, ley 23.349 t.o. 1997). El beneficiario de la indemnización no recibe la ventaja de un servicio: se lo resarce por un incumplimiento. **La cláusula penal pactada por contrato NO le quita el carácter indemnizatorio al pago** — es una valuación anticipada del daño, no el precio de un servicio.
   Dictámenes citados por la doctrina argentina: **Dict. AFIP (DAT) 78/2007** (penalidades por rescisión anticipada de contrato), **Dict. 83/1996**, **Dict. 21/2004** (indemnizaciones de seguros). Criterio: se analiza la **causa adecuada** del pago, no la etiqueta contractual.
   Fuente: "Las indemnizaciones frente al Impuesto a las Ganancias y al IVA" (Parte 2), Mondaq Argentina — https://www.mondaq.com/argentina/sales-taxes-vat-gst/145726/ (consultado 2026-07-13). **[VERIFICADO]** como doctrina; los dictámenes citados no fueron leídos en su texto literal → ver riesgo.

2. **La agencia de viajes actúa como intermediaria (Ley 18.829): a nombre propio pero por cuenta de terceros.** Su base imponible de IVA es su comisión/margen, no el precio del servicio que traslada del operador. El monto que la agencia recibe como comisión no interviene en el precio neto al turista; y los importes que la agencia mueve por cuenta del operador no son ingreso propio gravado de la agencia.
   Fuentes: Ley 18.829 (https://servicios.infoleg.gob.ar/infolegInternet/anexos/25000-29999/27128/norma.htm); **Dict. DAT 44/01** y **Dict. DAT 8/99** (base imponible servicios de turismo — trivia.consejo.org.ar/ficha/18003 y /17800). **[VERIFICADO]**.

3. **Diferencia clave — trasladar la multa "tal cual" vs agregar cargo propio:**
   - **Multa del operador trasladada tal cual (pass-through)**: la agencia recupera del cliente un cargo que le impuso un tercero (el mayorista). No es contraprestación de un servicio prestado por la agencia → **no genera IVA débito propio de la agencia**. Se comporta como reintegro de un gasto por cuenta y orden / concepto indemnizatorio. **[INTERPRETACIÓN RAZONABLE]**.
   - **Cargo de gestión propio de la agencia** (ej.: "gastos administrativos de cancelación" que la agencia cobra para sí): esto **SÍ es contraprestación de un servicio** de la agencia → **gravado al 21%** para RI. **[VERIFICADO]** en cuanto al principio general (servicio oneroso = gravado 21%, art. 3 y 28 Ley IVA).

### Riesgo si el sistema hace X

- **Riesgo A (sobre-gravar):** si el sistema, por defecto, mete IVA 21% sobre la multa pura trasladada, le cobra al cliente un IVA que **conceptualmente no corresponde** y la agencia ingresa un débito fiscal que no debía. Es "más seguro" ante AFIP pero encarece al cliente y sobre-declara débito.
- **Riesgo B (sub-gravar):** si el sistema marca como "no gravado" un concepto que en el caso concreto **sí es contraprestación** (ej.: la agencia disfrazó su cargo de gestión como "multa del operador"), omite IVA débito → contingencia fiscal para RI. AFIP podría además argumentar que una ND asociada a una **Factura A** (operación gravada) ajusta esa operación gravada y por ende debería llevar IVA. **Este es el nudo del riesgo residual.**

### Recomendación de default para el producto

- **Modelar la multa por líneas tipificadas** (ya existe `OperatorDeductionKind`): la ND debe poder llevar **una línea "multa operador" y otra línea "gestión agencia"** con tratamiento IVA independiente.
- **Default para RI:**
  - Línea **multa del operador (pass-through)** → **no gravado / no alcanzado** (sin IVA), como concepto indemnizatorio. **[INTERPRETACIÓN RAZONABLE]** — con aviso visible de riesgo residual.
  - Línea **cargo de gestión propio de la agencia** → **gravado 21%**. **[VERIFICADO]**.
- **Configurable por agencia/instalación**, no hardcodeado: dejar que el contador de cada agencia RI pueda forzar "multa gravada 21%" si su criterio profesional es el conservador. La alícuota no se hardcodea (memoria de sector: nunca hardcodear alícuotas).
- **Para Monotributo (Gaston hoy):** la Factura C / ND C **no discrimina IVA**. El tratamiento gravado/no-gravado **no cambia el comprobante** ni el importe al cliente. Por eso, para Monotributo, **el bloqueo actual por "falta firma de alícuota" no tiene sentido**: la ND C se puede emitir sin definir alícuota. El gate de alícuota debe aplicar **solo a RI**, no a Monotributo.

Nivel de riesgo de la recomendación: **medio** para RI (nudo del pass-through indemnizatorio vs ajuste de operación gravada), **bajo** para Monotributo.

> `Necesita confirmación profesional:` el tratamiento no-gravado de la multa trasladada por un RI vía ND asociada a Factura A es el punto que un matriculado debería firmar antes de prender emisión automática RI. Para Monotributo no es necesario (no discrimina IVA).

---

## TEMA 2 — Ajuste por el dólar (diferencia de cambio) cuando la multa está en USD y el cliente paga en ARS

### Conclusión corta

1. **Contablemente**: la diferencia entre el TC congelado del comprobante y el TC del día de cobro es un **Resultado financiero — Diferencia de cambio** (cuenta de resultado), que se reconoce **al momento del cobro** (diferencia realizada).
2. **En IVA (RI)**: la diferencia de cambio entre el momento del hecho imponible y el pago **SÍ está gravada**, a la **misma alícuota de la operación de origen** — **PERO solo si la operación de origen está gravada**. Si el concepto de origen es no gravado (la multa indemnizatoria del Tema 1), su diferencia de cambio **también es no gravada**. Y si el cliente paga en la **misma moneda extranjera** (USD), **no hay diferencia de cambio gravada**.
3. **Documentación**: si la diferencia de cambio es **gravada**, corresponde documentarla con **ND fiscal** (con su IVA). Si **no es gravada** (Monotributo, o concepto de origen no gravado), **basta el asiento contable interno**; no hace falta ND fiscal para la parte de dif. de cambio.

### Fundamento con fuentes

- **Art. 10, 5º párrafo, apartado 2 de la Ley de IVA**: integran el precio neto gravado "los intereses, actualizaciones, comisiones, recuperos de gastos y similares percibidos o devengados con motivo de pagos diferidos o fuera de término". La doctrina y AFIP encuadran ahí la **diferencia de cambio** entre el hecho imponible y el pago: **está gravada, y a la alícuota de la operación de origen** (si la venta es 10,5%, la dif. de cambio es 10,5%; si 21%, 21%).
  Fuentes: **Dict. DAT 31/2003** (https://archivo.consejo.org.ar/Bib_elect/septiembre03_CT/documentos/dat3103.htm); Mario Volman, "Facturación en dólares: sus implicancias en el IVA" (ucema.edu.ar); reunión CEAT FACPCE 04/08/2020 (Oscar Fernández). **[VERIFICADO]**.
- **Si se cobra en moneda extranjera (USD), NO se genera diferencia de cambio gravada** — **Dict. DGI 24/91** (Boletín 458). **[VERIFICADO]** (citado por Volman y por doctrina). Coherente: no hubo conversión, no hay mayor precio en pesos.
- **La diferencia de cambio sigue el tratamiento del concepto de origen**: es un accesorio del precio neto. Si el precio neto de origen es no gravado (indemnización), no hay "precio neto gravado" al cual adherir el IVA de la dif. de cambio. **[INTERPRETACIÓN RAZONABLE]** (derivación del art. 10; consistente con que el IVA de la dif. de cambio toma la alícuota del origen).
- **Documentación con ND**: el ejemplo de Volman emite una **ND por la diferencia de cambio incluyendo el IVA** correspondiente, para no soportar el perjuicio. Base normativa de facturar/documentar en moneda extranjera: **RG DGI 3445**. **[VERIFICADO]** como práctica documentada.

### Riesgo si el sistema hace X

- **Riesgo C (mezclar plata):** si el sistema mete la diferencia de cambio dentro del mismo importe de la multa sin separarla, se pierde la trazabilidad contable (no se puede explicar cuánto fue multa y cuánto fue dólar) y se rompe el cuadre del mayor del cliente.
- **Riesgo D (documentar de más):** si el sistema, para Monotributo, emite ND fiscal por la diferencia de cambio, genera comprobantes innecesarios (Monotributo no discrimina IVA; la dif. de cambio es puro resultado contable).
- **Riesgo E (omitir IVA en RI):** si el concepto de origen es gravado y el sistema no aplica IVA a la diferencia de cambio cobrada en pesos, el RI omite débito fiscal.

### Recomendación de default para el producto

- **Congelar el TC del comprobante** (criterio ya cerrado en memoria `nd-multa-usd-cotizacion-congelada` y ADR-012 §3.3): la NC total y la ND por multa heredan el **MonCotiz congelado** de la factura original, no el del día de emisión.
- **La diferencia entre TC congelado y TC de cobro se registra como asiento contable** en cuenta **"Diferencias de cambio" (resultado financiero)**, al momento del cobro. **No** se emite ND fiscal por la diferencia de cambio en el flujo estándar.
- **Excepción para RI con concepto gravado**: si en el futuro se factura una línea **gravada** en USD y el cliente paga ARS a otro TC, ahí la diferencia de cambio **arrastra IVA** y correspondería una **ND fiscal por la diferencia** — marcarlo como caso a resolver (no auto-emitir; enviar a "Comprobantes por resolver"). Para la **multa indemnizatoria no gravada** y para **Monotributo**, la diferencia de cambio es **solo asiento contable**, sin ND.
- **Si el cliente paga en USD** (misma moneda): no hay diferencia de cambio gravada ni resultado por conversión; solo revaluación de saldos si aplica.

Nivel de riesgo de la recomendación: **bajo** (el criterio de art. 10 + congelamiento es sólido y está documentado).

> `Necesita confirmación profesional contable:` la cuenta exacta del plan de cuentas de cada agencia para imputar la diferencia de cambio (resultado financiero). El principio es estándar; el número de cuenta lo define el contador de la agencia.

---

## TEMA 3 — Corrección/re-emisión de comprobantes y plazos

### Conclusión corta

- **(a) Corrección de una ND equivocada**: sí, el camino correcto es **NC que anule la ND errónea + ND nueva correcta**, ambas con **comprobantes asociados (CbtesAsoc)** apuntando a la factura original (y la NC apuntando a la ND que anula). No se "edita" un comprobante ya con CAE: se contra-emite.
- **Plazo RG 4540 (15 días corridos)**: aplica a **TODAS** las NC/ND de los regímenes de facturación de AFIP (**no** solo a la Factura de Crédito Electrónica MiPyME), y el plazo corre **desde el hecho que las origina**, no desde la fecha de la factura original.
- **(b) Plazo máximo para NC que anula una factura por anulación de viaje meses después**: el reloj de los 15 días **arranca en el evento de anulación**, no en la venta. Una anulación 6 meses después está bien: los 15 días se cuentan desde que se confirma la anulación (el "hecho documentable"). No hay un plazo que "venza" respecto de la fecha de la factura original.
- **(c) Moneda**: una ND/NC asociada a una factura en **USD debe emitirse en USD** (MonId=DOL, coherente con el comprobante asociado), **no** en ARS. Con `CanMisMonExt` = "N" cuando el cobro es en pesos.

### Fundamento con fuentes

- **RG AFIP 4540/2019** — plazo de **15 días corridos** desde que surge el hecho/situación (descuentos, bonificaciones, quitas, **devoluciones, rescisiones**, intereses); aplica a **todos** los regímenes de facturación, no solo FCE MiPyME (la FCE tiene reglas transitorias adicionales, art. 4 y 6.a, pero el plazo general de 15 días es transversal); **solo el emisor del comprobante original** puede emitir la NC/ND; **mismo receptor**; debe **identificar individualmente** la/s factura/s que ajusta (CbtesAsoc).
  Fuentes: texto oficial InfoLEG (https://servicios.infoleg.gob.ar/infolegInternet/anexos/325000-329999/326036/norma.htm) y resumen argentina.gob.ar (https://www.argentina.gob.ar/normativa/nacional/resoluci%C3%B3n-4540-2019-326036), consultados 2026-07-13. **[VERIFICADO]**.
- **"Hecho que origina" = anulación, no la venta**: RG 4540 habla del "hecho o situación que requiera su documentación". En una anulación de viaje, ese hecho es la anulación confirmada (criterio T2 de MagnaTravel, ya cerrado). Por eso los 15 días se cuentan desde el evento de anulación. **[INTERPRETACIÓN RAZONABLE]** — consistente con el texto y con la política interna T2 ya definida.
- **NC anula ND / ND anula NC con CbtesAsoc**: el mecanismo estándar de ARCA para revertir un comprobante ya emitido con CAE es contra-emitir el comprobante opuesto vinculándolo. No existe "edición" de un comprobante autorizado. **[VERIFICADO]** como práctica general de factura electrónica (WSFEv1 `CbtesAsoc`).
- **Moneda de la NC/ND asociada**: debe ser coherente con el comprobante que ajusta (memoria `nd-multa-usd-cotizacion-congelada`; RG 5616/2024 sobre `CanMisMonExt`). Una ND en ARS sobre una factura USD rompe la coherencia del set vinculado y el cuadre del mayor del cliente. **[VERIFICADO]** el principio de coherencia; el comportamiento a nivel de campo puntual de la ND se confirma en homologación (ver riesgo).

### ¿El plazo de 15 días es un bloqueo duro?

RG 4540 dice que las NC/ND "**deberán** emitirse dentro de los 15 días". Es un **requisito formal**. En la práctica, ARCA suele **igual otorgar el CAE** pasado ese plazo (no es un rechazo de la webservice por fecha), pero emitir tarde es una **debilidad formal** que puede observarse. **[INTERPRETACIÓN RAZONABLE]** — no confirmado el comportamiento literal de rechazo/aceptación de WSFEv1 por vencimiento del plazo. Por eso el sistema debe **avisar**, no necesariamente **bloquear**.

### Riesgo si el sistema hace X

- **Riesgo F (bloqueo indebido):** si el sistema bloquea la emisión pasados los 15 días, puede impedir una anulación legítima tardía cuando ARCA igual daría CAE. Mejor **avisar** ("fuera del plazo de 15 días — riesgo formal") y permitir emitir con registro.
- **Riesgo G (ND en pesos sobre factura USD):** rompe coherencia y cuadre; posible rechazo o inconsistencia fiscal. Evitar: heredar moneda del comprobante asociado.
- **Riesgo H (editar en vez de contra-emitir):** un comprobante con CAE **no se edita**; si el sistema "corrige" montos sin NC+ND nueva, queda un comprobante fiscal inconsistente.

### Recomendación de default para la pantalla "Comprobantes por resolver"

- **Mostrar la fecha del hecho** (anulación) y un **contador de plazo**: "Plazo sugerido para emitir: N días (RG 4540 — 15 días corridos desde la anulación)". Verde ≥ 6 días, amarillo 1-5 días, rojo/vencido pasados los 15 días.
- **Vencido el plazo**: **no bloquear**; mostrar aviso "Fuera del plazo formal de 15 días (RG 4540). Se puede emitir igual; queda registrado el retraso." + dejar traza en el audit trail.
- **Corrección de comprobante errado**: ofrecer explícitamente el flujo **"anular con NC + emitir ND nueva"**, nunca "editar". Ambos con CbtesAsoc automáticos.
- **Moneda**: la ND/NC hereda **siempre** la moneda y el TC congelado del comprobante asociado; nunca ofrecer cambiar a ARS una ND sobre factura USD.
- **Un aviso por comprobante**: cada factura viva con su propio contador de plazo (evitar mezclar varias facturas en un solo aviso).

Nivel de riesgo de la recomendación: **bajo** para la mecánica (NC+ND, CbtesAsoc, moneda), **medio** para el criterio de plazo (bloquear vs avisar — recomendado avisar).

---

## Riesgo residual que Gaston debe conocer (sin exigir contador, solo informar)

1. **[NO VERIFICADO - RIESGO] Multa no gravada en RI vía ND asociada a Factura A.** El criterio "multa indemnizatoria = no gravada" es sólido en doctrina (Dict. 78/2007, 83/1996, 21/2004), pero **no leí el texto literal de esos dictámenes** ni existe uno específico "multa de cancelación de agencia de viajes trasladada al cliente". AFIP podría sostener que una ND asociada a una operación gravada (Factura A) ajusta esa operación y debe llevar IVA. **Para RI, este punto conviene que lo firme un matriculado antes de prender emisión automática.** Para **Monotributo (Gaston hoy) no aplica**: la Factura/ND C no discrimina IVA.

2. **[INTERPRETACIÓN RAZONABLE] Cargo de gestión propio siempre gravado 21% (RI).** Si la agencia agrega su propio cargo, es servicio gravado. Verificado el principio; el riesgo es solo de clasificación (que se mezcle con la multa).

3. **[VERIFICADO] Diferencia de cambio gravada a la alícuota del origen (RI)** — pero **sigue el tratamiento del concepto de origen**: si la multa es no gravada, su dif. de cambio también. Si el cliente paga en USD, no hay dif. de cambio gravada (Dict. 24/91). Para Monotributo, la dif. de cambio es puro resultado contable.

4. **[VERIFICADO] Plazo RG 4540 = 15 días corridos, general (no solo FCE MiPyME), desde el hecho** (la anulación, no la venta). **[NO VERIFICADO - RIESGO]** el comportamiento literal de WSFEv1 si se emite pasado el plazo (¿otorga CAE igual?). Recomendado **avisar, no bloquear**; confirmar en homologación.

5. **[NO VERIFICADO - RIESGO] Campo puntual de la ND en moneda extranjera en WSFEv1** (`CanMisMonExt`, `MonCotiz` validado contra el comprobante asociado vs el oficial del día). El principio de heredar moneda/TC del comprobante asociado está claro; el comportamiento exacto del webservice se confirma en homologación (mismo test que ya pasa la NC total).

6. **Necesita confirmación: jurisdicción provincial/municipal.** Ingresos Brutos sobre la multa/cargo de gestión y sobre la diferencia de cambio **depende de la provincia** de la agencia. No modelado acá. Marcar `Necesita confirmación: jurisdicción provincial/municipal aplicable` (IIBB puede gravar la comisión/cargo de gestión y, según jurisdicción, la dif. de cambio).

7. **Gate del sistema mal calibrado hoy.** El bloqueo actual de la ND por "falta firma de alícuota" **no debería aplicar a Monotributo** (la ND C no lleva alícuota discriminada). El gate de alícuota debe ser **solo para RI**. Esto es un ajuste de producto, no un tema de riesgo fiscal.

---

## Fuentes consultadas (2026-07-13)

- Ley de IVA 23.349 (t.o. 1997) — biblioteca.afip.gob.ar / argentina.gob.ar
- Ley 18.829 (Agentes de Viajes) — servicios.infoleg.gob.ar/infolegInternet/anexos/25000-29999/27128/norma.htm
- "Las indemnizaciones frente a Ganancias e IVA" (Parte 2) — mondaq.com/argentina/sales-taxes-vat-gst/145726 (cita Dict. 78/2007, 83/1996, 21/2004)
- Dict. DAT 44/01 y DAT 8/99 (base imponible turismo) — trivia.consejo.org.ar/ficha/18003 y /17800
- Dict. DAT 31/2003 (diferencia de cambio en IVA) — archivo.consejo.org.ar/Bib_elect/septiembre03_CT/documentos/dat3103.htm
- Dict. DGI 24/91 (pago en moneda extranjera, sin dif. de cambio gravada) — citado por Volman
- Mario Volman, "Facturación en dólares: sus implicancias en el IVA" — ucema.edu.ar/~mv/Facturacion_en_dolares_.doc
- CEAT FACPCE 04/08/2020, IVA Diferencias de Cambio (Oscar Fernández) — facpce.org.ar
- RG AFIP 4540/2019 — servicios.infoleg.gob.ar/.../326036/norma.htm y argentina.gob.ar/normativa/nacional/resolución-4540-2019-326036
- RG 5616/2024 (`CanMisMonExt`, moneda extranjera) — referida en memoria de proyecto
