# Resolución fiscal de la "cuenta del operador" en anulaciones — MagnaTravel

Fecha: 2026-06-29
Alcance: lado OPERADOR de una anulación de viaje ya pagado al mayorista. NO toca la regla cliente ya cerrada (NC total + ND por la multa si la hay, matriculado 2026-06-01).
Origen: investigación fiscal interna (sin consulta a contador externo, por decisión del dueño). Normativa argentina real con fuente; supuestos y riesgos marcados explícitamente.

## Resumen ejecutivo (para Gaston, en fácil)

Cuando anulás un viaje que ya le pagaste al operador, hay que separar SIEMPRE dos cosas que la gente mezcla: lo que vos le facturás al cliente (eso ya está resuelto, NC + ND), y la **plata entre vos y el operador** (esto es lo que estamos cerrando ahora). La regla de oro: la pata operador es **contabilidad interna tuya, NO se emite nada al ARCA por ella**. Lo único que va al ARCA es la NC/ND al cliente.

La multa que el operador te retiene **no es lo mismo** que una multa que vos le cobrás al cliente por tu cuenta. Y todo depende de un interruptor que hoy está muerto: si con ese operador vos comprás-y-revendés (reseller) o sos solo intermediario que cobra comisión. Ese interruptor cambia el IVA y cambia si la ND al cliente corresponde o no. Recomendación: que ese interruptor **se elija por operador**, porque una agencia real usa los dos modelos al mismo tiempo según el mayorista.

## Ejemplo (fiambrería)

Sos dueño de una fiambrería y un cliente te encarga una picada que vos le comprás a un proveedor (ya se la pagaste, $100). El cliente se arrepiente. Llamás al proveedor: te devuelve $70 y se queda $30 porque ya la había armado. Tenés DOS libretas:

- **Libreta del cliente**: le devolvés su plata (la "nota de crédito"). Ya resuelto.
- **Libreta del proveedor**: "me tiene que devolver $70" y "esos $30 que se quedó, ¿son pérdida mía, o se los cobro al cliente?".

Si al cliente le habías facturado los $100 enteros (sos "reseller"): esos $30 son parte de TU venta, se los cobrás al cliente con una nota de débito o los comés de pérdida. Si vos solo cobrabas $10 de comisión y el proveedor le facturó directo al cliente (sos "intermediario"): esos $30 nunca fueron tuyos, no podés meterlos en TU factura — solo los pasás de mano.

---

## Hechos verificados (normativa y repo)

`Hecho verificado:` **RG AFIP 4540/2019** (vigencia 01/07/2020): la NC/ND debe ir al mismo receptor, con comprobante asociado (`<CbtesAsoc>`) y dentro de 15 días **corridos** del hecho.

`Hecho verificado:` **Mecanismo IVA agencias AR = art. 61 Decreto Reglamentario de la Ley de IVA + intermediación pura** (Dictámenes DAT 44/01 y DAT 8/99, DGI). La base del IVA de la agencia es su comisión/margen vía **deducción con discriminación explícita en factura**, NO por un "régimen de margen" automático. El "régimen especial de agencias de viajes" que aparece googleando es **ESPAÑOL (Ley 37/1992) y NO aplica en Argentina**.

`Hecho verificado:` **Manual del Desarrollador ARCA (WSFEv1/COMPG)**: en NC/ND la moneda y el tipo de cambio son los del comprobante asociado. Hard-codear ARS o TC=1 es error.

`Hecho verificado (repo):` existen modeladas pero MUERTAS las perillas `InvoicingMode` (reseller/intermediario) y `PenaltyOwnership`; y dos tipos de multa: `OperatorPenaltyPassThrough` y cargo propio de agencia (agency-owned). El motor de ND existe en facturación general pero NO está conectado al flujo de cancelación de la pata operador.

## Suposiciones

`Suposición:` Gaston hoy factura como **Monotributo (Factura/ND C)**, donde el IVA no se discrimina — esto simplifica TODO el lado IVA hoy. Pero el producto se vende a agencias **RI**, así que el diseño debe contemplar RI aunque no se active hoy.

