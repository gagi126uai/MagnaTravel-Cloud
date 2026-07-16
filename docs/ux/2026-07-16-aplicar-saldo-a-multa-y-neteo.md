# Aplicar saldo a favor a una multa + neteo en la devolución (Tanda D1)

> **Fecha:** 2026-07-16
> **Pantalla base:** `CustomerAccountPage.jsx` (cuenta corriente del cliente) — se monta SOBRE el
> rediseño del extracto aprobado el mismo día (`docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`).
> **Estado:** APROBADA por Gastón (P1..P6 = todas la opción recomendada, 2026-07-16).
> **Gate UX:** cumplido. `frontend-senior` implementa esta spec al pie de la letra; cualquier
> desvío por costo técnico o regla de negocio se le repregunta a Gastón ANTES de desviarse.
> **Origen:** Tanda D1 aprobada por Gastón: (a) saldar una multa del cliente con su saldo a favor;
> (b) al devolverle plata, descontar primero lo que debe (multa) y devolver la diferencia.

---

## 0. Qué construye esta obra (dos cosas)

1. **Aplicar saldo a favor a una multa (obra a):** un destino nuevo en la ficha "Usar saldo a favor"
   que permite saldar (entera o en parte) una multa por anulación que el cliente tiene pendiente.
2. **Neteo en la devolución (obra b):** cuando se devuelve plata (efectivo o transferencia) a un
   cliente que a la vez debe una multa, el sistema descuenta primero la multa y devuelve la diferencia,
   mostrando la cuenta antes de confirmar.

Ambas viven EN LÍNEA dentro de la ficha `UsarSaldoAFavorInline`, que cuelga del botón
**[Usar saldo a favor]** de la foto de saldo (nunca ventana flotante).

---

## 1. Reglas de negocio del backend (fijas — el front NO las reimplementa)

El front solo pinta lo que el backend calcula. Estas reglas son del servidor:

- **Solo son aplicables las multas con comprobante emitido** (multa firme). Las multas en trámite / en
  revisión (sin comprobante todavía) NO entran en la lista para saldar ni en el neteo.
- **Misma moneda:** el saldo a favor y la multa tienen que ser de la misma moneda. Nunca se mezcla ni
  se convierte (regla dura multimoneda 2026-06-09).
- **Tope de la aplicación = lo que falta cobrar de esa multa.**
- **Drenado de bolsillos en FIFO** (si el crédito viene de varios orígenes, el backend los consume del
  más viejo al más nuevo, invisible al usuario — igual que hoy en "aplicar a otra reserva").
- **La previa del neteo la calcula el servidor** (desglose + neto). El usuario confirma UNA vez.
- **La aplicación es revertible** (deja rastro; al revertir, la plata vuelve a "Crédito a favor" y la
  multa vuelve a "Multas abiertas").
- **El recibo del egreso (la devolución neteada) lleva el desglose.**
- **No se puede "Deshacer" una multa que tiene saldo aplicado:** el sistema pide revertir la
  aplicación primero.

---

## 2. Dónde se dispara cada flujo

- **Único punto de entrada:** el botón **[Usar saldo a favor]** de la foto de saldo (aparece solo si
  hay crédito a favor > 0 y el usuario tiene `cobranzas.edit`). Abre EN LÍNEA la ficha
  `UsarSaldoAFavorInline` debajo de la foto.
- **NO se dispara desde el renglón del extracto:** el extracto es documento de consulta, sin botones de
  acción por renglón (spec extracto 2026-07-16, P6/§3/§6). El renglón de la multa en el extracto sigue
  siendo solo lectura (con su link a la ficha de la reserva).
- La ficha trabaja **por moneda** (prop `moneda`): la lista de multas y el neteo se filtran a esa
  moneda sola.

El desplegable "Destino" de la ficha suma una opción. Orden propuesto:

```
Destino: [ Devolver por transferencia          ▾ ]
           Devolver en efectivo
           Aplicar a una multa           ← NUEVO (obra a)
           Aplicar a otra reserva
           Dejar como crédito (cerrar aviso)
```

---

## 3. Obra (a) — Aplicar saldo a favor a una multa

### 3.1 Elegir la multa (P1 = A: lista, elige el usuario)

