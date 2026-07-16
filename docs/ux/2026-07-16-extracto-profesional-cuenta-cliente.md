# Extracto profesional + rediseño de la cuenta corriente del cliente (Tanda D2)

> **Fecha:** 2026-07-16
> **Pantalla:** `CustomerAccountPage.jsx` (cuenta corriente del cliente).
> **Estado:** APROBADA por Gastón (P1..P6 = todas la opción recomendada, 2026-07-16).
> **Gate UX:** cumplido. `frontend-senior` implementa esta spec al pie de la letra; cualquier
> desvío por costo técnico o regla de negocio se le repregunta a Gastón ANTES de desviarse.
> **Origen:** Gastón: "quiero imagen profesional, sacar lo repetido, no parece un extracto de verdad".
> Requisitos de fondo aprobados en concepto desde un informe de patrones ERP (SAP/Odoo/NetSuite).

---

## 0. Lo que cambia y lo que DEROGA

Esta obra **DEROGA** dos puntos de la decisión del **2026-07-15** ("Las multas por anulación se ven
en la cuenta del cliente"), que quedaba así:
- ~~"La multa vive SOLO en el bloque nuevo 'Multa pendiente de cobro'. El extracto 'Estado de cuenta'
  NO cambia."~~ (P1 del 2026-07-15)
- ~~"El extracto sigue mostrando solo los viajes vivos."~~

**A partir del 2026-07-16 (P1 = A):** las reservas anuladas Y las multas **entran DENTRO del extracto**.
La reserva anulada se muestra con su factura + su nota de crédito debajo (contra-asiento que la cancela,
saldo vuelve solo); la multa se muestra como un renglón (nota de débito) que SUMA al saldo. En
consecuencia, el **saldo de arriba pasa a INCLUIR las multas**, para que siga coincidiendo con el saldo
de cierre del extracto (era justamente la preocupación del 2026-07-15: que el saldo de arriba coincida
con el del extracto — ahora coincide porque ambos incluyen la multa).

El bloque separado "Multa pendiente de cobro" (`MultaPendienteDeCobroBlock.jsx`) **desaparece** como
cartel apilado arriba: su información pasa a la línea "Multas abiertas" de la foto de saldo + los
renglones de multa dentro del extracto.

---

## 1. Layout final (mockup definitivo)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ ←  Fam. García        ✉ garcia@mail.com   ☎ 11-4444-5555   CUIT/DNI 20-…      │
│                                                            [ + Nuevo presupuesto ]│
├─────────────────────────────────────────────────────────────────────────────┤
│  SALDO DE LA CUENTA                          En pesos ($)     En dólares (US$) │
│                                                                                │
│   Facturado sin cobrar                         180.000            300          │
│   Multas abiertas                               20.000             —           │
│   Crédito a favor                            −  15.000          −  50          │
│  ────────────────────────────────────────────────────────────────────────     │
│   SALDO                                     ● 185.000  debe   ● 250  debe       │
│                                                             [ Usar saldo a favor ]│
└─────────────────────────────────────────────────────────────────────────────┘

  [ Estado de cuenta ]   [ Reservas ]   [ Facturación ]   [ Datos bancarios ]
  ═══════════════════

  EXTRACTO — Pesos ($)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ Fecha     Documento                          Debe      Haber      Saldo   │
  │ 20/05/26  Factura B 0001-00009 · R-1050 Bariloche  90.000    —     90.000 │
  │ 21/05/26  Nota de crédito 0003-00002 (anulación) · R-1050  —  90.000    0 │
  │ 02/06/26  Factura B 0001-00012 · R-1042 Cancún   180.000    —    180.000  │
  │ 10/06/26  Recibo 0001-00045 (efectivo) · R-1042      —   100.000  80.000  │
  │ 15/06/26  Nota de débito 0002-00003 (multa) · R-1050  20.000  —  100.000  │
  │ ─────────────────────────────────────────────────────────────────────────│
  │                                            Saldo al día (debe):  $ 185.000 │
  └─────────────────────────────────────────────────────────────────────────┘

  EXTRACTO — Dólares (US$)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ Fecha     Documento                          Debe      Haber      Saldo   │
  │ 05/06/26  Factura B 0002-00004 · R-1055 Miami    350      —        350    │
  │ 12/06/26  Recibo 0002-00008 (transferencia) · R-1055  —   50       300    │
  │ ─────────────────────────────────────────────────────────────────────────│
  │                                            Saldo al día (debe):   US$ 300  │
  └─────────────────────────────────────────────────────────────────────────┘
```

---

## 2. La "foto de saldo" (encabezado) — detalle

Un **solo recuadro** reemplaza a los 4-5 cartelitos apilados de hoy (chip "Debe", bloque "Multa
pendiente de cobro", cartel "A FAVOR", cartel "Crédito no aplicado"). Muestra **una columna por moneda**
(pesos a la izquierda, dólares a la derecha), NUNCA sumando ARS + USD (regla dura multimoneda 2026-06-09).

Tres líneas de composición + total, por moneda:

| Línea                     | Qué es                                                        | Color del número |
|---------------------------|---------------------------------------------------------------|------------------|
| **Facturado sin cobrar**  | Lo que el cliente debe por viajes vivos (venta confirmada − cobrado). | Neutro (slate)   |
| **Multas abiertas**       | Multas por anulación pendientes de cobro (con o sin comprobante todavía). | Ámbar            |
| **Crédito a favor**       | Saldo a favor disponible del cliente (se muestra en negativo, resta). | Verde            |
| **SALDO**                 | Facturado sin cobrar + Multas abiertas − Crédito a favor.     | Rojo si debe · Verde si a favor · Gris si 0 |

Reglas de la foto de saldo:
- **Solo aparece una línea si tiene monto en esa moneda.** Si el cliente no tiene multas, la línea
  "Multas abiertas" no se dibuja (esconder complejidad). Si no tiene crédito, no se dibuja "Crédito a
  favor". El "Facturado sin cobrar" y el "SALDO" están siempre.
- **El SALDO al pie manda el color:** rojo si el cliente debe, verde si tiene saldo a favor neto, gris
  si está en 0. Debajo del número va la palabra chiquita **"debe"** (rojo) o **"a favor"** (verde);
  nada cuando es 0.
- **Multa sin comprobante todavía:** dentro de "Multas abiertas", la parte que todavía no tiene
  comprobante emitido se distingue con una segunda línea chica ámbar: **"(incluye $ X en trámite)"**.
  No se usa jerga fiscal (nada de "ND", "CAE", "en revisión" crudo). Esto conserva lo decidido el
  2026-07-15 sobre distinguir la multa firme de la que está saliendo, pero ahora dentro de la foto.
- **Botón "Usar saldo a favor":** aparece a la derecha de la foto SOLO si hay crédito a favor > 0 y el
  usuario tiene permiso `cobranzas.edit`. Al tocarlo se abre EN LÍNEA, debajo de la foto, la ficha
  `UsarSaldoAFavorInline` que ya existe (aplicar a otra reserva / devolver). Nunca ventana flotante.
- **Aplicaciones de saldo a favor revertibles:** la lista "Saldo a favor aplicado a otras reservas"
  (con su reversión en línea) se mantiene tal cual, colgada debajo de la foto de saldo (no como cartel
  suelto arriba). Solo aparece si hay aplicaciones vivas.

### Qué se SACA del encabezado (P3 = A)
- Las 4 tarjetitas **Documentado / Cobrado / Reservas / Comprobantes**. Motivo: "Reservas" y
  "Comprobantes" repiten el número que ya está en las solapas; "Documentado/Cobrado" repite plata que
  el extracto ya totaliza.
- El avatar grande y el bloque de identidad de media pantalla se reducen a **una línea sobria**:
  nombre + contactos + CUIT/DNI. Sin círculo gigante de inicial.
- El chip suelto "Debe en $" / "Debe en US$" (ahora vive como el "SALDO" de la foto).
- El bloque ámbar "Multa pendiente de cobro" (ahora es la línea "Multas abiertas" + renglones del extracto).
- Los carteles verdes "A FAVOR EN $" y ámbar "CRÉDITO NO APLICADO EN $" apilados (ahora son la línea
  "Crédito a favor" de la foto; el crédito no aplicado se resuelve como nota chica bajo esa línea si el
  backend lo separa — ver §7).

---

## 3. El extracto (solapa "Estado de cuenta") — detalle

**Es un documento de CONSULTA, de solo lectura. No tiene botones de acción por renglón** (las acciones
—ver PDF, eliminar cobro, anular recibo— viven en la ficha de la reserva). Se mantiene el botón
"Nuevo cobro" arriba de la solapa (acción de cabecera, no por renglón), como hoy.

### Columnas (P4 = A): 5 columnas
`Fecha` · `Documento` · `Debe` · `Haber` · `Saldo`

- **Documento** = tipo + número + reserva, todo junto en una columna. Ej.:
  `Factura B 0001-00012 · R-1042 Cancún`. Se funden las viejas columnas "Concepto" y "Comprobante".
  - El número de reserva (`R-1042`) es un **link** a la ficha de esa reserva (P6 = A: ahí vive su
    extracto propio). Nunca se muestra un Id interno/GUID; solo el número legible.
  - Nunca se muestra el término fiscal crudo. "Nota de crédito" / "Nota de débito" / "Factura" /
    "Recibo" SÍ se usan (es pantalla de facturación, glosario 2026-07-08). Entre paréntesis, en
    criollo, el motivo cuando aporta: `(anulación)`, `(multa)`, `(efectivo)`, `(transferencia)`.
- **Debe** = lo que SUMA a la deuda (factura, nota de débito/multa). En blanco ("—") si es abono.
- **Haber** = lo que RESTA (cobro, nota de crédito). En blanco ("—") si es cargo.
- **Saldo** = saldo corriente después de ese movimiento. Rojo si debe, verde si a favor, gris si 0.

### Un bloque por moneda
Una tabla por moneda (Pesos primero, Dólares después; el orden lo decide el backend). **NUNCA se
mezclan ARS y USD en la misma columna de saldo** (regla dura 2026-06-09). Cada bloque cierra con su
propia línea **"Saldo al día (debe): $ …"**.

### Anulados visibles con contra-asiento (P1 = A)
- Una reserva **anulada NO desaparece** del extracto. Se ven sus dos renglones:
  1. La **factura** original (Debe).
  2. La **nota de crédito** de la anulación (Haber), que la cancela → el saldo vuelve al valor previo.
- La **multa** de esa anulación es un renglón aparte (nota de débito) que SÍ suma al saldo, con su
  `(multa)` y el número de reserva.
- Orden: cronológico estricto (el backend ya entrega las líneas ordenadas con su saldo corriente).

---

## 4. Estados de la pantalla

| Estado | Qué se ve |
|--------|-----------|
| **Cargando** | La foto de saldo muestra "…" en lugar de números (no un "$0" falso que después salte). El extracto muestra "Cargando estado de cuenta…" con el spinner, como hoy. |
| **Cuenta con deuda** | Foto de saldo con SALDO en rojo + "debe" debajo. Extracto con sus bloques. |
| **Cuenta al día (sin deuda, sin crédito)** | Foto de saldo: SALDO en gris/verde con el texto **"Al día — sin deuda pendiente"**. Las líneas "Multas abiertas" y "Crédito a favor" no aparecen. El extracto igual muestra su historial de movimientos (cerró en 0). |
| **Cliente con saldo a favor neto** | SALDO en verde + "a favor" + botón "Usar saldo a favor". |
| **Cliente sin movimientos** (nuevo, nunca compró) | Foto de saldo con **"Al día — sin movimientos"**. Extracto: estado vacío **"Todavía no hay movimientos en esta cuenta."** (como hoy). |
| **Sin permiso de ver montos** | Esta pantalla es del lado VENTA: lo que el cliente debe y pagó SÍ se ve sin `cobranzas.see_cost` (regla 2026-06-09). El gate de ACCESO a la pantalla es `clientes.view` + `cobranzas.view` (lo controla el backend). No hay enmascarado de costo acá (no hay costo ni margen del lado cliente). |
| **Error de carga** | Mensaje en rojo **"No se pudo cargar el estado de cuenta."** + botón **"Reintentar"** (como hoy). La foto de saldo, si el overview cargó, se mantiene; si falló el overview, cae en el estado de error de la página. |
| **Base de datos no disponible** | El componente `DatabaseUnavailableState` de siempre. |

---

## 5. Multimoneda (reglas duras, no se rompen)
1. ARS y USD **SIEMPRE separados**, nunca sumados ni convertidos (ni en la foto de saldo ni en el
   extracto ni en las multas).
2. **Nunca** aparece la palabra "diferencia de cambio".
3. Si el cliente maneja **una sola moneda**, se ve como una cuenta de una moneda (una sola columna en
   la foto, un solo bloque en el extracto). La segunda columna/bloque solo aparece si de verdad hay dos.

---

## 6. Qué NO hacer
- **No** ventana flotante para nada (ni cobro ni usar-saldo ni reversión): todo EN LÍNEA (regla dura
  "el modal me parece horrible").
- **No** botones de acción por renglón del extracto (es documento de consulta).
- **No** mostrar Id/GUID, enum interno, texto crudo de error de AFIP, "CAE", "RG 4540", ni el token de
  estado (`pendingCollection`/`issuing`/`underReview`): siempre en criollo.
- **No** sumar monedas en ningún total.
- **No** volver a apilar cartelitos de colores arriba: la composición vive en UNA foto de saldo.
- **No** recalcular saldos/multas en el front: todo viene calculado del backend; el front solo pinta.
- **No** reintroducir las 4 tarjetitas del encabezado.

---

## 7. Qué necesita del backend

> Referencia: `src/TravelApi.Application/DTOs/CustomerAccountDtos.cs` y
> `src/TravelApi.Domain/Reservations/CustomerAccountStatementBuilder.cs`.
> El front NO deduce nada: todo esto se calcula en el servidor.

1. **El extracto del cliente (`CustomerAccountStatementDto`) debe incluir las reservas ANULADAS como
   renglones** — hoy el builder las excluye ("muestra solo viajes vivos"). Cada anulada aporta:
   - su **factura** original (línea `Charge`),
   - su **nota de crédito** de anulación (línea `Credit`),
   - su **multa** como **nota de débito** (línea `Charge`), si la hubo.
   Todas con `Kind` correcto (`Invoice` / `CreditNote` / `DebitNote`), `Date`, `DocumentRef` (número
   legible), `Description` (motivo en criollo), `ReservaPublicId` + `NumeroReserva`, y su
   `RunningBalance` recalculado. El `ClosingBalance` de cada moneda pasa a **incluir las multas**.

2. **La multa cuenta en el saldo (`Debe`) de la cuenta.** Hoy `Summary.ReceivableByCurrency` (el "Debe"
   de arriba) NO incluye las multas — viven aparte en `PendingPenalties`. Para que la foto de saldo y
   el extracto coincidan (P1 = A), el saldo de cierre por moneda del extracto debe reconciliar con el
   SALDO de la foto **incluyendo las multas**. Backend define el número autoritativo (una sola fuente),
   y expone la **composición** para la foto de saldo:
   - `facturadoSinCobrar` por moneda (= lo de hoy: venta confirmada − cobrado de viajes vivos),
   - `multasAbiertas` por moneda, separando la parte **firme** (comprobante emitido) de la parte
     **en trámite** (emitiéndose o en revisión) — reusar lo que ya arma `CustomerPendingPenaltyTotalDto`
     (`FirmAmount` / `NotYetIssuedAmount`),
   - `creditoAFavor` por moneda (= `CreditBalanceByCurrency` de hoy),
   - `saldo` por moneda = facturadoSinCobrar + multasAbiertas − creditoAFavor.
   Ideal: un DTO nuevo `CustomerAccountBalanceCompositionDto` (o campos aditivos en
   `CustomerAccountSummaryDto`) para no obligar al front a re-sumar.

3. **Crédito no aplicado:** si el backend lo sigue separando (`UnappliedCreditByCurrency`), el front lo
   muestra como nota chica bajo "Crédito a favor" (no como cartel aparte). Confirmar si sigue haciendo
   falta como línea propia o si se pliega dentro de "Crédito a favor".

4. **Compatibilidad:** los escalares legacy (`TotalSales`/`TotalPaid`/`TotalBalance`) y los campos
   viejos por moneda quedan; lo nuevo es **aditivo**. El endpoint sigue siendo
   `GET /customers/{id}/account/statement` (extracto) + `GET /customers/{id}/account` (overview con la
   composición). Nada de endpoints nuevos si se puede evitar.

5. **`PendingPenalties` / `MultaPendienteDeCobroBlock`:** al mudar las multas al extracto + la foto de
   saldo, el bloque separado deja de renderizarse. El dato `PendingPenalties` puede seguir viniendo (lo
   usa la foto para la línea "Multas abiertas" y el "(incluye $ X en trámite)"); no hace falta borrarlo
   del backend, pero el front deja de dibujar el bloque viejo.

---

## 8. Resumen para implementadores (frontend-senior)

- Archivo principal: `src/TravelWeb/src/features/customers/pages/CustomerAccountPage.jsx`.
- Solapa del extracto: `src/TravelWeb/src/features/customers/components/EstadoCuentaClienteTab.jsx`
  (pasar de 6 a 5 columnas; fundir Concepto+Comprobante en "Documento"; renglones de anuladas/multas).
- Bloque a **retirar del render**: `MultaPendienteDeCobroBlock.jsx` (su info va a la foto de saldo +
  extracto). No borrar el archivo hasta confirmar que nada más lo usa.
- **Componente nuevo sugerido:** `FotoDeSaldoCuenta.jsx` (o inline en la página) que pinta la foto de
  saldo por moneda leyendo la composición del backend. Mantener la lógica de "qué mostrar" en un helper
  puro testeable (molde `pendingPenaltiesLogic.js`), el JSX solo pinta.
- Molde visual del extracto: `SupplierExtractoSection.jsx` (el lado proveedor ya usa este patrón).
- El extracto por reserva ya existe y ya está conectado (`EstadoCuentaExtracto.jsx` dentro de
  `ReservaDetailPage.jsx`); NO se crea pantalla nueva (P6 = A). El link del renglón lleva a
  `/reservas/{publicId}`.
- Depende del trabajo de backend de §7 (extracto con anuladas+multas + composición del saldo).
  Coordinar: primero backend expone los renglones y la composición, después el front pinta.
- Gate final obligatorio: `data-exposure-reviewer` (que no se filtre ningún interno) + `frontend-reviewer`
  (que cumpla esta spec y la guía).
