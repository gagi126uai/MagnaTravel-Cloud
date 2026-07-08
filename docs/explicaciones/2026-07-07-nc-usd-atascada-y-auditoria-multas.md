# 2026-07-07 (noche) — NC USD atascada, auditoría de multas/estados y moneda automática

Trabajo disparado por el dogfood de Gastón de esta noche: la prueba A salió bien,
la prueba B (anular una reserva facturada en dólares) dejó la anulación A MEDIAS
porque la nota de crédito en dólares falló, y volvió a reportar que "lo de las
multas se ve muy raro" sin poder explicar por qué.

## 1. NC en dólares atascada — diagnóstico (investigación, sin fix todavía)

**Qué se descartó con evidencia:**
- `CanMisMonExt` SÍ se emite hoy (la memoria del 2026-07-01 que decía lo contrario
  quedó obsoleta el 02/07 con ADR-042). La NC espeja MonId/MonCotiz/CanMisMonExt de
  su factura original (`InvoiceService.cs:1470-1471`, `AfipService.cs:783-785`).
- `AfipService` NO se tocó entre el 03/07 (primeras NC ARS+USD reales OK) y hoy:
  el XML de la NC se arma igual que cuando funcionó. No es una regresión "de forma".

**Las dos causas candidatas (se ven casi iguales en pantalla, fix opuesto):**
- **(A) Error técnico/transitorio de AFIP** (red caída, timeout): la NC queda
  `Pending`, el BookingCancellation queda `AwaitingFiscalConfirmation` y nunca
  llega el callback. **"Reintentar" SÍ resuelve.** AFIP es intermitente (hay
  antecedente de 30 min caída en la memoria del proyecto).
- **(B) Rechazo real de ARCA** (`Resultado="R"`) por un dato: el candidato fuerte
  es **la cotización** — la NC hereda el `MonCotiz` manual de la factura pero sale
  con `CbteFch` de HOY; si ARCA exige la cotización del día (RG 5616), rebota.
  Candidato secundario: coherencia del bloque `CbtesAsoc` en divisa.
  **"Reintentar" NO alcanza** (re-hereda el mismo dato → loop): conecta con el
  norte pendiente "TC real del BNA por fecha" (ADR-011).

**Dato que falta para confirmar (pedido a Gastón):** el texto del error de la NC
(campo Observaciones del comprobante / cartel de la anulación) y si su estado dice
"Rechazado" (→ causa B) o "En proceso" (→ causa A).

**Estado contradictorio de la ficha atascada (confirma el "está raro"):** la
reserva ya está transicionada (PendingOperatorRefund) con TODOS los servicios
cancelados, el chip de arriba sigue diciendo "Facturada total", el cuerpo muestra
"anulación en revisión / Reintentar", y el paso de multa queda colgado porque el
gate de la ND exige el puntero de NC principal que es null. Cuatro máquinas de
estado en una foto.

## 2. Auditoría de multas y estados (erp-systems-expert) — el diagnóstico

**La raíz del "está raro y no sé explicarlo": dos multas distintas fusionadas.**
1. La multa que el OPERADOR le retiene a la agencia (cuentas a PAGAR, tema con el
   proveedor).
2. La multa que la agencia le traslada al CLIENTE con una ND fiscal (cuentas a
   COBRAR, tema con el cliente).

La ficha hace UNA pregunta operador-céntrica ("¿El operador te cobró multa?") que
al confirmarse produce un efecto cliente-céntrico (emite la ND al cliente) sin
decirlo en primer plano (`ConfirmarMultaOperadorInline.jsx`, la aclaración está
enterrada en gris). Hasta los comentarios del código se contradicen sobre de quién
es la multa (I2).

**Incoherencias detectadas** (detalle completo con file:line en el reporte del
agente, resumen): I1 dos multas fusionadas (ALTA); I2 código contradictorio (ALTA);
I3 banner "falta decidir la multa" sin botones cuando no se puede actuar; I4 la
multa salta de lugar y de nombre al confirmarse (tarea en el cuerpo → chip de plata
arriba); I5 anulación a medias convive con chip "Facturada total"; I7 multa con ND
rebotada desaparece de la ficha (vive solo en la bandeja — mal para un dueño que ES
el back-office); I6/I8 rótulos duplicados y "Deshacer" mezclado con texto de estado.

**Patrón ERP recomendado:** el objeto correcto ya existe (BookingCancellation);
falta la pantalla que lo muestre como UN documento con dos caras. Propuesta: una
sola tarjeta "Anulación de la reserva" con dos columnas — **"Lo del cliente"**
(NC devuelta, ND de multa con su estado de cobro) y **"Lo del operador"**
(la pregunta "¿te cobró multa?", lo retenido, el reembolso pendiente/recibido) —
con el estado del documento arriba ("Anulación completa / a medias — Reintentar")
y, si es traslado 1:1, una flecha "→ trasladado al cliente" entre columnas.

**Preguntas abiertas para Gastón (definen el diseño, NO construir antes):**
1. Cuando el operador retiene multa, ¿siempre se la traslada igual al cliente
   (mismo monto), o a veces se la come la agencia o cobra un número distinto?
2. ¿Quiere ver en la ficha lo que le CUESTA la anulación (lo retenido por el
   operador) aunque no se lo cobre al cliente?
3. Una multa cuya ND rebotó: ¿verla en la ficha como "en revisión" o que viva solo
   en la bandeja de administración?

Validación fiscal pendiente: la forma exacta del traslado (una ND vs dos
comprobantes, modelo intermediario) la debe mirar `travel-agency-accountant-argentina`
antes de implementar.

## 3. Moneda automática al facturar — ya existía (con un hueco)

El pedido de Gastón ("si detecta dólares que elija dólares") YA estaba implementado
en el form de la ficha (`EmitirFacturaInline` → `elegirGrupoPrecarga`): solo USD →
precarga USD; solo ARS → ARS; mezcla → ARS sin adivinar. Se hizo solo un refactor
sin cambio de comportamiento (función pura extraída a `lib/invoiceCurrencyDefault.js`
+ tests que importan la función real) — commit `6ed083e`, frontend-reviewer APROBADO,
1772/1772 tests.

**El hueco:** `CreateInvoiceModal` (colas "Pendientes de facturar" y Facturación
global) NO consulta las monedas de los servicios y arranca siempre en ARS. Si
Gastón facturó desde ahí, por eso no vio el automático. A confirmar con él desde
qué pantalla factura; extenderlo a ese modal es tarea aparte (área fiscal, con
reviewers).

## Estado al cierre

- Deployado hoy: aviso del vigía con números (`8ca5193`) + refactor moneda
  (`6ed083e`). Sin fix de la NC USD (bloqueado en el texto del error).
- Próximo paso 1: con el error de Gastón → arreglar causa A (retry/resiliencia) o
  causa B (cotización de la NC = decisión fiscal + norte TC BNA adelantado).
- Próximo paso 2: respuestas de Gastón a las 3 preguntas de multas + las 2 de
  facturación → diseño de la tarjeta "Anulación" (gate UX con ux-ui-disenador,
  porque cambia pantalla) → implementación.