Al elegir "Aplicar a una multa" se muestra la lista de multas pendientes del cliente en esa moneda
(solo las firmes/aplicables). El usuario elige cuál saldar (mismo patrón que hoy elige la reserva
destino en "Aplicar a otra reserva").

```
┌ Usar saldo a favor ─────────────────────────────────── ✕ ┐
│ Saldo a usar:  Quedan $ 10.000 de $ 10.000 · ARS          │
│ Destino:       [ Aplicar a una multa            ▾ ]       │
│                                                           │
│ ¿A qué multa?                                             │
│ ┌───────────────────────────────────────────────────┐   │
│ │ ●  R-1050 · Bariloche — Multa por anulación         │  │
│ │       Falta cobrar:  $ 3.000                         │  │
│ │ ○  R-1099 · Cancún — Multa por anulación             │  │
│ │       Falta cobrar:  $ 8.500                         │  │
│ └───────────────────────────────────────────────────┘   │
│                                                           │
│ Monto a aplicar: [ 3.000        ]   (máx. $ 3.000)        │
│                                                           │
│ Se van a aplicar $ 3.000 del saldo a favor a la multa de  │
│ la reserva R-1050.                                        │
│                        [ Cancelar ]   [ Aplicar saldo ]   │
└───────────────────────────────────────────────────────────┘
```

