# Spec: chip "Anulada" en las solapas de Facturación (reserva y cliente)

> **Fecha:** 2026-08-18 · **Autor:** `ux-ui-disenador` · Fuentes ÚNICAS: `docs/ux/guia-ux-gaston.md`,
> `docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md` (B.1 colores de significado, molde
> `StatusChip`) y `docs/ux/2026-08-16-guia-rollout-estandar-visual.md`.
>
> **Gap que se cierra:** una factura ya ANULADA sigue mostrando el chip verde "Aprobado" (o
> "Rechazado") en la solapa Facturación de la ficha de reserva (`InvoicingTab.jsx`) y en la solapa
> Facturación del cliente (`FacturacionClienteTab.jsx`). El renglón chico con el motivo de anulación
> ("Anulada — Motivo: …") **ya se agregó bien** en la Tanda 3 (2026-08-18) en las dos pantallas — lo
> único que falta es el chip.
>
> **Patrón YA construido y firmado que este documento REUSA tal cual, sin inventar nada nuevo:** la
> función `ChipEstadoFiscal` de `src/TravelWeb/src/features/invoices/pages/FacturacionPage.jsx`
> (pantalla global de Facturación) ya resuelve exactamente este caso: rojo + tachado + texto
> "Anulada" cuando `annulmentStatus === "Succeeded"`, con prioridad sobre el resultado ARCA
> (Aprobado/Rechazado). Ese patrón sigue al pie de la letra reglas ya firmadas de la guía: B.1
> (rojo = freno/sin efecto) y la convención de **tachado = anulado/cancelado** que ya se usa en toda
> la app (servicios cancelados, movimientos anulados, reservas Anuladas). No es una decisión nueva:
> es aplicar la misma regla en dos pantallas que hoy quedaron afuera.
>
> **No se implementa código.** Entrega para `frontend-senior`, con `frontend-reviewer` verificando
> además contra `docs/ux/guia-ux-gaston.md`.

---

## 1) Solapa "Facturación" de la ficha de reserva (`InvoicingTab.jsx` → `InvoiceSection`)

**Campo que ya viaja del backend** (no hay que pedir nada nuevo): `invoice.annulmentStatus`
(`"Pending"` / `"Succeeded"` / `"Failed"` / ausente) y `invoice.annulmentReason` — ya usados hoy por
el renglón `Anulada — Motivo: {...}` que muestra la propia tarjeta.

### Antes (hoy) — el chip queda mal

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Factura A                                        [ ✔ Aprobado ]  ← MAL,  │
│                                                     (verde)  sigue verde  │
│ F-2026-1067 · Juan Pérez                                                  │
│ 18/08/2026 · #00012345                                                    │
│ Anulada — Motivo: Cliente cambió la fecha del viaje                       │
│                                                                             │
│                              [ Ver PDF ]  [ Descargar ]  [ Anular ] ← MAL, │
│                                                    ya está anulada, no debería poder tocarse de nuevo │
└──────────────────────────────────────────────────────────────────────────┘
```

### Después — chip "Anulada" (rojo, tachado), mismo criterio que la pantalla global

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Factura A                                          [ A̶n̶u̶l̶a̶d̶a̶ ]          │
│                                                       (rojo, tachado)     │
│ F-2026-1067 · Juan Pérez                                                  │
│ 18/08/2026 · #00012345                                                    │
│ Anulada — Motivo: Cliente cambió la fecha del viaje                       │
│                                                                             │
│                                        [ Ver PDF ]  [ Descargar ]         │
└──────────────────────────────────────────────────────────────────────────┘
```

**Cambio de código puntual:** en `InvoiceSection` (líneas ~150-162 hoy), el bloque que hoy dice

```jsx
{invoice.annulmentStatus === "Pending" ? (
  <StatusChip tone="ambar" ...>Anulando…</StatusChip>
) : (
  <StatusChip tone={invoice.resultado === "A" ? "verde" : invoice.resultado === "R" ? "rojo" : "azul"}>
    {invoice.resultado === "A" ? "Aprobado" : invoice.resultado === "R" ? "Rechazado" : "En proceso"}
  </StatusChip>
)}
```

pasa a usar la misma función `ChipEstadoFiscal` (o una copia idéntica) que ya existe en
`FacturacionPage.jsx`, que agrega el caso `annulmentStatus === "Succeeded"` → rojo tachado "Anulada",
y `annulmentStatus === "Failed"` → rojo "Error anulación" (caso excepcional que hoy tampoco se
distingue). El chip "Anulando…" (Pending) queda igual — **ojo:** el color correcto según B.1/el
patrón de `FacturacionPage.jsx` es **azul** ("en curso", no le pide nada al usuario), no ámbar como
está hoy en `InvoicingTab.jsx` — se corrige de paso para quedar igual en las tres pantallas.

## 2) Solapa "Facturación" del cliente (`FacturacionClienteTab.jsx` → `ChipEstadoFiscal`)

Mismo gap, mismo arreglo. Esta pantalla además tiene su propio `resolverEstadoFiscal()` en
`facturacionFilters.js`, que hoy solo distingue `anulando` (Pending) pero no
`Succeeded` — se le agrega la clave `anulada` con la misma prioridad que ya usa
`FacturacionPage.jsx` (anulando > anulada > aprobado > rechazado > en_proceso).

