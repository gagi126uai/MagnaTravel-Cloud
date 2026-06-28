# Deuda por reserva — rediseño a grilla + solapa propia (2026-06-28)

> **Origen:** Gastón rechazó la sección "Deuda por expediente" de la ficha del operador.
> Está hecha con cajitas sueltas, choca con el resto de la página y vive metida adentro de
> "Cuenta corriente". Instrucción del dueño (textual de intención):
> 1. Que sea **TODA GRILLA** (una tabla limpia y pareja, como el resto de la página), no cajitas.
> 2. Que viva en **su propia solapa** ("fijate cómo lo hacen los ERPs").
> 3. **Renombrarla "Deuda por reserva"** (NO "expediente").
>
> Este documento es la especificación para `frontend-senior`. Lo que NO esté cerrado acá
> está en el bloque de PREGUNTAS PARA GASTÓN del final y NO se construye hasta que responda.

---

## 1. Diseño recomendado (qué se construye)

### 1.1 Idea base: una grilla por moneda, calcada del Extracto

La página ya tiene un patrón limpio y aprobado: el **Extracto de cuenta**
(`SupplierExtractoSection`) muestra **un bloque por moneda**, cada bloque con su encabezado
(la moneda + el saldo a la derecha) y abajo una **tabla `DataGrid`** pareja. La nueva
"Deuda por reserva" se hace **igual**: un bloque por moneda, una sola tabla adentro.

Por qué así y no una tabla única con una columna "Moneda":
- Es **idéntico al Extracto** que está al lado → la página queda uniforme.
- Cumple sola la **regla dura de multimoneda**: pesos y dólares nunca se tocan ni se suman;
  cada uno tiene su propio bloque y su propio total.
- Es como los ERPs muestran la deuda a proveedores (apertura por moneda, una fila por reserva).

### 1.2 Mockup de la grilla (caso normal, con permiso de ver montos)

```
┌─ Solapas de la ficha del operador ─────────────────────────────────────────────┐
│ Cuenta corriente │ ▸ Deuda por reserva │ Servicios comprados │ Reembolsos │ … │
└────────────────────────────────────────────────────────────────────────────────┘

  Deuda por reserva

  ┌─ $  PESOS ─────────────────────────────────────────  Total deuda:  $ 180.000 ─┐
  │  Reserva       Detalle              Compras       Pagado          Saldo        │
  │ ───────────────────────────────────────────────────────────────────────────── │
  │  R-1042 ↗      Fam. García          $ 120.000     $ 50.000        $ 70.000      │
  │  R-1051 ↗      Fam. López           $ 150.000     $ 40.000        $ 110.000     │
  │ ───────────────────────────────────────────────────────────────────────────── │
  │  Anticipos a cuenta (pagos sin reserva imputada)                 − $ 30.000     │
  └────────────────────────────────────────────────────────────────────────────────┘

  ┌─ US$  DÓLARES ─────────────────────────────────────  Total deuda:  US$ 300 ────┐
  │  Reserva       Detalle              Compras       Pagado          Saldo        │
  │ ───────────────────────────────────────────────────────────────────────────── │
  │  R-1042 ↗      Fam. García          US$ 450       US$ 150         US$ 300        │
  └────────────────────────────────────────────────────────────────────────────────┘
```

