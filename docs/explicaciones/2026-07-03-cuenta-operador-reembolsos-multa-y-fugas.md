# 2026-07-03 — Cuenta del operador: la multa a la vista + cierre de la tanda cortada

> Contexto: la sesión del 2026-07-03 se cortó a mitad (se acabó el crédito). Esta sesión de cierre
> retomó el árbol de trabajo, terminó lo que faltaba, pasó todas las reviews y dejó la tanda lista.

## Qué se construyó (spec aprobada: `docs/ux/2026-07-03-cuenta-operador-reembolsos-multa.md`)

Las 5 decisiones de dirección de Gastón (2026-07-02), cerradas con P1=C y P2..P6=A:

1. **"Registrar reembolso recibido" muestra la cuenta completa** (P3): en el renglón se lee
   "Pagaste US$ 500 − Multa del operador US$ 100 = te devuelven US$ 400 (estimado)." con los montos
   reales. El backend expone por caso y moneda `PaidToOperator`, `PenaltyRetained`, `AmountReceived`
   y garantiza el invariante `estimado = pagado − multa − ya devuelto` (con tope por línea para que
   la cuenta cierre incluso si el operador devolvió de más — hallazgo de la review backend).
2. **Reembolso $0 explicado en criollo** (P4): en vez del "$0" seco, el motivo que informa el
   backend (`ZeroRefundReason`): "Todavía no le pagaste nada al operador…" / "la multa se quedó con
   todo…" / "Ya te devolvió todo…". El front NUNCA deduce restando; sin permiso de costos viaja null
   (el motivo es cualitativo sobre costos — decisión de la review de seguridad).
3. **Puente a la multa desde el operador** (P2): la solapa "Reembolsos" avisa "Falta confirmar la
   multa de esta anulación." + botón "Ir a la reserva a confirmar" (`PenaltyPendingConfirmation`).
   La acción fiscal sigue viviendo SOLO en la reserva.
4. **Chip "Operador: X" en cada servicio de la reserva** (P5): debajo del nombre, link a la ficha
   del operador solo con permiso `proveedores.view` (texto plano sin él). Desktop y mobile.
5. **"Reembolsos operador" salió del menú, SIN vista global** (P1=C, elección consciente de Gastón):
   ruta `/operator-refunds` eliminada, página `OperatorRefundsPage.jsx` borrada, hook/API sin la rama
   global. Los reembolsos se ven operador por operador. Trade-off anotado en la spec: los vencidos ya
   no se juntan en un solo lugar.

Además (RESTOS): la solapa ahora muestra también los **residuos** (parcialmente devuelto / cerrada
con resto / en proceso) con etiqueta en español y "− Ya devuelto" en la cuenta, y un flag
`CanRegisterRefund` deshabilita las filas que el backend no aceptaría.

## Conciliación por construcción

El recuadro naranja "Me tiene que devolver" y la solapa "Reembolsos" salían de dos cálculos
distintos. Ahora comparten la MISMA fórmula (`SupplierCancellationCircuitReader.LiveReceivableForLine`
/ `IsReceivableEligible`), así que cuadran **por construcción** (test de conciliación con números
explícitos: 750 == 750). La review backend confirmó que esto además corrigió una divergencia real
preexistente (el read-model recortaba después de sumar; el extracto por línea).

## Fugas de mensajes técnicos cerradas (data-exposure)

Tarea #9 del retomo + 6 fugas nuevas que encontró el gate en esta sesión (B1–B6):

- `ArcaErrorSanitizer` (nuevo, blocklist): el texto de negocio en español pasa; el ruido técnico
  (.NET/EF/XML/SOAP) se reemplaza por un genérico. Usado por la bandeja de NDs (FUGA 1), la
  notificación de anulación fallida (FUGA 2) y TODOS los 409 (FUGA 3) **y ahora también los 400**
  (B6, con recorte del sufijo `(Parameter 'x')` del framework) de `CancellationsController`.
- Mensajes reescritos en criollo (el detalle técnico va al log): flag de módulo apagado (B1), caso
  fiscal TotalPlusNewInvoice en Confirm (B2) y en editar liquidación (B4), operador no encontrado
  (B3, sin GUIDs), fechas de confirmar multa (B5, sin `OperatorConfirmationDate`).

## Extracto del cliente server-side (venía del 2026-07-02, ahora reviewado y commiteado)

El armado del extracto de la cuenta del cliente se movió del front al backend
(`CustomerAccountStatementBuilder`); la lib del front se borró. La review frontend (que estaba
pendiente) pasó: estados UX correctos, multimoneda separada, y de paso quedó arreglado que las
tarjetas Ventas/Cobrado ya no suman ARS+USD en un escalar.

## Reviews

- backend-dotnet-reviewer: Approved-with-conditions → la condición (sobre-reembolso rompía el
  desglose visible) se arregló con tope por línea + 2 tests.
- data-exposure-reviewer (gate obligatorio): Blocked (B1–B6) → todo arreglado + tests → re-review.
- frontend-reviewer: Approved-with-conditions → el MAJOR (faltaba el desglose del mockup B en la
  solapa) se arregló reusando `construirTextoCuentaReembolso` (fuente única con el panel) → re-review.

## Suites

- Backend unit: verde (incluye tests nuevos de sobre-reembolso y fugas B6).
- Integración (Docker Postgres, el CI no las corre): 205/207; los 2 fallos
  (`OwnershipResolverPostgresTests`) pasan en aislamiento — flaky de interferencia entre tests,
  PREEXISTENTE, anotado como follow-up.
- Front: verde + build de producción.

## Pendientes que NO bloquean (anotados)

- Verificar en prod que la #F-2026-1025 aparezca en la bandeja de NDs pendientes y reintentarla
  (la bandeja tiene botón "Reintentar").
- Flaky de `OwnershipResolverPostgresTests` al correr la suite completa de integración.
- ESLint `no-use-before-define` en TravelWeb (prevención TDZ).
- Test de carrera de idempotencia del reembolso contra Postgres real (viejo follow-up).
- Divergencia teórica header vs extracto del cliente si un saldo por reserva queda negativo
  (documentada por la review backend; hoy el barrido a saldo a favor lo impide).
