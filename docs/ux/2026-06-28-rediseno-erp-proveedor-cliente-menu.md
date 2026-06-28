# Rediseño tipo ERP — Ficha del operador, alta de operador, cuenta del cliente, filtros de facturación y menú principal

> **Qué es esto:** especificación de diseño (sin código) para los 5 frentes que pidió Gastón.
> La arma el agente `ux-ui-disenador` SOLO a partir de `docs/ux/guia-ux-gaston.md`.
> Lo que la guía ya cubre se aplica y se cita; lo que NO cubre va al bloque **PREGUNTAS PARA GASTON** del final.
> Fecha: 2026-06-28.

---

## Decisiones de producto que YA tomó Gastón (no se re-preguntan, se diseña a esto)

1. **Cuenta del cliente:** se saca la solapa "Pagos" suelta y los cobros pasan a vivir DENTRO de "Estado de cuenta" como renglones de abono del extracto (tocar un renglón → ver el recibo). Quedan 3 solapas limpias: Reservas · Estado de cuenta · Facturación.
2. **Renombre:** la solapa "Facturación AFIP" pasa a llamarse **"Facturación"**. AFIP/ARCA aparece solo como estado de cada comprobante, nunca en el nombre de la solapa. La lista de Facturación SÍ lleva filtros (fechas, tipo de comprobante, estado, moneda, número).
3. **Ficha del operador:** se parte la página apilada de hoy en **un encabezado (identidad + saldo en vivo) + solapas**, igual que la ficha de la reserva.
4. **Alta de operador:** CUIT + condición fiscal obligatorios para guardar, pero con un **escape "datos fiscales pendientes"** que deja guardar igual; el freno duro es al primer pago, no al alta. Las cuentas bancarias NO se cargan en el alta (se agregan después, en su solapa).
5. **Menú principal:** se reagrupa por MÓDULO (arriba van dominios), y las bandejas/acciones sueltas se mudan adentro de su módulo.

Todo esto se apoya en reglas ya escritas de la guía que se citan más abajo.

## Respuestas confirmadas por Gastón (2026-06-28) — cierra el gate UX

Gastón respondió las decisiones que mueven el producto (las demás quedaron con la opción recomendada, puede corregirlas al verlas):
- **Menú (P15):** árbol aprobado tal cual.
- **Estilo del menú (P16):** **módulos que se abren y cierran (colapsables)** — opción B. (Por eso el Sidebar se construye colapsable; esto SÍ está aprobado por el dueño.)
- **Solapas del operador (P1):** **5 solapas separadas** — opción A.
- **Facturación global (P14):** **sí**, además una pantalla general de Facturación en el menú (dentro de Ventas).
- **Cuenta del cliente:** se fusiona "Pagos" en Estado de cuenta (3 solapas).
- **Alta de proveedor:** se abre dentro de la página (no ventana); CUIT + condición obligatorios con escape "datos fiscales pendientes".
- **Facturación:** renombrar sin "AFIP" + filtros; período default 90 días.

---

# 1) Ficha del operador (proveedor) — encabezado + solapas

## Reglas de la guía que ya mandan acá
- **(2026-06-27)** La cuenta del operador se rehace como **EXTRACTO tipo libro mayor/banco**: franja de saldo POR MONEDA arriba ("Le debo en $" / "Le debo en US$", nunca sumados) + una sola línea de tiempo cronológica con saldo corriente. Las 5 tarjetas que mezclaban monedas DESAPARECEN. El pago al operador pasa a EN LÍNEA (debajo, dentro de la página), reemplaza la ventana flotante.
- **(2026-06-27)** Se mantiene: enmascarado de costos sin permiso (`cobranzas.see_cost` → "—" / "Sin permiso", nunca verde) y la sección "Deuda por expediente".
- **(2026-06-09 P8 / 2026-06-10)** Dos saldos por moneda + una sola lista con la moneda en cada fila.
- **(2026-06-28)** Las cuentas bancarias del operador se administran EN LÍNEA (lista + ficha que se abre debajo), nunca en ventana.
- **(2026-06-21 ADR-036 P4=B)** El estado pagado/impago al operador es etiqueta de estado sin montos.