Detalles del diseño:
- **Un bloque por moneda**, en el mismo orden que usa el Extracto (Pesos primero, Dólares después).
  Si el operador maneja una sola moneda, se ve un solo bloque (regla dura #3 de multimoneda).
- **Encabezado del bloque** = `CurrencyBadge` + la palabra "Pesos"/"Dólares" a la izquierda, y
  a la derecha **"Total deuda: $ X"** (este es `globalTotals` de esa moneda). Mismo lugar donde
  el Extracto pone su "Saldo". Color: rojo si debemos (>0), verde si pagamos de más (<0), gris
  si es 0 — igual que el Extracto.
- **Tabla `DataGrid`** (los mismos componentes `DataGrid/DataGridHeader/Row/Cell` del Extracto),
  densidad `compact`, una **fila por reserva**. Columnas:
  - **Reserva** — el `numeroReserva` (ej. "R-1042") como **enlace real** (`<Link>` a
    `/reservas/{reservaPublicId}`) con la flechita ↗, para que funcione Ctrl+click / abrir en
    pestaña nueva. El `publicId` solo se usa para armar el link; nunca se muestra.
  - **Detalle** — el `fileName` (texto, sin link). Alineado a la izquierda. (El nombre de esta
    columna está en PREGUNTAS — P3.)
  - **Compras** — `confirmedPurchases`, alineado a la derecha, fuente mono.
  - **Pagado** — `totalPaid`, a la derecha.
  - **Saldo** — `balance`, a la derecha, en negrita; rojo si >0 (debemos), verde si <0
    (saldo a favor), gris si 0. Misma semántica de color que el Extracto.
- **Anticipos a cuenta** (pagos del operador sin imputar a ninguna reserva): van como una
  **fila al pie del bloque de su moneda**, en cursiva/atenuada, con la etiqueta
  "Anticipos a cuenta (pagos sin reserva imputada)" y el monto **en negativo** en la columna
  Saldo (restan). Si en esa moneda no hay anticipos, la fila no aparece. (Alternativa en P4.)

### 1.3 Enmascarado sin permiso `cobranzas.see_cost`

Mismo criterio que ya rige en toda la app (guía 2026-06-05 / 2026-06-09): quien no tiene
permiso de ver costos **no ve ningún monto de costo/deuda**. El backend ya devuelve los montos
en 0/ocultos. En pantalla:

- **Un solo aviso arriba de la solapa** (no repetido por bloque): "No tenés permiso para ver
  los montos de deuda." (un cartel arriba, coherente con la limpieza de encabezado ya decidida).
- En **Compras / Pagado / Saldo** y en el **Total deuda** del encabezado: se muestra **"—" en
  gris neutro**, nunca verde ni rojo (para no confundir "sin permiso" con "no debe nada").
- La estructura de la grilla (reservas, columnas, links) se sigue viendo: el vendedor ve qué
  reservas hay con el operador, pero los números van tapados.

```
  ⚠ No tenés permiso para ver los montos de deuda.

  ┌─ $  PESOS ─────────────────────────────────────────  Total deuda:  —  ─────────┐
  │  Reserva       Detalle              Compras       Pagado          Saldo        │
  │ ───────────────────────────────────────────────────────────────────────────── │
  │  R-1042 ↗      Fam. García          —             —               —             │
  │  R-1051 ↗      Fam. López           —             —               —             │
  └────────────────────────────────────────────────────────────────────────────────┘
```

### 1.4 Estados de la pantalla

- **Cargando:** caja con el título "Deuda por reserva" y "Cargando deuda…" (igual que hoy).
- **Vacío** (sin reservas ni anticipos): tarjeta centrada con ícono y el texto
  **"No hay deuda registrada con este operador."** (cambia "proveedor" → "operador" para hablar
  como el resto de la ficha, que dice "Operador").
- **Error:** "No se pudo cargar la información. Intentá recargar la página." con botón Reintentar
  (mismo patrón que el Extracto).

```
  Deuda por reserva
  ┌────────────────────────────────────────────────┐
  │                  ▢  (ícono)                     │
  │   No hay deuda registrada con este operador.    │
  └────────────────────────────────────────────────┘
```

---

## 2. La solapa nueva

- **Nombre de la solapa:** **"Deuda por reserva"** (con un ícono coherente, ej. el de capas/
  layers que ya usaba la sección, o el de documento — a criterio de `frontend-senior`, mismo
  estilo que las otras solapas).
- **Posición recomendada:** **segunda solapa, justo después de "Cuenta corriente"**. Quedan
  **6 solapas**:

```
  Cuenta corriente │ Deuda por reserva │ Servicios comprados │ Reembolsos │ Datos bancarios │ Datos
```

- **Se saca de adentro de "Cuenta corriente":** sí. Hoy `SupplierDebtByReservaSection` está
  apilada debajo del Extracto dentro de la solapa "Cuenta corriente" (~L1304). **Se mueve a la
  solapa nueva.** "Cuenta corriente" queda solo con: botones de acción + ficha de pago en línea
  + Extracto. Más limpia.
- **El "Total deuda" por moneda:** vive **solo en la solapa nueva** (en el encabezado de cada
  bloque). No se duplica dentro de "Cuenta corriente": arriba de toda la ficha ya están los
  **chips de saldo por moneda** (`BalanceHeaderChips`, siempre visibles sobre las solapas), que
  ya muestran cuánto se le debe al operador en cada moneda. Duplicarlo sería ruido. (Confirmar
  en P7.)

---

## 3. Qué NO hay que hacer

- **No** mantener las cajitas/mini-recuadros por moneda (lo que Gastón rechazó). Todo va en
  `DataGrid`, como el Extracto y "Servicios comprados".
- **No** sumar ni convertir pesos y dólares en un número (regla dura multimoneda).
- **No** mostrar verde/rojo cuando no hay permiso: siempre "—" gris.
- **No** mostrar `publicId` ni ningún código interno; el número de reserva alcanza y va como link.
- **No** dejar la palabra "expediente" en lo que ve el usuario (título, solapa, textos). Los
  `data-testid` internos pueden quedar, pero el texto visible dice "reserva".

---

## PREGUNTAS PARA GASTON

### Tema: Deuda por reserva del operador (la sección que pediste pasar a grilla)
Contexto: ya quedó claro que va toda en grilla, en su propia solapa y se llama "Deuda por
reserva". Faltan definir unos detalles de cómo se arma la tabla. Te dejo cada uno con dibujito;
podés responder "1A, 2B…" o "otra cosa: …".

**P1. ¿Cómo agrupamos la tabla cuando el operador tiene plata en pesos Y en dólares?**
  A) (recomendada) **Una tabla por moneda**, una abajo de la otra — igual que el Extracto de al lado.
