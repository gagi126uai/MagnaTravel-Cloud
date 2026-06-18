# Cómo armar y probar la NOTA DE DÉBITO en homologación (ADR-013/014)

La factura y la nota de crédito ya están probadas en homologación. Lo único sin probar es la **nota de
débito** (la que se emite por la **multa propia de la agencia** al cancelar). Acá están los pasos exactos
para armar ese caso. Todo verificado en el código (no inventado).

## Antes de empezar (configuración, una sola vez)

1. **ARCA en modo homologación** (el switch `IsProduction` en false) y emisor **Monotributo (Factura C)**
   — como ya lo venís usando para probar facturas y notas de crédito.
2. **Prender dos llaves:**
   - `EnableNewCancellationFlow` = **true** (llave maestra del módulo de cancelación). Esta NO está en el
     panel a propósito: se prende por base de datos / config.
   - `EnableCancellationDebitNote` = **true** (la nota de débito). Esta SÍ se prende desde
     **Configuración** (está marcada como "zona peligrosa", con modal de confirmación).
   - El sistema exige que la primera esté prendida para aceptar la segunda.
3. El usuario que prueba tiene que tener el permiso **`cancellations.classify_agency_penalty`** (el Admin
   ya lo tiene).

## Pasos para armar el caso

1. Crear una **reserva**, cargar un servicio con precio y **confirmarla**.
2. **Emitir la Factura C** de esa reserva (la factura original — la que ya sabés que homologa).
3. **Cancelar** la reserva. En el modal de cancelación, en la parte de la penalidad, elegir:
   - **"Cargo propio de la agencia"** (NO "del operador"),
   - concepto **"Cargo de gestión"** (o "Cargo de cancelación"),
   - estado **"Confirmada"**,
   - un **monto en pesos, menor o igual al de la factura**.
4. El sistema emite **primero la Nota de Crédito total** (obtiene CAE) y, en cuanto la NC tiene CAE,
   **emite automáticamente la Nota de Débito C** asociada a la factura original.
5. Verificar en la **bandeja de notas de débito** (pantalla de notas de débito pendientes/emitidas) que la
   ND quedó **emitida con CAE**.

## Variante "diferida" (opcional, también vale para probar)

Si en el paso 3 elegís estado **"Estimada"** (todavía no sabés el monto final), la ND NO se emite en el
momento. Después, cuando el operador confirma el monto, abrís el **modal de "Confirmar penalidad"** (desde
la bandeja), cargás el monto confirmado + la fecha, y ahí se emite la ND.

## Qué rebota si algo está mal (y qué significa)

- **"La nota de crédito todavía no tiene CAE, esperá"** (INV-ADR014-001): la ND espera a que la NC obtenga
  CAE primero. Es el orden correcto, no es un error.
- **"Esta penalidad es del operador, no emite cargo propio"** (INV-ADR014-002): elegiste "del operador"
  (pass-through). Para que haya ND hay que elegir **"cargo propio de la agencia"**.
- Cualquier caso fuera del camino feliz (factura que no sea C, moneda que no sea pesos, multa mayor a la
  factura) va a **revisión manual** y NO emite ND por las dudas. Es la red de seguridad.

## Lo que esto destraba

Con esta prueba en verde, la nota de débito queda homologada igual que la factura y la nota de crédito, y
el módulo de cancelación con multa (criterio del contador del 1ero de junio: NC total + ND) queda listo
para usar con clientes reales.