Detalle de cada fila de la lista:
- **Título:** `R-1050 · Bariloche — Multa por anulación` (número de reserva + nombre del file + "Multa
  por anulación"). El número de reserva nunca es un Id/GUID: es el número legible.
- **Debajo:** `Falta cobrar:  $ 3.000` (lo que resta cobrar de esa multa, en la moneda de la ficha).

Monto:
- **Sugerido = el menor entre** lo que falta cobrar de la multa elegida y el saldo a favor disponible.
- Editable hacia abajo (aplicación parcial). `máx.` = ese mismo mínimo.
- Validación: > 0 y ≤ máximo. Mensajes en criollo (reusar `validarAplicacion`).

Línea de resumen (aparece cuando hay multa elegida y monto > 0):
> "Se van a aplicar $ 3.000 del saldo a favor a la multa de la reserva R-1050."

Botón: **[ Aplicar saldo ]**. Un solo click, SIN cartel "¿Seguro?" (la aplicación es revertible).
Anti-doble-click con `disabled` mientras guarda ("Procesando…").

Éxito (toast):
> "El saldo a favor se aplicó a la multa de la reserva R-1050."

La página recarga: baja "Multas abiertas" en la foto y aparece el renglón en el extracto (§5).

### 3.2 Estado: no hay multas que se puedan saldar

Si el cliente no tiene ninguna multa firme (con comprobante emitido) en esa moneda:

```
│ ¿A qué multa?                                             │
│   No hay multas que puedas saldar con saldo a favor.      │
│   (Las multas que todavía no tienen comprobante emitido   │
│    no se pueden saldar hasta que el comprobante salga.)   │
```

Nunca se muestra el término fiscal crudo ni el estado interno (`issuing`/`underReview`/CAE): siempre
en criollo.

---

## 4. Obra (b) — Devolver plata descontando la multa (neteo)

Cuando el destino es **"Devolver por transferencia"** o **"Devolver en efectivo"** y el cliente tiene
una multa firme sin pagar en esa moneda, el servidor manda la **previa** con el desglose. El usuario la
mira y confirma una vez.

### 4.1 Previa del neteo (P3 = A; P4 = A: neto completo, sin teclear monto)

```
┌ Usar saldo a favor ─────────────────────────────────── ✕ ┐
│ Saldo a usar:  Quedan $ 10.000 de $ 10.000 · ARS          │
│ Destino:       [ Devolver por transferencia     ▾ ]       │
│                                                           │
│ Este cliente tiene una multa sin pagar. Antes de          │
│ devolverle, se descuenta lo que debe:                     │
│ ┌───────────────────────────────────────────────────┐   │
│ │   Saldo a favor                       $ 10.000      │  │
│ │ − Multa R-1050 (por anulación)       −$  3.000      │  │
│ │ ─────────────────────────────────────────────      │  │
│ │   Le devolvés                         $  7.000      │  │
│ └───────────────────────────────────────────────────┘   │
│                                                           │
│ Cuenta del cliente para transferir:                       │
│   CBU 0123…4567   [ Copiar ]                              │
│ Referencia (opcional): [ ____________________ ]           │
│                                                           │
│                     [ Cancelar ]   [ Devolver $ 7.000 ]   │
└───────────────────────────────────────────────────────────┘
```

- **El desglose y el neto los arma el servidor** (previa). El front los pinta tal cual; NO recalcula.
- **NO hay casillero de monto** (P4 = A): se devuelve el neto completo de una. El botón lleva el neto:
  **[ Devolver $ 7.000 ]**.
- Para **transferencia** se muestra la cuenta bancaria del cliente con [Copiar] y el campo Referencia
  (opcional), igual que hoy (`RecuadroCuentaBancaria`). Para **efectivo** sigue valiendo el aviso Ley
  25.345 (tope legal; si el neto lo supera, el backend rechaza con 409 y el front muestra el mensaje
  tal cual).
- Si hay **varias** multas firmes en esa moneda, el servidor las descuenta todas (FIFO) y el desglose
  muestra una línea por multa; el neto es el saldo a favor menos la suma. (Ej.: `− Multa R-1050` y
  `− Multa R-1099` como dos renglones del desglose.)

Éxito (toast):
> "Se registró la devolución de $ 7.000. La multa de R-1050 quedó saldada."

El recibo del egreso lleva el desglose (§7).

### 4.2 Estado: el saldo no alcanza para devolver nada

Si el saldo a favor es ≤ que la multa, se usa todo para la multa y no queda nada para devolver:

```
│ ┌───────────────────────────────────────────────────┐   │
│ │   Saldo a favor                        $  2.000     │  │
│ │ − Multa R-1050 (cubre $ 2.000 de $ 3.000) −$ 2.000  │  │
│ │ ─────────────────────────────────────────────      │  │
│ │   Le devolvés                          $      0     │  │
│ └───────────────────────────────────────────────────┘   │
│  Todo el saldo a favor se usa para la multa; no queda     │
│  nada para devolver. Queda $ 1.000 de multa sin pagar.    │
│                          [ Cancelar ]  [ Aplicar a la multa ] │
```

- El botón cambia de "Devolver $0" a **[ Aplicar a la multa ]** (el resultado real es una aplicación,
  no una devolución).
- El texto del renglón de la multa y el "cubre $ X de $ Y" los arma el servidor.

---

## 5. Cómo se ve en el extracto y la foto que la multa quedó saldada (P2 = A)

La aplicación de saldo a favor a una multa **aparece como un renglón Haber** (baja el saldo), igual que
un pago. Es la forma "extracto de verdad": queda el rastro, y al revertir aparece el renglón inverso.

```
  EXTRACTO — Pesos ($)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ 15/06/26  Nota de débito 0002-00003 (multa) · R-1050   20.000    —  100.000 │
  │ 16/06/26  Saldo a favor aplicado · R-1050 (multa)          —   3.000  97.000 │
  └─────────────────────────────────────────────────────────────────────────┘
```

- **Columna Documento:** `Saldo a favor aplicado · R-1050 (multa)`. El número de reserva es link a la
  ficha (igual que los demás renglones). El motivo `(multa)` entre paréntesis, en criollo.
- **Haber** = el monto aplicado (baja el saldo corriente).
- En la **foto de saldo**, la línea "Multas abiertas" baja por ese monto (de $ 20.000 a $ 17.000 en el
  ejemplo). El SALDO de la foto sigue coincidiendo con el cierre del extracto (backend recalcula).
- **Al revertir** la aplicación (§6), el extracto muestra el renglón inverso (Debe) que devuelve el
  saldo al valor previo, y "Multas abiertas" vuelve a subir.
- **El backend arma el renglón** (`Kind`, `Date`, `DocumentRef`/descripción, `RunningBalance`). El
  front solo pinta.

---

## 6. Revertir la aplicación (lista que ya existe)

La lista "Saldo a favor aplicado a otras reservas" pasa a mostrar los dos casos (a otra reserva y a una
multa). Renombrar el título a **"Saldo a favor aplicado"** (sirve para ambos).

```
  SALDO A FAVOR APLICADO
  ┌───────────────────────────────────────────────────────┐
  │ Saldo a favor aplicado a la reserva R-1042             │
  │   $ 5.000 · 10/06/26                    [ Revertir ]   │
  │───────────────────────────────────────────────────────│
  │ Saldo a favor aplicado a la multa de R-1050           │
  │   $ 3.000 · 16/06/26                    [ Revertir ]   │
  └───────────────────────────────────────────────────────┘
```

- Cada fila linkea a la ficha de la reserva (número legible, nunca Id).
- Revertir pide **motivo en línea** y confirma (igual que hoy). Al revertir: la plata vuelve a "Crédito
  a favor" y la multa vuelve a "Multas abiertas"; el extracto muestra el renglón inverso.
- El backend distingue el tipo de destino en cada `activeApplication` (reserva vs multa). El front pinta
  el texto según el tipo. Si el backend no distingue todavía, coordinar (ver §9).

---

## 7. El recibo de la devolución neteada (P6 = A)

El recibo del egreso lleva el desglose exacto:

```
  Devolución de saldo a favor
    Saldo a favor              $ 10.000
    Menos multa R-1050        −$  3.000
    Total devuelto             $  7.000
```

- Lo arma el backend (el front no genera el recibo). Este es el texto/desglose que debe salir.
- Si hubo varias multas, una línea "Menos multa R-…" por cada una.

---

## 8. Deshacer una multa con saldo aplicado (en la ficha de la reserva, P5 = A)

Cuando alguien intenta "Deshacer" una multa que tiene saldo a favor aplicado, el panel de la ficha
(`OperatorPenaltyStepPanel` / `DeshacerMultaEmitidaInline`) lo frena con este texto exacto:

```
  No se puede deshacer esta multa todavía: tiene $ 3.000 de saldo a favor
  aplicados. Primero revertí esa aplicación desde la cuenta del cliente y
  después deshacé la multa.
                                        [ Ir a la cuenta del cliente ]
```

- El botón **[ Ir a la cuenta del cliente ]** navega a la cuenta corriente del cliente, donde vive la
  lista de aplicaciones revertibles (§6).
- El backend informa si la multa tiene saldo aplicado y cuánto; el front no lo deduce.
- No se muestra jerga fiscal ni internals.

---

## 9. Estados de la pantalla

| Estado | Qué se ve |
|--------|-----------|
| **Cargando la lista de multas / la previa del neteo** | "…" en los números (nunca un "$0" falso que después salte). |
| **Sin multas aplicables** (obra a) | Cartelito de §3.2: "No hay multas que puedas saldar con saldo a favor…". |
| **Saldo insuficiente** (neteo, crédito ≤ multa) | Previa con "Le devolvés $ 0" + "Queda $ X de multa sin pagar"; el botón pasa a **[ Aplicar a la multa ]** (§4.2). |
| **Éxito** | Toast en criollo + recarga de la página (baja "Multas abiertas" y aparece el renglón). |
| **Sin permiso** | Sin `cobranzas.edit` el botón [Usar saldo a favor] no aparece; los montos igual se ven (esta pantalla es del lado cliente, NO hay "—" acá — spec extracto §4). |
| **Error del servidor** | Cartel rojo en criollo (`getApiErrorMessage`), sin internals (nada de CAE / enum / GUID / stack / texto crudo de AFIP); botón reintenta en el mismo lugar; lo cargado NO se pierde. |
| **Previa desactualizada** (el server revalida al confirmar y rechaza: la multa cambió, la pagaron o se deshizo mientras mirabas) | Cartel ámbar: **"La cuenta cambió mientras mirabas esta pantalla. Actualizamos los números; revisá y volvé a confirmar."** + se vuelve a pedir la previa al servidor. Nunca se manda un neto viejo. |
| **Base de datos no disponible** | El componente `DatabaseUnavailableState` de siempre. |

---

## 10. Multimoneda (reglas duras, no se rompen)

1. Saldo a favor y multa **SIEMPRE de la misma moneda**; nunca sumados ni convertidos (ni en la lista,
   ni en el neteo, ni en el extracto).
2. **Nunca** aparece la palabra "diferencia de cambio".
3. La ficha opera por moneda; la lista de multas y la previa se filtran a la moneda de la ficha.

---

## 11. Qué NO hacer

- **No** ventana flotante para nada (todo EN LÍNEA). La aplicación es revertible → NO lleva cartel
  "¿Seguro?".
- **No** botón de acción en el renglón del extracto (es documento de consulta): el disparador es el
  [Usar saldo a favor] de la foto.
- **No** recalcular en el front el tope, el neto, el desglose ni el saldo: todo viene del backend.
- **No** mostrar multas en trámite / en revisión como aplicables.
- **No** mezclar ni convertir monedas; nunca "diferencia de cambio".
- **No** mostrar Id/GUID, enum interno, "CAE", "RG 4540", token de estado ni texto crudo de AFIP:
  siempre en criollo.
- **No** teclear el monto en el neteo (P4 = A): se devuelve el neto completo.
- **No** dejar "Deshacer" una multa con saldo aplicado: primero se revierte la aplicación.

---

## 12. Qué necesita del backend

> Referencia: `UsarSaldoAFavorInline.jsx`, `creditWithdrawalLogic.js`, `CustomerAccountPage.jsx`,
> y los DTOs de cuenta del cliente. El front NO deduce nada.

1. **Lista de multas aplicables del cliente por moneda** (para el picker de la obra a): cada ítem con
   número de reserva legible, nombre del file, "por anulación", lo que falta cobrar, moneda. Solo
   multas firmes (comprobante emitido).
2. **Endpoint para aplicar saldo a favor a una multa** (equivalente a `credit/apply` pero con destino
   multa): recibe moneda + monto + identificador legible/publicId de la multa; drena bolsillos FIFO;
   registra el pago contra la multa; devuelve OK o error en criollo. Revertible.
3. **Previa del neteo en la devolución:** al elegir "Devolver por transferencia/efectivo", el servidor
   calcula y devuelve el desglose (saldo a favor − multas firmes = neto) con los textos por renglón,
   listo para pintar. Revalida al confirmar (si cambió, rechaza con mensaje de "la cuenta cambió").
4. **El renglón "Saldo a favor aplicado" en el extracto** (`CustomerAccountStatementBuilder`): la
   aplicación a una multa aparece como línea Haber (y su inversa al revertir), con `Kind`, `Date`,
   descripción en criollo (`Saldo a favor aplicado · R-… (multa)`), `ReservaPublicId` + número, y su
   `RunningBalance`. "Multas abiertas" de la foto baja/sube en consecuencia.
5. **`activeApplications` distingue destino reserva vs multa** (para la lista revertible §6): un campo
   de tipo/target que el front use para el texto ("a la reserva R-…" vs "a la multa de R-…").
6. **El recibo del egreso lleva el desglose** (§7): armado por el backend.
7. **El panel de Deshacer multa informa si tiene saldo aplicado y cuánto** (para el freno §8).

---

## 13. Resumen para implementadores (frontend-senior)

- Archivo principal del flujo: `src/TravelWeb/src/features/customers/components/UsarSaldoAFavorInline.jsx`
  (suma el destino "Aplicar a una multa" + el picker de multas + la previa del neteo).
- Lógica pura testeable: `src/TravelWeb/src/features/customers/lib/creditWithdrawalLogic.js`
  (nuevo destino en `DESTINOS_RETIRO`, validación de la aplicación a multa, armado del payload). La
  regla de "qué mostrar" vive en el helper puro; el JSX solo pinta.
- Página que orquesta y recarga: `src/TravelWeb/src/features/customers/pages/CustomerAccountPage.jsx`
  (lista de aplicaciones revertibles ampliada al caso multa; título a "Saldo a favor aplicado").
- Extracto: `src/TravelWeb/src/features/customers/components/EstadoCuentaClienteTab.jsx` pinta el
  renglón "Saldo a favor aplicado" (Haber) que entrega el backend.
- Freno del Deshacer: `OperatorPenaltyStepPanel` / `DeshacerMultaEmitidaInline` en la ficha de la
  reserva (texto §8).
- Se monta SOBRE el rediseño del extracto (`docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`):
  el botón [Usar saldo a favor] cuelga de la foto de saldo; la lista revertible cuelga de la foto.
- Depende del backend de §12 (lista de multas aplicables, endpoint de aplicación a multa, previa del
  neteo, renglón en el extracto, distinción de destino, recibo con desglose, dato del freno). Coordinar:
  primero backend expone datos y previa, después el front pinta.
- Gate final obligatorio: `data-exposure-reviewer` (que no se filtre ningún interno) + `frontend-reviewer`
  (que cumpla esta spec y la guía).