## Layout propuesto (ASCII)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ←   DESPEGAR ARGENTINA S.A.        CUIT 30-12345678-9 · Resp. Inscripto    │  ← ENCABEZADO
│     Operador                                                               │     (identidad)
│                                                                            │
│     ┌─ Le debo en $ ──────┐  ┌─ Le debo en US$ ────┐  ┌─ A favor ───────┐  │  ← chips de saldo
│     │  $ 1.250.000        │  │  US$ 3.400          │  │ US$ 200         │  │     EN VIVO por
│     └─────────────────────┘  └─────────────────────┘  └─────────────────┘  │     moneda
├──────────────────────────────────────────────────────────────────────────┤
│  Cuenta corriente │ Servicios comprados │ Reembolsos │ Datos bancarios │ Datos │ ← SOLAPAS
├──────────────────────────────────────────────────────────────────────────┤
│  (contenido de la solapa elegida)                                          │
│                                                                            │
│  ── Cuenta corriente (la que abre primero) ──────────────────────────     │
│   [ Registrar pago ]   [ Usar saldo a favor ]   ← acciones; abren ficha    │
│                                          en línea DEBAJO (sin ventana)     │
│                                                                            │
│   EXTRACTO — Pesos ($)                                  Saldo              │
│   12/06  Compra · Hotel Maitei (R-1042)      + 350.000   $ 1.250.000       │
│   10/06  Pago realizado                      − 200.000   $   900.000       │
│   …                                                                        │
│                                                                            │
│   EXTRACTO — Dólares (US$)                              Saldo              │
│   08/06  Compra · Aéreo (R-1050)             + 3.400     US$ 3.400         │
│   …                                                                        │
│                                                                            │
│   ── Deuda por expediente ──  (se mantiene tal cual, ya cumple multimoneda)│
└──────────────────────────────────────────────────────────────────────────┘
```

## Solapas propuestas (orden) y qué va en cada una

| # | Solapa (nombre propuesto) | Qué contiene | De dónde sale hoy |
|---|---|---|---|
| 1 | **Cuenta corriente** (abre primero) | Acciones "Registrar pago" / "Usar saldo a favor" (fichas en línea) + Extracto por moneda + Deuda por expediente | `SupplierExtractoSection` + `PagarProveedorInline` + `UsarSaldoOperadorInline` + `SupplierDebtByReservaSection` |
| 2 | **Servicios comprados** | Grilla operativa: tipo, descripción, reserva, fecha, estado, código, costo/venta | grid del fondo de la página actual |
| 3 | **Reembolsos** | Reembolsos a cobrar de este operador (anulaciones) | `OperatorRefundsPendingSection` |
| 4 | **Datos bancarios** | Lista de cuentas (CBU/alias tapados) + ficha en línea para agregar/editar | `ListaCuentasBancarias` |
| 5 | **Datos** | Identidad editable: razón social, CUIT, condición fiscal, contacto, teléfono, email, dirección, activo/inactivo. Reemplaza la ventana de "Editar proveedor" | `SupplierFormModal` (deja de ser ventana) |

**Encabezado (siempre visible, arriba de las solapas):** nombre + tipo + CUIT + condición fiscal, y los **chips de saldo en vivo por moneda** (Le debo en $ / Le debo en US$ / A favor). Los montos van tapados sin permiso de costos (regla 2026-06-27).

> Lo que está en duda (orden exacto, nombres, qué solapa abre, si "Servicios comprados" y "Reembolsos" son solapa propia o van adentro de Cuenta corriente, y si el saldo a favor además va como chip de encabezado) → **P1 a P6**.

---

# 2) Alta de operador (proveedor)

## Reglas de la guía que ya mandan acá
- **(2026-06-05)** Solo lo imprescindible a la vista; lo secundario detrás de **"Más detalles"**. NADA de "(opcional)" repartido; los obligatorios se marcan con asterisco.
- **(2026-06-05, Ronda 7)** La obligatoriedad la define Gastón, no el código viejo.
- **(2026-06-28)** Las cuentas bancarias se cargan DESPUÉS, en su solapa (no en el alta).

## Decisión de producto de Gastón a respetar
Obligatorios para guardar: **razón social + moneda por defecto + CUIT + condición fiscal**, con un interruptor **"datos fiscales pendientes"** que, prendido, deja guardar sin CUIT/condición. El freno duro es al primer pago.

## Layout propuesto (ASCII)

```
┌──────────────────────────────────────────────────────────────┐
│  Nuevo operador                                              │
│                                                              │
│  Razón social *                                              │
│  [ Despegar Argentina S.A.________________________ ]         │
│                                                              │
│  Moneda por defecto *            CUIT *                      │
│  [ Pesos ($)        ▾ ]          [ 30-12345678-9____ ]       │
│                                                              │
│  Condición fiscal *                                          │
│  [ Resp. Inscripto  ▾ ]                                      │
│                                                              │
│  [ ] Datos fiscales pendientes — los completo más adelante   │
│      (al tildar, CUIT y condición dejan de ser obligatorios; │
│       no se le puede pagar hasta completarlos)               │
│                                                              │
│  ▸ Más detalles  (cerrado por defecto)                       │
│     Contacto · Teléfono · Email · Dirección · Notas          │
│                                                              │
│                            [ Cancelar ]   [ Crear operador ] │
└──────────────────────────────────────────────────────────────┘
```

## Campos (orden)
**A la vista:** Razón social\* · Moneda por defecto\* · CUIT\* · Condición fiscal\* · interruptor "Datos fiscales pendientes".
**En "Más detalles" (cerrado):** Contacto · Teléfono · Email · Dirección · Notas. (Plazo de pago / cuenta contable por defecto: solo si Gastón los quiere — hoy no existen en el form; no se agregan sin que él lo pida, regla 2026-06-05 Ronda 7.)
**No va en el alta:** cuentas bancarias (se cargan en su solapa después — 2026-06-28).

**Comportamiento del escape:** con "Datos fiscales pendientes" tildado, los campos CUIT y Condición fiscal quedan habilitados pero no frenan el "Crear operador". El operador queda marcado como "fiscalmente incompleto"; al intentar el primer pago, el sistema avisa que hay que completar esos datos (la regla de freno al pago es de backend/dominio, no de esta pantalla).

> En duda: si esta ficha sigue siendo ventana o pasa a pantalla/en línea (la guía tiene una regla dura contra ventanas, pero nunca habló de ESTE form), el texto exacto del interruptor, y si la moneda arranca en pesos → **P7 a P9**.

---

# 3) Cuenta del cliente — 3 solapas

## Reglas de la guía que ya mandan acá
- **(2026-06-22 §3)** "Estado de cuenta" = resumen tipo extracto bancario: una sola línea de tiempo donde factura/ND SUMAN (cargo) y cobro/NC RESTAN (abono), con saldo corriente por moneda. Saldo a favor del cliente + enlace a su cuenta corriente. Para quien ve costos, además margen.
- **(2026-06-09 P5 / 2026-06-10)** Dos saldos arriba ("Debe en $/US$") + una sola lista con la moneda en cada fila.
- **(2026-06-10)** Palabra "cobro" unificada.
- Cobros plegados dentro del extracto como abonos (decisión de producto de Gastón de esta tanda).

## Layout propuesto (ASCII)

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ←   Cuenta corriente — FAM. GARCÍA                                         │
│     juan@garcia.com · 11 5555-5555 · DNI 30.111.222                        │
│                                                                            │
│  ┌ Ventas ─┐ ┌ Cobrado ┐ ┌ Reservas ┐ ┌ Facturas ┐   ┌ Debe en $ ───────┐ │  ← resumen
│  │ $ 2,1M  │ │ $ 1,8M  │ │    4     │ │    3     │   │  $ 300.000        │ │     (encabezado)
│  └─────────┘ └─────────┘ └──────────┘ └──────────┘   └───────────────────┘ │
│                                                                            │
│  ┌ A FAVOR EN US$  US$ 200 ┐  [ Usar saldo a favor ]                       │  ← cartel a favor
├──────────────────────────────────────────────────────────────────────────┤
│   Reservas  │  Estado de cuenta  │  Facturación                            │  ← 3 SOLAPAS
├──────────────────────────────────────────────────────────────────────────┤      (default:
│   (Estado de cuenta = extracto con saldo corriente; los cobros van         │     Estado de
│    como renglones de abono; tocar un cobro → ver el recibo)                │     cuenta)
│                                                                            │
│   EXTRACTO — Pesos ($)                                          Saldo      │
│   12/06  Factura B 0001-00012345          + 500.000           $ 500.000    │
│   13/06  Cobro · Transferencia            − 200.000           $ 300.000  ▸ │ ← ▸ ver recibo
│   …                                                                        │
└──────────────────────────────────────────────────────────────────────────┘
```