`Suposición:` el caso central a resolver es **prepago** (la agencia ya pagó al operador). El caso no-prepago se trata aparte donde corresponde.

`Suposición:` "cuadre por moneda" = ledgers ARS y USD separados, nunca se netean entre sí.

---

# SEAM 1 — Multa pass-through vs cargo propio de la agencia

### Decisión recomendada

**a) El operador SIEMPRE reembolsa NETO de su propia multa.** Si retuvo $30 de $100, te devuelve $70. Tu cuenta "me tiene que devolver" nace en $70 (no $100).

**b) La penalidad PROPIA de la agencia NO cambia lo que el operador te debe.** Son dos corrientes distintas: lo que el operador retiene es plata del operador; lo que vos le cobrás al cliente por gestionar la cancelación es ingreso TUYO. Pueden convivir en una misma anulación.

**c) Cómo se factura cada caso (depende del modelo del operador):**

| Caso | Modelo | Cliente | Comprobante | IVA |
|---|---|---|---|---|
| Multa pass-through | **Reseller** (facturaste el total) | La porción no reintegrable ES parte de tu facturación → la re-cobrás | NC total + **ND** por la multa al cliente | RI: cargo de gestión gravado a alícuota general. Monotributo: ND **C**, sin IVA discriminado |
| Multa pass-through | **Intermediario** (facturaste solo comisión) | Esa plata NUNCA pasó por tu factura → la pasás de mano | NC total **sin ND propia**; la ND la hace el mayorista | No corresponde ND propia |
| Cargo propio de agencia | Cualquiera | Le cobrás TU fee de cancelación | **ND propia** (o factura) por el fee | Servicio gravado (RI) / Factura-ND **C** (Mono) |

### Por qué / fuente

NO contradice ni reabre el criterio del matriculado: lo **completa**. El matriculado (2026-06-01) dijo "pass-through del operador = solo NC total, NO ND propia". La regla cerrada del dueño es "NC total + ND por la multa si la hay". **Ambas son verdad en modelos distintos**: la ND por la multa pass-through corresponde en **reseller**, y NO en **intermediario**. La perilla `InvoicingMode` decide qué rama del criterio ya firmado aplica. Fuente base IVA: art. 61 DR IVA + DAT 44/01.

### Riesgo fiscal

`Riesgo fiscal:` si un operador es **intermediario** y el sistema dispara la ND pass-through igual (hoy `PenaltyOwnership` decide SIN mirar `InvoicingMode`), la agencia declara ingreso ajeno como propio → sobre-declaración de IVA/Ganancias. **Bug latente #1.**

`Necesita confirmación profesional:` tratamiento IVA fino de la ND reseller en RI por tipo de producto. Hoy en Monotributo es inocuo (ND C).

---

# SEAM 2 — Cierre sin multa (el operador devuelve todo)

### Decisión recomendada

**La agencia NO emite ningún comprobante fiscal por la pata operador.** Alcanza con: (1) cierre interno explícito "operador confirmó sin multa" que libera el reembolso al 100%, (2) asiento de reclasificación, (3) audit trail. El botón "sin multa" emite **cero CAE / cero ARCA**.

**Ojo con el comprobante del OTRO lado:** cuando el operador anula y reembolsa, **te emite a vos una NC de compra**. Esa NC **la recibís, no la emitís**. Si la agencia es **RI**, debe **registrar esa NC recibida para revertir el IVA crédito fiscal** de la compra original. En Monotributo no hay crédito fiscal → solo reversa interna.

`Riesgo contable (RI):` si no registra la NC de compra del operador, queda crédito fiscal indebido. Hay que modelar "NC de compra recibida del operador". Inocuo en Monotributo.

---

# SEAM 3 — Naturaleza contable de "multa retenida por el operador"

### Decisión recomendada (depende del modelo)

