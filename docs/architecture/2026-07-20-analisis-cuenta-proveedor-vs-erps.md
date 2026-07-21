# Análisis: cuenta corriente del proveedor + registrar pago, vs. cómo lo hacen los ERP de verdad

Fecha: 2026-07-20
Autor: erp-systems-expert (análisis, sin implementación)
Motivo: queja textual de Gaston sobre la pantalla de pago a proveedor ("extremadamente confusa... no te dice después dónde impacta... la 1051 no aparece en cuenta corriente pero sí en nueva factura")

Alcance: SOLO análisis y propuesta. No se tocó código de producto.

---

## 1. El flujo ACTUAL, en criollo (con evidencia)

### 1.1 Qué le muestra la pantalla hoy

La ficha del proveedor (`SupplierAccountPage.jsx`) tiene 7 solapas: Cuenta corriente, Deuda por reserva, Servicios comprados, Facturas operador, Reembolsos, Datos bancarios, Datos. La acción de pagar vive en la solapa "Cuenta corriente" (`D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelWeb\src\features\suppliers\pages\SupplierAccountPage.jsx:1651-1659`).

Ahí arriba de todo hay 3 recuadros por moneda ("Le debo" / "Me tiene que devolver" / "Saldo a favor", líneas 179-232) y un botón **"Registrar pago"** que despliega una ficha en línea (`PagarProveedorInline.jsx`) — no un modal, eso está bien resuelto.

### 1.2 Qué le pide el formulario, EN ORDEN

Dentro de "Registrar pago" (`PagarProveedorInline.jsx`), el orden real de los campos es:

1. **Monto** (obligatorio, el primer campo del form) — línea 803-818.
2. Moneda / "pagar en otra moneda" (solo si el proveedor tiene deuda en dos monedas) — línea 761-772, 820-858.
3. Método, fecha, referencia, nota — línea 862-911.
4. Recuadro de tipo de cambio, SOLO si el pago cruza de moneda — línea 913-976.
5. Recién ACÁ, al final del formulario y marcado **"(opcional)"**, aparece: **"Imputar a una reserva / servicio"** — un desplegable de reserva y, si la reserva tiene servicios de ese proveedor, un segundo desplegable de servicio (líneas 978-1046).
6. Botón "Confirmar pago".

Es decir: el sistema le pide primero CUÁNTO pagar y en qué moneda, y solo al final —y como algo optativo, casi escondido— le pregunta A QUÉ RESERVA/SERVICIO corresponde ese pago. El modelo mental que le impone al usuario es "primero la plata, después (si te acordás) el destino", al revés del orden con el que un cajero piensa la operación ("le estoy pagando ESTA factura/reserva del operador").

### 1.3 Qué pasa DESPUÉS de guardar — acá está el hueco más grave

Al confirmar, el componente llama `onGuardado()` (línea 690), que en la página padre (`handlePagoGuardado`) cierra la ficha y recarga el overview + el extracto. El único lugar donde el usuario puede "ver" el pago después es el Extracto de cuenta (`SupplierExtractoSection.jsx`), que es un libro mayor CRONOLÓGICO por moneda (fecha / concepto / comprobante / cargo / abono), no por reserva.

La descripción de la línea de un pago en el extracto es literalmente el **método de pago** ("Transferencia", "Efectivo"...) o, sin ese permiso, el texto genérico **"Pago al operador"** — la evidencia exacta:

```
D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Infrastructure\Services\SupplierService.cs:838-841
string description = canSeePaymentDetails && !string.IsNullOrWhiteSpace(payment.Method)
    ? payment.Method
    : "Pago al operador";
string? documentRef = canSeePaymentDetails ? payment.Reference : null;
```

**A la reserva/servicio que el usuario eligió imputar NO se la nombra en ningún lado del extracto.** Si querés saber "¿esto bajó la deuda de la 1051?" tenés que ir manualmente a la solapa "Deuda por reserva", buscar esa reserva y comparar el saldo antes/después de memoria — la pantalla no te lo dice. Esto es EXACTAMENTE lo que Gaston describe: "tenés la opción de imputar... pero no te dice después dónde impacta".

### 1.4 El botón "Usar saldo a favor" sí cierra el círculo (referencia de lo que está bien hecho)