## Solapas (orden) — 3, default "Estado de cuenta"

| # | Solapa | Qué contiene |
|---|---|---|
| 1 | **Reservas** | Lista operativa de reservas del cliente (igual que hoy) |
| 2 | **Estado de cuenta** (abre primero) | Extracto con saldo corriente por moneda; cobros plegados como abonos (tocar → recibo); saldo a favor + enlace; margen para quien ve costos |
| 3 | **Facturación** | Lista de comprobantes con filtros (ver punto 4). Antes "Facturación AFIP" |

**Encabezado (arriba de las solapas):** identidad + tarjetas de resumen (Ventas/Cobrado/Reservas/Facturas) + saldo "Debe en $/US$" + cartel "A FAVOR" con botón "Usar saldo a favor" + (en duda) datos bancarios.

> En duda: nombre de la solapa del dinero ("Estado de cuenta" vs "Cuenta corriente"), si los datos bancarios del cliente son una 4ta solapa o quedan en el encabezado, y si las tarjetas de resumen de arriba se quedan fijas o entran dentro de la solapa → **P10 a P12**.

---

# 4) Facturación — barra de filtros

## Decisión de producto de Gastón a respetar
La lista de Facturación lleva: **rango de fechas · tipo de comprobante (Factura / NC / ND + letra A/B/C) · estado (emitida / pagada / pendiente / anulada + estado fiscal) · moneda · texto libre por número.**