- **Reseller + prepago (caso central):** la multa retenida es un **costo de cancelación** **compensado por el ingreso de la ND** al cliente. Neto en resultados ≈ 0. En el mayor del operador **NO suma como nueva compra**: reduce "me tiene que devolver", no infla "le debo".
- **No-prepago + reseller:** la multa es **deuda nueva** ("le debo $30") y a la vez **costo**; se compensa con la ND.
- **Intermediario:** la multa **NO es costo de la agencia**; es **movimiento de balance** entre contrapartes. No pasa por resultados.
- **Cargo propio de agencia:** es **ingreso** (gravado).

### Asiento sugerido (reseller + prepago; costo 100, multa 30, reembolso 70)

Al confirmar la multa / cierre de la pata operador:
```
Dr  Costo de cancelación (penalidad retenida operador)   30
Dr  Reembolsos a cobrar – Operador (Activo / AR)          70
    Cr  Anticipo a operador / Compra anulada (cancela)        100
```
Al recibir el reembolso:
```
Dr  Caja / Banco                                          70
    Cr  Reembolsos a cobrar – Operador                        70   → operador en CERO
```
Lado cliente (separado, ya decidido): NC total revierte 100, ND re-factura 30 → el costo 30 se compensa con el ingreso 30 → la agencia queda **entera** (neto ≈ 0). **Sin ND en reseller, esos 30 son pérdida pura.**

`Necesita confirmación profesional:` cuenta exacta + deducibilidad en Ganancias. El asiento es la intuición correcta y construible.

---

# SEAM 4 — Reembolso en moneda distinta a la del pago

### Decisión recomendada (la REGLA, sin inventar TC)

1. **Cuadre por moneda.** "Me tiene que devolver" nace en la **moneda del pago original**, valuada al **TC snapshot del día del hecho**.
2. Cuando entra plata en otra moneda, se aplica al crédito al **TC del día del cobro**.
3. La diferencia es **DIFERENCIA DE CAMBIO** → va a **resultados** (resultado financiero realizado). **La absorbe la agencia**, no se re-factura al cliente y **no genera comprobante ARCA**.
4. Residuo por moneda: se cierra como diferencia de cambio, con opción de marcarlo "a reclamar".
5. **Snapshot obligatorio:** persistir el TC de cada pata (pago, multa, reembolso). **Nunca** recalcular en vivo.

`No verificado:` **qué TC exacto** (BNA billete/divisa, BCRA A3500). No invento cotización. La REGLA es "diferencia de cambio a resultados al TC del día de cada pata"; la cotización a adoptar como política la firma el matriculado. Diseño: **fuente de TC configurable y consistente**, persistida en cada evento.

---

# SEAM 5 — Multi-operador (hoy una multa única a nivel reserva-padre)

### Decisión recomendada

**NO es aceptable** una multa agregada a nivel reserva-padre. La multa va **por operador / por línea**, posteada a la cuenta de ESE operador y en SU moneda. El total a nivel reserva es solo **roll-up de visualización**, nunca fuente de verdad.

Razones: cada operador tiene su cuenta corriente; pueden estar en monedas distintas; cada uno emite su propia NC de compra; en RI la reversa de IVA crédito es por factura de cada proveedor; la ND pass-through concilia por operador.

`Riesgo fiscal:` multa agregada → mayores de operador que no concilian y reversa de IVA por proveedor inexacta (RI). Recomendación técnica: desagregar desde el inicio.

---

# Qué perilla debe "variar por operador"

### `InvoicingMode` (reseller vs intermediario) → **POR OPERADOR**. Recomendado fuerte.

Una agencia minorista real opera **los dos modelos en simultáneo**, según el mayorista. El modelo es de **la relación con cada operador**, no de la agencia.

**Ejemplo concreto:**
- Mayorista de paquetes (Ola / Julia Tours / Eurovips): la agencia **compra y revende**, factura el **total** → **reseller**. Cancela y retiene 30% → **pass-through con ND**.
- Asistencia al viajero (Assist Card / Universal) o aéreo BSP: la agencia es **intermediaria**, cobra **comisión** → **intermediario**. Multa del prestador **NO genera ND propia**.
- La **misma agencia, la misma semana**: reseller con uno, intermediario con otro.