### Antes (hoy) — fila de tabla (desktop)

```
 FECHA     COMPROBANTE       TIPO      MONEDA  IMPORTE      ESTADO              ACCIÓN
 ──────────────────────────────────────────────────────────────────────────────────────
 18/08/26  00001-00012345    Factura A  $ ARS   $ 45.000,00  [✔ Aprobado] (verde,  [ Ver ]
                                                               MAL)
                                                               Anulada — Motivo: Cliente
                                                               cambió la fecha del viaje
```

### Después

```
 FECHA     COMPROBANTE       TIPO      MONEDA  IMPORTE      ESTADO              ACCIÓN
 ──────────────────────────────────────────────────────────────────────────────────────
 18/08/26  00001-00012345    Factura A  $ ARS   $ 45.000,00  [A̶n̶u̶l̶a̶d̶a̶] (rojo)      [ Ver ]
                                                               Anulada — Motivo: Cliente
                                                               cambió la fecha del viaje
```

Mismo cambio en la tarjeta mobile (`MobileRecordCard` → `statusSlot`), que ya usa el mismo
`ChipEstadoFiscal`: se corrige solo, sin tocar el layout de la tarjeta.

---

## 3) Qué NO cambia

- **Ningún dato nuevo.** `annulmentStatus` y `annulmentReason` ya viajan del backend desde la Tanda 3
  (2026-08-18); esta spec solo cambia qué chip se pinta con lo que ya llega.
- El renglón `Anulada — Motivo: {...}` **no se toca** — ya está bien en las dos pantallas.
- El botón "Ver PDF"/"Ver"/"Descargar" **sigue disponible** en una factura anulada (hace falta poder
  ver el comprobante histórico y su CAE, aunque ya no tenga efecto).
- La solapa de "Cuentas por pagar" (`SupplierInvoicesSection.jsx`) **no se toca** — ese chip ya
  funciona bien, es distinto (estado propio `pendiente/pago_parcial/pagada/anulada`, no
  `AnnulmentStatus` de ARCA).
- La pantalla global de Facturación (`FacturacionPage.jsx`) **no se toca** — es la fuente del patrón
  que estas dos pantallas copian.
- Los filtros de fecha/cliente/reserva/comprobante/moneda **no cambian**.

---

## PREGUNTAS PARA GASTÓN

Son 2, sobre dos comportamientos chicos que la guía no cubre todavía (el chip en sí ya sale directo
de reglas firmadas — B.1 rojo = freno, tachado = anulado — y no hace falta preguntarlo).

### Tema: el botón "Anular" de una factura que ya está anulada

Contexto: en la solapa Facturación de la reserva, hoy el botón "Anular" sigue apareciendo aunque la
factura YA esté anulada (el chequeo actual solo tapa el botón mientras se está anulando, no después).
Con el chip nuevo se nota más el error, porque al lado va a decir "Anulada" y al lado el botón
"Anular" sigue ahí.

**P1. ¿Qué hacemos con el botón "Anular" cuando la factura YA está anulada?**

```
  A) Se ESCONDE (la recomendada) ✅ — mismo criterio que ya usás en Cuentas por pagar:
     una vez anulada, no se puede repetir la acción.
     [ Ver PDF ]  [ Descargar ]                    ← sin botón "Anular"

  B) Se deja como está — el botón sigue mostrándose siempre, anulada o no.
     [ Ver PDF ]  [ Descargar ]  [ Anular ]         ← como hoy (se puede tocar de nuevo)
```

### Tema: filtro por Estado/Resultado en las dos pantallas

Contexto: la pantalla global de Facturación ya tiene la opción "Anulada" en su filtro de Estado. Las
dos pantallas de esta spec (solapa de la reserva y solapa del cliente) todavía no la tienen — hoy solo
se puede filtrar por Aprobado / Rechazado / En proceso (y "Anulando" en la del cliente).

**P2. ¿Agregamos "Anulada" como opción de filtro en estas dos pantallas, en esta misma tanda?**

```
  A) SÍ, se agrega ahora (la recomendada) ✅ — mismo filtro que ya existe en la pantalla
     global, para poder buscar rápido "todas las anuladas" sin tener que mirar chip por chip.
     Estado: [ Todos ▾ ]
             · Aprobado
             · Rechazado
             · En proceso
             · Anulando
             · Anulada          ← nueva opción

  B) NO por ahora — se deja para otra tanda aparte, esta spec resuelve solo el chip.
```

---

---

## RESPUESTAS DE GASTÓN (18/08 noche — FIRMADAS, multiple choice)

- **P1 = A**: el botón "Anular" SE ESCONDE cuando la factura ya está anulada
  (mismo criterio que Cuentas por pagar: la acción no se repite).
- **P2 = A**: se agrega la opción "Anulada" al filtro de estado de las dos
  solapas (reserva y cliente), en esta misma tanda — espejo del filtro que
  ya tiene la pantalla global de Facturación.

Con esto la spec queda completa y ejecutable sin más preguntas.