## Layout propuesto (ASCII)

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Facturación                                          3 comprobantes       │
│                                                                            │
│  Desde [01/06/2026]  Hasta [30/06/2026]   Tipo [ Todos ▾ ]                 │
│  Estado [ Todos ▾ ]   Moneda [ Todas ▾ ]   🔎 [ Buscar por número____ ]    │
├──────────────────────────────────────────────────────────────────────────┤
│  Fecha    Comprobante        Tipo        Importe      Estado     Acción    │
│  12/06    0001-00012345      Factura B   $ 500.000    Aprobado   [ Ver ]   │
│  10/06    0001-00000087      NC A        $ 120.000    Aprobado   [ Ver ]   │
│  …                                                                         │
└──────────────────────────────────────────────────────────────────────────┘
```

## Filtros (orden) y opciones
1. **Desde / Hasta** (rango de fechas).
2. **Tipo:** Todos · Factura A · Factura B · Factura C · Nota de crédito A/B/C · Nota de débito A/B/C.
3. **Estado:** Todos · Emitida · Pagada · Pendiente · Anulada · (estado fiscal: Aprobado / Rechazado / En proceso / Anulando).
4. **Moneda:** Todas · Pesos ($) · Dólares (US$). (El filtro de moneda sigue la regla dura: nunca mezclar; cada comprobante muestra su moneda.)
5. **Buscar por número** (texto libre).

Cada comprobante mantiene su estado fiscal como chip (Aprobado/Rechazado/En proceso/Anulando), como hoy. La acción "Ver" abre el PDF; donde aplique, "Enviar al cliente" (regla 2026-06-24 H2 P5).

> En duda: qué período muestra por defecto y si esta misma barra de filtros vale también para una pantalla global de Facturación (módulo), no solo la del cliente → **P13 a P14**.

---

# 5) Menú principal — árbol por módulos

## Estado en la guía
La sección "Navegación" de la guía está **vacía (_pendiente_)**: NO hay ninguna regla escrita de menú. Por eso TODO el árbol se propone acá y se manda a aprobar. La decisión de producto de Gastón ("reagrupar por módulo, mudar las bandejas sueltas adentro") y el respaldo del experto ERP son la base de esta propuesta.

## Árbol propuesto (para aprobar)

```
🏠  Inicio (Dashboard)
✉️  Mensajes

