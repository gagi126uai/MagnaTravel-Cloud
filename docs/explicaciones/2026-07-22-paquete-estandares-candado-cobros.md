# 2026-07-22 — Paquete de estándares: constitución, candado C2/C4, saneo de cobros y cartel emergente

Explicación nivel trainee de lo que subió hoy en cuatro commits (52d6d784, 369be498, 2dfa5619, 97a55f1a).

## 1. La Constitución quedó guardada en el repo (52d6d784)

Los tres documentos firmados por Gastón (62 reglas P/F/T/PR + 18 huecos ERP E-* + 20 huecos del rubro A-*) ahora viven en `docs/estandares/`. Junto con ellos entraron la matriz de decisiones del candado (`docs/architecture/2026-07-22-matriz-candado-decisiones-gaston.md`) y dos specs UX firmadas. Desde ahora, todo brief a un agente cita reglas por número y todo reviewer bloquea citando la regla violada.

## 2. Candado C2/C4 en el motor (369be498)

**El problema**: una reserva Confirmada tiene "candado" — para editarla hay que pedir un destrabe (autorización con motivo, que expira). Pero dos puertas estaban sin llave: anular UN servicio suelto y confirmar el costo de un servicio pasaban por al lado del candado.

**La solución**: el guard del candado (`ReservaLockGuard`) ahora corre también en `CancelServiceAsync` (operación `ServiceCancelled`) y en los 5 caminos de confirmar costo (operación nueva `ServiceCostConfirmed`). Detalles finos:

- El guard va DENTRO de la transacción y DESPUÉS del `ChangeTracker.Clear()` de los reintentos de concurrencia: así corre en cada reintento con datos frescos y deja exactamente UNA fila de auditoría (quién destrabó, cuándo, qué operación).
- Anular la reserva ENTERA queda a propósito FUERA de este candado: tiene su propio circuito con frenos fiscales (decisión C3 de la matriz; pendiente de re-confirmar con Gastón).
- Los tests viejos cuyo escenario era una Confirmada se arreglaron CARGANDO una autorización viva real (nunca aflojando el candado).

**Reviews**: técnico y de seguridad, ambos aprobados citando F-1/T-3/F-6/PR-4. Verificado con la app real: sin destrabe → 409 con mensaje claro; con destrabe → pasa.

## 3. Saneo del catch ancho de cobros (2dfa5619)

**El problema**: los rechazos de negocio de cobros viajaban como `InvalidOperationException` genérica y el controller tenía filtros anchos: una falla técnica podía disfrazarse de mensaje de negocio, o un rechazo legítimo salir como error feo.

**La solución**: excepción tipada `PaymentValidationException` (mensaje siempre en criollo, apto para el vendedor) para TODO rechazo de negocio de cobros; el controller la atrapa angosto → 409/400 con el mensaje; lo que no sea negocio cae al 500 genérico neutro (el detalle técnico va solo al log). De paso se cerró un vector latente: un `catch (ArgumentException)` viejo que podía devolver texto de framework en inglés.

**Tests**: 21 asserts en Adr032 + colaterales en 6 archivos más, todos exigiendo tipo exacto + mensaje (jamás relajados). Suite unit: 3934/3934. Reviews: técnico, seguridad y exposición de datos aprobados; el retoque final re-revisado.

## 4. Cartel emergente único + renombres (97a55f1a)

Los avisos LARGOS de rechazo del motor ahora salen en una ventana emergente única (`CartelEmergente`), igual en todas las pantallas — antes cada una tenía su estilo y rompía la estética (feedback de Gastón probando en PROD). Las fichas de trabajo inline siguen inline. Además, los renombres firmados: "Anular reserva" / "Anular varios servicios" / "Anular servicio".

## Verificado de verdad (app corriendo, no solo tests)

- Paseos E2E reales: p1 16/16 · p3 18/18 · p4 28/28.
- Candado C2 y C4 probados por API real (409 → destrabe → 200), con el mensaje en criollo.
- Cobros: pago inválido → mensaje de negocio; pago válido → registrado (20/20).
- CI completo verde (unit + integración Postgres + front) y deploy al VPS exitoso.

## Qué queda para después

- Obra C1 del frontend del candado (botones apagados + ofrecer destrabar al tocar) — spec firmada lista.
- Fase 2 del cartel (unificar ventanas viejas: Swal costo $0, ConfirmModal).
- Nits de reviewers (backlog): unificar vocabulario del rastro de auditoría, test que fije el texto del candado end-to-end, parametrizar test de confirmar costo por los 5 tipos, extender saneo T-2 a `CustomersController.ApplyCreditToPenalty`, defensa en profundidad en `EnsureCollectable`.
- Confirmar con Gastón la asimetría C3 (anular reserva entera sin candado).
- La revisión a mano de Gastón del sistema completo: su lista de errores manda.