### `PenaltyOwnership` (pass-through vs agency-owned) → **POR EVENTO/LÍNEA**, con default por operador.

En una misma anulación pueden convivir la multa que retiene el operador (pass-through) y un fee propio de la agencia (agency-owned).

**Regla de cableado correcta:** el disparo de la ND pass-through al cliente debe mirar **`InvoicingMode` Y `PenaltyOwnership` juntos**. Hoy lo decide `PenaltyOwnership` solo → bug latente.

| InvoicingMode | Penalidad | ¿ND al cliente? |
|---|---|---|
| Reseller | Pass-through operador | **SÍ** |
| Reseller | Agency-owned (fee propio) | **SÍ** |
| Intermediario | Pass-through operador | **NO** |
| Intermediario | Agency-owned (fee propio) | **SÍ** |

---

## Tabla de decisiones tomadas

| # | Seam | Decisión | Firma matriculado |
|---|---|---|---|
| 1 | Pass-through vs propio | Operador reembolsa **neto** de su multa; penalidad propia **no** reduce ese reembolso | IVA fino ND reseller RI |
| 2 | Cierre sin multa | Agencia **no emite** nada; registra NC de compra recibida (reversa IVA crédito si RI) | Reversa IVA crédito (RI) |
| 3 | Naturaleza de la multa | Reseller prepago = **costo** compensado por ND (neto≈0); reduce "me tiene que devolver", no infla "le debo" | Cuenta exacta + deducibilidad |
| 4 | Reembolso otra moneda | Crédito en moneda del pago; diferencia = **diferencia de cambio a resultados**, sin comprobante; snapshot TC por pata | Qué TC + Ganancias |
| 5 | Multi-operador | Multa **por operador/línea**, en su moneda; total = solo roll-up | Estimado agregado temporal |
| — | Perillas | `InvoicingMode` **por operador**; `PenaltyOwnership` **por evento** con default por operador; ND mira **ambas** | — |

## Riesgos fiscales asumidos (informados a Gaston)

1. **Bug latente #1 (el más serio):** hoy la ND pass-through se dispara mirando solo `PenaltyOwnership`. En un operador intermediario eso declara ingreso ajeno como propio. Hasta que `InvoicingMode` esté vivo y cableado, **todo operador se trata como reseller por defecto** (= comportamiento actual, no cambia nada hoy) y los intermediarios reales quedan a revisión manual.
2. **IVA fino de la ND reseller + RI** no cerrado por producto. **Hoy inocuo** (Monotributo, ND C). Real al vender a RI.
3. **Reversa de IVA crédito fiscal** de la compra anulada (RI). Inocuo en Monotributo.
4. **Diferencia de cambio:** la REGLA está; qué cotización oficial se usa es política a firmar.
5. **Multi-operador agregado:** si se postea al padre, los mayores de operador no concilian.
6. **IIBB sobre la multa re-facturada (ND reseller):** provincial. `Necesita confirmación: jurisdicción`.
7. **Deducibilidad de la multa en Ganancias:** el asiento es correcto en intuición; cuenta exacta y deducibilidad las firma el matriculado.

## No verificado

`No verificado:` artículo literal de la RG que fija "TC de NC/ND = TC del comprobante asociado" (está en el Manual del Desarrollador ARCA).
`No verificado:` qué tipo de cambio oficial específico corresponde a la diferencia de cambio.

## Fuentes

- Dictamen DAT 44/01 — Base imponible, servicios de turismo (trivia.consejo.org.ar/ficha/18003)
- Dictamen DAT 8/99 — IVA agencias de turismo (trivia.consejo.org.ar/ficha/17800)
- Ley de IVA — texto actualizado (Infoleg, anexos 40000-44999/42701/texact.htm)
- Acta AFIP — Espacio de Diálogo Entidades de Turismo (afip.gob.ar)