VENTAS  (Cuentas por Cobrar)
   👤  Clientes
   🧲  Posibles clientes (CRM)
   💳  Cobranza y Facturación
   📄  Facturación            ← módulo de comprobantes (filtros del punto 4)
   📑  NC por revisar
   🔧  Reconciliación NC

COMPRAS  (Cuentas por Pagar)
   🏢  Operadores
   💰  Reembolsos operador

CAJA Y BANCOS
   ↔️  Caja
   🏦  Cuentas bancarias (agencia)   ← (depende de la tanda 2026-06-28)

RESERVAS
   📁  Reservas

CATÁLOGO
   💲  Tarifario
   📦  Países y destinos

GESTIÓN
   ✅  Aprobaciones
   📥  Mis solicitudes
   📈  Comisiones      (solo dueño/admin)
   📊  Reportes
   🛡️  Administración
   ⚙️  Configuración
```

## Qué se mudó respecto del menú de hoy (todo plano)
| Hoy (suelto arriba) | Pasa a |
|---|---|
| Reembolsos operador | dentro de **Compras** |
| NC por revisar | dentro de **Ventas** |
| Reconciliación NC | dentro de **Ventas** |
| Aprobaciones · Mis solicitudes | dentro de **Gestión** |
| Cobranza y Facturación, Clientes, CRM | dentro de **Ventas** |
| Proveedores → renombrado **Operadores** | dentro de **Compras** |
| Caja | dentro de **Caja y Bancos** |
| Tarifario, Países y destinos | dentro de **Catálogo** |
| Comisiones, Reportes, Administración, Configuración | dentro de **Gestión** |
| Dashboard, Mensajes | quedan sueltos arriba (acceso directo) |

**Importante:** los permisos de cada ítem NO cambian (cada link mantiene su `requiredPermission` / `adminOnly` de hoy). Reagrupar es solo visual; un módulo cuyos ítems el usuario no puede ver, no se muestra.

> En duda: si el árbol se aprueba tal cual o se mueve algo; cómo se ve la agrupación (títulos de sección siempre a la vista vs módulos que se abren/cierran con un click); si "Aprobaciones/Mis solicitudes" quedan como módulo "Gestión" o en otro lado; y dónde van CRM y Mensajes → **P15 a P18**.

---

# PREGUNTAS PARA GASTON

### Tema: Ficha del operador (la pantalla del proveedor con solapas)
Contexto: hoy es una sola página larga apilada; la partimos en encabezado + solapas, como la ficha de la reserva. Hay que confirmar cuántas solapas, su orden y sus nombres.

**P1. ¿Estas son las solapas del operador y en este orden?**
  A) Cuenta corriente · Servicios comprados · Reembolsos · Datos bancarios · Datos (5 solapas). (recomendada)
```
   [ Cuenta corriente ] Servicios comprados   Reembolsos   Datos bancarios   Datos
```
  B) Menos solapas: Cuenta corriente (con servicios y reembolsos adentro) · Datos bancarios · Datos (3 solapas).
```
   [ Cuenta corriente ]   Datos bancarios   Datos
   (servicios comprados y reembolsos viven adentro de Cuenta corriente, scrolleando)
```
  C) Otra combinación (contame cuáles y en qué orden).

**P2. ¿Qué solapa se abre primero al entrar al operador?**
  A) **Cuenta corriente** (lo que más se mira: cuánto le debo y el extracto). (recomendada)
  B) Servicios comprados (lo operativo primero).
  C) Otra (contame).

**P3. "Servicios comprados" (la lista de lo que le compraste a ese operador): ¿solapa propia o adentro de Cuenta corriente?**
  A) **Solapa propia** "Servicios comprados". (recomendada — es una lista grande, se busca sola)
```
   Cuenta corriente │ [ Servicios comprados ] │ Reembolsos │ …