`UsarSaldoOperadorInline.jsx` SÍ hace lo correcto: pide la reserva destino primero, sugiere el monto (mínimo entre deuda de esa reserva y saldo disponible), y el extracto después muestra una sección aparte "Saldo a favor aplicado a reservas" con el nombre de la reserva destino (`SupplierExtractoSection.jsx:224-356`, especialmente la línea 254 `{aplicacion.targetReservaNumber ?? "la reserva"}`). Ese patrón —reserva primero, feedback de destino después— es el que falta en "Registrar pago".

### 1.5 Deuda que nace del SERVICIO, no de un documento del proveedor (contexto real que hay que respetar)

A diferencia de un ERP genérico donde la Cuenta por Pagar nace SIEMPRE de una factura del proveedor, acá la deuda nace del **servicio confirmado** (regla oficial única: `WorkflowStatusHelper.CountsForSupplierDebtByType`, `D:\Documentos\MagnaTravel\MagnaTravel-Cloud\src\TravelApi.Domain\Entities\WorkflowStatusHelper.cs:85-91` — solo cuenta si el estado mapea a "Confirmado"). La "Factura del operador" (`SupplierInvoicesSection.jsx`) es un documento OPCIONAL que reclasifica servicios ya existentes, no la fuente de la deuda. Esto es una diferencia de fondo con el patrón ERP de manual (factura = origen de la deuda) y el rediseño tiene que respetarla, no forzar "factura primero".

---

## 2. Cómo lo resuelven los ERP establecidos (con fuentes)

El patrón es consistente entre SAP Business One, Odoo, Dynamics 365 Business Central y NetSuite, con el mismo esqueleto:

1. **El pago es un documento propio**, con vendedor/proveedor, moneda, monto total, fecha, medio.
2. **La aplicación a documentos abiertos es una GRILLA, no un desplegable opcional al final.** Se listan los documentos (facturas/bills) ABIERTOS de ese proveedor con su saldo pendiente; el usuario tilda cuáles paga y cuánto de cada uno (por defecto, el saldo completo; editable hacia abajo). En SAP B1 esto es la tabla de "open transactions" de Salida de pagos ([Outgoing Payments: Vendor & Customer](https://help.sap.com/docs/SAP_BUSINESS_ONE/68a2e87fb29941b5bf959a184d9c6727/44eadab5cf1903fce10000000a1553f6.html)); en Dynamics BC es la página "Apply Entries" con el campo "Amount to Apply" por línea ([Reconcile vendor payment receipts... Apply Entries](https://learn.microsoft.com/en-us/dynamics365/business-central/payables-how-apply-purchase-transactions-manually)); en NetSuite es "Pay Bills" con un checkbox "Apply" por bill ([Paying Bills to a Single Vendor](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N2381924.html)).
3. **Pagar sin aplicar a nada concreto es una opción EXPLÍCITA con su propio nombre**, no el default silencioso: SAP B1 lo llama "Payment on Account" ([Outgoing Payments: Vendor & Customer](https://help.sap.com/docs/SAP_BUSINESS_ONE/68a2e87fb29941b5bf959a184d9c6727/44eadab5cf1903fce10000000a1553f6.html)); Odoo registra el pago "not directly linked to an invoice" contra una cuenta puente hasta que se concilia manualmente ([Payments — Odoo 18.0](https://www.odoo.com/documentation/18.0/applications/finance/accounting/payments.html)).
4. **Después de aplicar, el extracto/ledger del proveedor muestra la relación documento↔pago↔saldo explícitamente.** En Odoo, cuando das de alta una factura y hay un pago pendiente de aplicar de ese proveedor, aparece un banner azul "Add" bajo "Outstanding Debits" ([Payments — Odoo 18.0](https://www.odoo.com/documentation/18.0/applications/finance/accounting/payments.html)); en Dynamics BC "Apply Entries" deja trazada la aplicación en las Vendor Ledger Entries y hay reportes de Aged Payables por documento ([Populating the Payments Journal](https://dynamicscommunities.com/ug/populating-the-payments-journal-in-business-central/)); Odoo también ofrece "Payments matching" para conciliar en lote ([Payments — Odoo 18.0](https://www.odoo.com/documentation/18.0/applications/finance/accounting/payments.html)).

Resumen de la receta ERP: **documento abierto → aplicación explícita en grilla → saldo que se actualiza a la vista, con el vínculo documento-pago siempre visible.**

Sources:
- [Payments — Odoo 18.0 documentation](https://www.odoo.com/documentation/18.0/applications/finance/accounting/payments.html)
- [Outgoing Payments: Vendor & Customer — SAP Business One](https://help.sap.com/docs/SAP_BUSINESS_ONE/68a2e87fb29941b5bf959a184d9c6727/44eadab5cf1903fce10000000a1553f6.html)
- [Reconcile vendor payment receipts or refunds in the payment journal — Business Central](https://learn.microsoft.com/en-us/dynamics365/business-central/payables-how-apply-purchase-transactions-manually)
- [Populating the Payments Journal in Business Central](https://dynamicscommunities.com/ug/populating-the-payments-journal-in-business-central/)
- [Paying Bills to a Single Vendor — NetSuite](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N2381924.html)
- [Applying a Vendor Credit — NetSuite](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_N2393276.html)

---

## 3. Diferencias que explican la confusión de Gaston

| # | Patrón ERP estándar | MagnaTravel hoy | ¿Explica la queja? |
|---|---|---|---|
| 1 | Grilla de documentos abiertos, tildás cuáles pagás | Un desplegable de "Reserva" y, adentro, otro de "Servicio", los dos marcados "(opcional)" y AL FINAL del form | Sí — invierte el orden mental (plata primero, destino después/nunca) |
| 2 | "Pago on account" es una opción con NOMBRE PROPIO y visible | El default silencioso es "sin imputar a nada" (dejar los desplegables vacíos); nada distingue "elegí no imputar a propósito" de "me olvidé de imputar" | Sí — el usuario no sabe si dejarlo vacío fue correcto |
| 3 | Después de aplicar, el ledger muestra documento↔pago | El extracto muestra método de pago y referencia, NUNCA la reserva/servicio imputado (`SupplierService.cs:838-841`) | Sí — es la causa directa de "no te dice después dónde impacta" |
| 4 | La deuda nace de la factura del proveedor | La deuda nace del SERVICIO confirmado (`CountsForSupplierDebtByType`); la factura del operador es opcional y reclasifica, no origina | Contexto real del negocio — NO es un error, hay que diseñar respetando esto, no copiar el "factura primero" a ciegas |
| 5 | Multimoneda: casi ningún ERP pyme lo maneja con la sofisticación con la que acá se separan pesos/dólares sin mezclar | Acá está BIEN resuelto (recuadros separados, "pagar en otra moneda" con TC explícito) — no es la fuente de la confusión | No — esta parte es fortaleza, no hay que tocarla |

---

## 4. Hipótesis sobre "la 1051 no aparece en cuenta corriente pero sí en nueva factura" (relevamiento, no investigación puntual — la investigación específica del caso la está haciendo otro agente)

Encontré una causa estructural consistente con el síntoma, con evidencia de código:

- **"Deuda por reserva"** (y el desplegable "Imputar a una reserva" de Registrar pago, y "Usar saldo a favor") se arman con `GET /suppliers/{id}/account/debt-by-reserva`, que en el backend filtra los servicios con `WorkflowStatusHelper.CountsForSupplierDebtByType(row.Type, row.Status)` — **solo entran servicios en estado "Confirmado" (o "Finalizado", que mapea a Confirmado)** (`SupplierService.cs:2668-2670`, regla en `WorkflowStatusHelper.cs:85-91`). Si una reserva no tiene NINGÚN servicio confirmado de ese proveedor (y tampoco tiene pagos/aplicaciones de crédito/cargos ya registrados contra ella), **directamente no aparece** en el diccionario que arma el desglose (`SupplierService.cs:2765-2779`).

- **"Nueva factura"** (la grilla de servicios elegibles para facturar, dentro de `SupplierInvoicesSection.jsx`) usa `GET /suppliers/{id}/account/services`, que llama a `BuildSupplierServicesQuery` — esa query **NO filtra por el estado del servicio**, solo por `ValidReservationStatuses` (que la reserva esté viva) y por el proveedor (`SupplierService.cs:2036-2110` aprox., ver ej. líneas 2040, 2059, 2078, 2097: el único `Where` es `SupplierId == supplierId && ValidReservationStatuses.Contains(...Status)`). El filtro de "queda algo por facturar" (`remainingToInvoice > 0`) se aplica en el frontend sobre `NetCost`, sin mirar si el servicio está Confirmado o todavía Solicitado (`SupplierInvoicesSection.jsx:77-82`).

**Hipótesis concreta**: si los servicios del operador en la reserva 1051 están en estado "Solicitado" (todavía no confirmados por el operador) — o en cualquier estado que `CountsForSupplierDebtByType` no cuente como deuda — la reserva:
- NO genera deuda con el proveedor → no aparece en "Deuda por reserva" ni en el desplegable de "Registrar pago".
- SÍ tiene `NetCost` cargado y ningún renglón de factura todavía → SÍ aparece como elegible en "Nueva factura", porque esa pantalla no aplica el mismo filtro de "¿esto es deuda real?".

Esto es, en sí mismo, una inconsistencia de reglas de negocio, no solo de presentación: **dos pantallas del mismo circuito de plata usan dos universos distintos de "qué servicios cuentan"**, y una de ellas (Nueva factura) deja registrar una factura de un servicio que el sistema todavía no considera deuda confirmada. Esto queda anotado para que el otro agente que investiga el caso puntual lo confirme contra los datos reales de la 1051 (estado exacto de sus servicios); acá solo dejo la causa estructural verificada en código.

---

## 5. Propuesta de rediseño — UNA recomendación

**Recomendación única**: convertir "Registrar pago" de un formulario lineal con imputación opcional escondida, a un flujo de **2 pasos: elegí qué estás pagando → confirmá el monto y el medio**, reusando el motor actual (nada de la validación de moneda/TC/tope cambia), y agregar una confirmación explícita post-guardado que diga a dónde impactó la plata. Por qué esta y no la otra opción (dejar el form como está y solo agregar el cartel de confirmación): el orden actual sigue siendo el problema de fondo — un cartel de confirmación al final no arregla que el usuario tenga que acordarse de completar un campo opcional escondido.

### 5.1 Qué se mantiene del motor actual (sin tocar)

- El cálculo de deuda por reserva/moneda (`SupplierDebtCalculator`, `debt-by-reserva`).
- La validación de tope por reserva/servicio (`ResolveSupplierPaymentImputationAsync`).
- El manejo de pago cruzado de moneda con TC (ya está bien resuelto, no lo toca esta propuesta).
- "Usar saldo a favor" queda IGUAL — ya sigue el patrón correcto.
- El endpoint `POST /suppliers/{id}/payments` — el payload que arma el frontend no necesita cambiar de forma, solo el ORDEN en que se lo pide al usuario.

### 5.2 Qué cambia (solo presentación + un dato nuevo en la respuesta del guardado)

1. **Paso 1 — "¿Qué estás pagando?"** (reemplaza el desplegable opcional al final):
   - Grilla con las reservas/servicios CON DEUDA de ese proveedor (mismo dato que hoy trae `debt-by-reserva`), cada fila con: reserva, detalle del servicio, moneda, saldo pendiente, y un botón "Pagar esto".
   - Arriba de la grilla, un botón secundario y con nombre propio: **"Pago a cuenta (sin imputar a una reserva)"** — mismo comportamiento que hoy tiene dejar los desplegables vacíos, pero ahora es una DECISIÓN visible, no un olvido.
   - Elegir una fila lleva al paso 2 con el monto y la moneda PRE-CARGADOS (igual que ya hace "Liquidar cargo facturado aparte", que es el único lugar de la ficha actual que ya preselecciona bien — ver `handleCargoChange`, línea 593-612).

2. **Paso 2 — confirmar el pago** (el formulario actual, pero con la reserva/servicio ya fijada arriba, no como un desplegable perdido):
   - Monto, moneda, método, fecha, referencia, nota — igual que hoy.
   - Si eligió "Pago a cuenta", un aviso corto: "Este pago no se imputa a ninguna reserva; queda como saldo a favor para usar después."

3. **Después de guardar — la confirmación que hoy no existe**:
   - Reemplazar el cierre silencioso de la ficha por un cartel de éxito que diga explícitamente el destino: **"Pago registrado. Bajó la deuda de la reserva 1051 en $45.000 (quedan $12.000 pendientes)"** o, si fue a cuenta: **"Pago registrado como saldo a favor. Podés usarlo en cualquier reserva de este proveedor."**
   - Esto solo requiere que el backend devuelva en la respuesta del POST el saldo restante de la reserva/servicio imputado (dato que YA calcula `SupplierDebtCalculator` — no hay lógica nueva, es exponer un número que el motor ya produce).

4. **Extracto**: agregar la reserva/servicio imputado como dato visible en la línea del pago (hoy la `description` es solo el método de pago). Esto es un cambio de DTO (agregar `reservaNumero`/`servicioDescripcion` a `SupplierAccountStatementLineDto`), no de motor — el dato ya está en `SupplierPayment.ReservaId`/`ServicePublicId`, solo no se proyecta hacia esa pantalla.

### 5.3 Mockup ASCII

```
┌─ Registrar pago al proveedor ─────────────────────────────── [x] ─┐
│                                                                     │
│  PASO 1 · ¿Qué estás pagando?                                      │
│                                                                     │
│  Reserva      Detalle              Moneda   Debe        Acción     │
│  ────────────────────────────────────────────────────────────     │
│  1051         Hotel — Bariloche    $        $ 45.000    [Pagar]    │
│  1049         Traslado aeropuerto  US$      US$ 120     [Pagar]    │
│  1032         Paquete — Cancún     $        $  8.500    [Pagar]    │
│                                                                     │
│  ┌───────────────────────────────────────────────────────────┐    │
│  │  ¿No es para ninguna reserva puntual?                      │    │
│  │  [ Pago a cuenta (sin imputar) ]                            │    │
│  └───────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────┘

   ↓ (eligió "Pagar" en la 1051)

┌─ Registrar pago al proveedor ─────────────────────────────── [x] ─┐
│                                                                     │
│  Pagás la reserva 1051 — Hotel Bariloche         [Cambiar]         │
│  Debe: $ 45.000                                                    │
│                                                                     │
│  Monto*        [ 45.000            ]                               │
│  Método        [ Transferencia  ▾  ]      Fecha  [ 20/07/2026 ]    │
│  Referencia    [                    ]     Nota   [              ]  │
│                                                                     │
│                                    [ Cancelar ]   [ Confirmar pago ]│
└─────────────────────────────────────────────────────────────────┘

   ↓ (confirmó)

┌─ ✓ Pago registrado ─────────────────────────────────────────────┐
│  Bajó la deuda de la reserva 1051 (Hotel Bariloche) en $ 45.000. │
│  Esa reserva queda saldada con este operador.                   │
│                                    [ Ver cuenta ]  [ Cerrar ]     │
└───────────────────────────────────────────────────────────────┘
```

### 5.4 Qué NO cambia de alcance de esta propuesta

- No propongo tocar el modelo de "deuda nace del servicio confirmado" — es una decisión de negocio ya tomada (ADR-041/044) y correcta para el rubro (no siempre hay factura del operador).
- No propongo agregar facturas obligatorias del proveedor — siguen siendo opcionales, como hoy.
- La corrección de la inconsistencia "Nueva factura" vs. "Deuda por reserva" (sección 4) es un tema APARTE de esta propuesta de UX de pago — es un fix de reglas de negocio (¿debería "Nueva factura" excluir servicios no confirmados, igual que el resto del circuito?) que el dueño tiene que decidir con el agente que está investigando el caso puntual de la 1051.

---

## 6. Qué NO verifiqué

- No corrí la app para reproducir el caso real de la reserva 1051 — la hipótesis de la sección 4 es 100% de lectura de código (file:line citado), no de datos en producción. El estado exacto de los servicios de la 1051 lo tiene que confirmar el agente que está en esa investigación puntual.
- No revisé `SupplierAccountStatementLineDto` completo ni el resto de los campos disponibles en el DTO de extracto — solo confirmé que `description`/`documentRef` no llevan la reserva/servicio imputado.
- No medí impacto en tests existentes de la propuesta de rediseño (es una propuesta de diseño, no una implementación).
- No consulté a Gaston si la recomendación de "grilla de reservas con deuda como paso 1" es la que prefiere frente a alternativas — sigo la regla de "guiarlo con una recomendación única", pero la decisión final es de él.
- No verifiqué cómo se comporta el pago "a cuenta" (sin reserva) hoy en producción con datos reales — solo el código del payload y el endpoint.