```
     $  PESOS                                  Total deuda: $ 180.000
     Reserva    Detalle      Compras    Pagado    Saldo
     R-1042 ↗   Fam.García   $120.000   $50.000   $70.000
     ───────────────────────────────────────────────────
     US$  DÓLARES                              Total deuda: US$ 300
     Reserva    Detalle      Compras    Pagado    Saldo
     R-1042 ↗   Fam.García   US$450     US$150    US$300
```
  B) **Una sola tabla** con una columna "Moneda" y un renglón por reserva-y-moneda.
```
     Reserva    Detalle     Moneda   Compras    Pagado    Saldo
     R-1042 ↗   Fam.García   $       $120.000   $50.000   $70.000
     R-1042 ↗   Fam.García   US$     US$450     US$150    US$300
```
  (Recomiendo A: queda idéntica al Extracto y nunca mezcla monedas.)

**P2. ¿Qué columnas de plata mostramos?**
  A) (recomendada) **Las tres: Compras · Pagado · Saldo** (igual que el Extracto muestra Cargo·Abono·Saldo).
```
     Reserva    Detalle      Compras    Pagado    Saldo
     R-1042 ↗   Fam.García   $120.000   $50.000   $70.000
```
  B) **Solo el Saldo** (lo que falta pagar), más simple.
```
     Reserva    Detalle      Saldo
     R-1042 ↗   Fam.García   $70.000
```
  (Recomiendo A: ver de un vistazo cuánto se compró y cuánto se pagó es lo que hace un ERP.)

**P3. La columna de al lado del número de reserva muestra el nombre del file (ej. "Fam. García"). ¿Cómo la titulamos?**
  A) (recomendada) **"Detalle"**.
  B) **"Titular"** (el apellido/familia del cliente).
  C) **"File"** o **"Expediente"** — ojo que "expediente" es la palabra que pediste sacar.
```
     Reserva    [A: Detalle / B: Titular]   Compras …
     R-1042 ↗   Fam. García                 …
```
  (Recomiendo A "Detalle"; "Titular" también sirve si te gusta más. Evitaría "File/Expediente".)

**P4. Los "Anticipos a cuenta" (plata que le pagaste al operador sin asignar a ninguna reserva). ¿Dónde van?**
  A) (recomendada) **Una fila al pie de la tabla de su moneda**, restando.
```
     R-1042 ↗   Fam.García   $120.000   $50.000   $70.000
     ──────────────────────────────────────────────────
     Anticipos a cuenta (pagos sin reserva imputada)   − $ 30.000
```
  B) **Una tablita aparte**, debajo de todas las monedas.
```
     (tabla de reservas)
     ──────────────
     Anticipos a cuenta
     $   − $ 30.000
     US$ − US$ 0
```
  (Recomiendo A: queda todo en la misma grilla y se entiende que el anticipo baja la deuda de esa moneda.)

**P5. El "Total deuda" de cada moneda, ¿dónde lo ponemos?**
  A) (recomendada) **Arriba a la derecha del bloque de esa moneda** (donde el Extracto pone el Saldo).
```
     $  PESOS                          Total deuda: $ 180.000
     Reserva   Detalle   …
```
  B) **Como un renglón TOTAL al pie** de la tabla de esa moneda.
```
     R-1051 ↗  Fam.López  …  $110.000
     ─────────────────────────────────
     TOTAL                    $ 180.000
```
  (Recomiendo A: queda igual que el Extracto de al lado.)

**P6. ¿En qué orden va la solapa nueva y se saca de "Cuenta corriente"?**
  A) (recomendada) **Segunda, justo después de "Cuenta corriente"** (6 solapas), y la deuda se
     **saca** de adentro de "Cuenta corriente".
```
     Cuenta corriente │ Deuda por reserva │ Servicios comprados │ Reembolsos │ Datos bancarios │ Datos
```
  B) **Última de las de plata** (después de "Servicios comprados").
```
     Cuenta corriente │ Servicios comprados │ Deuda por reserva │ Reembolsos │ Datos bancarios │ Datos
```
  (Recomiendo A: deuda y cuenta corriente son hermanas, conviene que estén pegadas.)

**P7. Arriba de toda la ficha del operador ya están los chips de "Le debo: $ … / US$ …" (siempre a la vista). ¿Mostramos además el total de deuda dentro de "Cuenta corriente"?**
  A) (recomendada) **No, solo en la solapa nueva** (los chips de arriba ya muestran el total).
  B) **Sí, repetirlo** también en "Cuenta corriente".
  (Recomiendo A: no repetir el mismo número en dos lugares de la misma pantalla.)

---

> **Nota de consistencia técnica (para frontend-senior, no es pregunta a Gastón):** usar los
> mismos componentes `DataGrid` y el `CurrencyBadge` que `SupplierExtractoSection.jsx`, densidad
> `compact`, con scroll horizontal en mobile como ya hace el Extracto. El comportamiento mobile
> queda cubierto por ese patrón existente (no se inventa nada nuevo).