```
  B) Adentro de Cuenta corriente, más abajo (scrolleando).

**P4. "Reembolsos" (la plata que el operador te tiene que devolver por anulaciones): ¿solapa propia o adentro de Cuenta corriente?**
  A) **Solapa propia** "Reembolsos", con un numerito si hay pendientes. (recomendada)
```
   … │ [ Reembolsos (2) ] │ Datos bancarios │ …
```
  B) Adentro de Cuenta corriente, más abajo.

**P5. El "saldo a favor" del operador (cuando le pagaste de más): además de mostrarse en el extracto, ¿querés un chip en el encabezado de arriba?**
  A) **Sí**, un chip verde "A favor" arriba, al lado de "Le debo en $/US$". (recomendada — se ve de un vistazo)
```
   [ Le debo en $ 1.250.000 ]  [ Le debo en US$ 3.400 ]  [ A favor US$ 200 ]
```
  B) No, solo dentro del extracto (más sobrio).

**P6. Nombres de las solapas: ¿te quedan bien estos textos?**
  A) **Cuenta corriente · Servicios comprados · Reembolsos · Datos bancarios · Datos** (recomendada)
  B) Cambiar alguno (ej. "Datos" → "Ficha"; "Cuenta corriente" → "Extracto"; "Reembolsos" → "Devoluciones"). Decime cuál.

---

### Tema: Alta de un operador nuevo (cargar un proveedor)
Contexto: hoy el alta es una ventana que se abre encima, con Razón Social / CUIT / Condición / Contacto. Le sumamos "Moneda por defecto" y el escape "datos fiscales pendientes". Y hay una regla tuya fuerte de que las ventanas que se abren encima no te gustan.

**P7. El alta del operador, ¿sigue siendo una ventana que se abre encima, o la pasamos a pantalla/abrirse dentro de la página (como todo lo demás)?**
  A) **Que se abra DENTRO de la página** (en línea / pantalla propia), sin ventana encima — coherente con "el modal me parece horrible". (recomendada)
```
   Operadores
   [ + Nuevo operador ]
   ┌─ se abre acá abajo, dentro de la página ─────────────┐
   │ Razón social * …                                     │
   └──────────────────────────────────────────────────────┘
```
  B) Dejarla como ventana que se abre encima (como hoy).
  C) Otra (contame).

**P8. El interruptor para guardar sin los datos fiscales: ¿qué texto te gusta?**
  A) **"Datos fiscales pendientes — los completo más adelante"** (debajo, un textito gris: "no se le puede pagar hasta completarlos"). (recomendada)
  B) "Guardar sin CUIT por ahora".
  C) Otro texto (escribímelo).

**P9. La "Moneda por defecto" del operador, ¿con qué arranca elegida?**
  A) **Pesos ($)** por defecto, se puede cambiar a dólares. (recomendada)
  B) Sin nada elegido, obliga a elegir.
  C) Otra (contame).

---

### Tema: Cuenta del cliente (las solapas de adentro)
Contexto: sacamos la solapa "Pagos" suelta; los cobros pasan a verse dentro de "Estado de cuenta" (el extracto). Quedan 3 solapas: Reservas · Estado de cuenta · Facturación. El "Estado de cuenta" abre primero.

**P10. La solapa del dinero, ¿la llamamos "Estado de cuenta" o "Cuenta corriente"?**
  A) **"Estado de cuenta"** (es como la venís nombrando). (recomendada)
  B) "Cuenta corriente".
  C) Otro nombre (contame).

**P11. Los datos bancarios del CLIENTE (su CBU/alias, ej. para devolverle plata): ¿una 4ta solapa o quedan arriba en el encabezado?**
  A) **4ta solapa "Datos bancarios"** (queda prolijo, separado de la plata). (recomendada)
```
   Reservas │ Estado de cuenta │ Facturación │ [ Datos bancarios ]
```
  B) Arriba, en el encabezado, debajo del saldo (como hoy).
  C) Otra (contame).

**P12. Las tarjetas de resumen de arriba (Ventas / Cobrado / Reservas / Facturas / Saldo): ¿se quedan fijas arriba o entran dentro de "Estado de cuenta"?**
  A) **Se quedan fijas arriba** (encabezado), se ven siempre en cualquier solapa. (recomendada)
  B) Entran dentro de "Estado de cuenta" (encabezado más limpio, pero las ves solo en esa solapa).

---

### Tema: Filtros de la lista de Facturación
Contexto: ya pediste estos filtros (fechas, tipo, estado, moneda, número). Falta afinar el período por defecto y si sirven también para una pantalla general de facturas.

**P13. Cuando entrás a Facturación, ¿qué período te muestra de arranque?**
  A) **Los últimos 90 días**, con la opción de poner "todo" o cambiar las fechas. (recomendada — no se cuelga con años de comprobantes)
  B) El mes actual.
  C) Todo, desde el principio.
  D) Otra (contame).

**P14. ¿Querés además una pantalla GENERAL de Facturación (todos los comprobantes de la agencia, no solo los de un cliente) con esta misma barra de filtros?**
  A) **Sí**, una pantalla de Facturación en el menú con los mismos filtros. (recomendada — es lo de un ERP)
  B) No por ahora; solo la solapa dentro de cada cliente.
  C) Otra (contame).

---

### Tema: Menú principal (cómo se agrupa todo a la izquierda)
Contexto: hoy el menú es una lista plana larga con bandejas sueltas mezcladas (Reembolsos operador, NC por revisar, Reconciliación NC, Aprobaciones, Mis solicitudes). Te propongo agruparlo por módulos. Esto se aprueba ANTES de tocar nada.

**P15. ¿Te cierra este árbol de módulos? (mirá el detalle arriba en el punto 5)**
```
  Inicio · Mensajes
  VENTAS:   Clientes · Posibles clientes · Cobranza y Facturación · Facturación · NC por revisar · Reconciliación NC
  COMPRAS:  Operadores · Reembolsos operador
  CAJA Y BANCOS:  Caja · Cuentas bancarias (agencia)
  RESERVAS: Reservas
  CATÁLOGO: Tarifario · Países y destinos
  GESTIÓN:  Aprobaciones · Mis solicitudes · Comisiones · Reportes · Administración · Configuración
```
  A) **Sí, tal cual.** (recomendada)
  B) Sí pero muevo algo (decime qué ítem va a qué módulo).
  C) No, lo armamos distinto (contame).

**P16. ¿Cómo se ven los módulos en el menú?**
  A) **Títulos de sección siempre a la vista** (un rótulo gris "VENTAS" y debajo sus ítems, todos visibles, sin tener que abrir nada). (recomendada — nada se esconde)
```
   VENTAS
     Clientes
     Posibles clientes
     Cobranza y Facturación
   COMPRAS
     Operadores
     …
```
  B) **Módulos que se abren y cierran** (tocás "VENTAS" y se despliega; arranca cerrado para ocupar menos).
```
   ▸ VENTAS
   ▸ COMPRAS
   ▾ CAJA Y BANCOS
       Caja
       Cuentas bancarias
```
  C) Otra (contame).

**P17. "Reservas", ¿dónde te gusta más?**
  A) **Módulo propio "RESERVAS"** arriba de todo (es el corazón del día a día). (recomendada)
  B) Suelto arriba, sin título de módulo, bien a mano.
  C) Adentro de "Ventas".

**P18. "Posibles clientes (CRM)" y "Mensajes", ¿dónde van?**
  A) **CRM dentro de Ventas; Mensajes suelto arriba** (a mano, se usa todo el tiempo). (recomendada)
  B) Los dos sueltos arriba.
  C) Otra (contame).
```

---

## Qué NO hacer (para frontend-senior, una vez aprobado)
- No inventar solapas, nombres ni filtros que no estén en esta spec o en la guía.
- No mezclar pesos y dólares en ningún número (regla dura multimoneda).
- No mostrar montos de costo/deuda sin permiso `cobranzas.see_cost` (van "—" / "Sin permiso", nunca verde).
- No usar ventanas que se abren encima para fichas de pago, cobro, carga o cuentas bancarias: todo EN LÍNEA.
- No cambiar permisos de los ítems del menú al reagrupar (solo es visual).
- Nada se construye hasta que Gastón responda P1–P18.
```
