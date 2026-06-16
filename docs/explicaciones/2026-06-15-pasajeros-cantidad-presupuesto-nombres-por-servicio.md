# 2026-06-15 — Dos errores de reservas: pantalla negra al borrar + pasajeros al "cliente aceptó"

## Qué pidió Gastón

1. Al borrar algo en un presupuesto, la pantalla se ponía en negro y no funcionaba.
2. Al hacer click en "el cliente aceptó", el sistema le exigía cargar pasajeros. Sentía una
   desconexión: el presupuesto/cotización debería "venir con los pasajeros". Pidió investigar
   con el experto de dominio (con internet) la diferencia entre cotización y presupuesto y si
   conviene exigir pasajeros en los presupuestos.

## Error 1 — Pantalla negra al borrar (RESUELTO)

**Causa raíz:** en `src/TravelWeb/src/features/reservas/components/ServiceList.jsx` se usaba
`useEffect` dentro de los modales `ModalBorrarVsCancelar` y `ModalBloqueoCancelacionServicio`,
pero `useEffect` **no estaba importado** (el import solo traía `useCallback, useRef, useState`).
Al abrir el modal de borrar/cancelar un servicio se lanzaba `ReferenceError: useEffect is not
defined` durante el render. Como la app **no tenía ningún ErrorBoundary**, una excepción de
render desmontaba todo el árbol de React → página en blanco (negra en modo oscuro).

El build de Vite/Rollup NO detecta hooks usados sin importar, y los tests no renderizaban ese
modal, por eso pasó inadvertido.

**Arreglos:**
- Agregado `useEffect` al import de `ServiceList.jsx` (fix de raíz).
- **Red de seguridad nueva:** `src/TravelWeb/src/components/ErrorBoundary.jsx` (doble nivel:
  uno dentro del `Layout` que conserva la navegación, otro externo de último recurso). Si una
  pantalla se rompe, muestra "Algo se rompió al mostrar esta pantalla" con Recargar / Volver al
  inicio, y manda el detalle técnico a `console.error`. Nunca más pantalla negra muerta.
- Arreglo de `askConfirmation` en `ReservaDetailPage.jsx`: antes cerraba el cartel de confirmar
  ANTES de terminar la operación (sin `await`), permitiendo doble clic; ahora espera, muestra
  "Procesando…" y bloquea el doble clic.
- Limpieza: corregido `handleVolver` (código muerto), `data-testid` y `role="alert"` en el
  ErrorBoundary.

## Error 2 — Pasajeros: cantidad en el presupuesto, nombres por servicio (CONSTRUIDO, sin desplegar)

### Investigación de dominio (con respaldo de industria)

- **Cotización** = borrador interno; **Presupuesto** = propuesta formal al cliente. Ambos se
  arman con **cantidades** (PAX: adultos/menores/infantes), NO con nombres. El precio depende
  de cuántos son y la composición, no de cómo se llaman.
- Los **nombres legales + documento** se cargan **al reservar/emitir con el operador**, no antes
  de que el cliente acepte. El aéreo es el más estricto (nombre exacto al emitir el pasaje);
  hotel/traslado pueden holdear con el titular.
- Pedir nombres antes es fricción innecesaria (el cliente compara precios, los nombres pueden no
  estar confirmados) y acumula dato sensible de gente que quizá no viaje.

### Decisión de Gastón

- El **Presupuesto** exige solo la **cantidad** de pasajeros (count > 0) para avanzar.
- Al **"el cliente aceptó"** ya NO se piden nombres (deja de frenar).
- Los **nombres + documento** se exigen **al resolver/emitir cada servicio**:
  - Aéreo: nombre + documento de todos los pasajeros declarados, antes de "Emitido".
  - Hotel / Traslado: alcanza el **titular** (primer pasajero) con nombre.
  - Asistencia: nombre + documento + fecha de nacimiento de todos.
  - Paquete / Genérico: nombre de todos.
- Todos los pasajeros van en todos los servicios automáticamente (sin paso de asignar a mano;
  se retiró el panel de asignación manual).
- Nunca se factura antes de confirmar el servicio → no se tocó facturación.

### Implementación

- **Diseño:** `docs/architecture/adr/ADR-031-pasajeros-nominales-por-servicio.md` (revisado y
  corregido por el architect-reviewer, que detectó el bypass B1).
- **Backend:**
  - `src/TravelApi.Domain/Reservations/PassengerNominalRules.cs` (helper puro, regla por tipo,
    titular = primer Passenger por Id, conteo por cantidad declarada, mensajes sin exponer el
    número de documento).
  - `BookingService.cs` + `BookingService.CatalogCreates.cs`: envoltorio único
    `EnsureNominalCoverageBeforeResolvingAsync` invocado en TODOS los call-sites que dejan un
    servicio resuelto (status, mark-emitido, no-confirmación, **y los Create/Update de alta** —
    cierre del bypass B1: crear un servicio ya "Confirmado" en InManagement no puede
    auto-confirmar la reserva sin nombres).
  - `ReservaService.cs`: `EnsureReadinessForSaleAsync` aflojado (Budget→InManagement: solo
    cantidad>0 + ≥1 servicio); `GetTransitionReadinessAsync` realineado a Budget→InManagement
    (era preview muerto contra Budget→Confirmed); helper espejo para el servicio genérico.
  - `VoucherService.cs`: guard de pasajeros reforzado (titular con nombre).
  - Sin migración (todos los campos existían). Sin feature flags.
- **Frontend:**
  - `lib/pasajeroHint.js` (+ tests): regla por tipo como pista de UI (la autoridad es el backend).
  - `PasajeroInlineForm.jsx`: carga de nombre/documento EN LÍNEA (sin ventana flotante).
  - `PassengerList.jsx`: renglones vacíos por pasajero declarado + contador "X de N".
  - `ServiceList.jsx`: botón "Marcar emitido"/resolver apagado + cartelito + mini-formulario en
    línea cuando faltan nombres (desktop y mobile).
  - `ReservaDetailPage.jsx`: franja recordatoria "Cargá los nombres antes de emitir" + contador;
    el botón "El cliente aceptó" ya no abre modal y queda apagado con cartelito si la cantidad es 0.
  - Eliminados `ConfirmReservaModal.jsx` y `PassengerAssignmentsPanel.jsx` (huérfanos).
  - Decisiones de UX registradas en `docs/ux/guia-ux-gaston.md` (sección 2026-06-15).

### Cadena de agentes

domain-expert → architect (ADR-031) → architect-reviewer (Changes Required, atendidos) →
backend-senior → frontend-senior → backend-reviewer (Approved w/comments) + security-reviewer
(Approved w/comments) → frontend-reviewer (Changes Required → corregido → **Approved**).

### Pruebas

- Backend unit + architecture: **1766/1766 verde**. Integración Postgres NO corrida (necesita
  Docker; la corre Gastón en el VPS).
- Frontend: build verde + **582/582 verde**.

## Refinamiento v2.1 — "solo para algunos" + autocompletado (misma sesión)

Gastón detectó que cada servicio ya trae su propia cantidad de pasajeros, y que hay servicios
para un subconjunto (ej: excursión solo para los adultos). Se afinó el modelo (ADR-031 v2.1):

- El gate de nombres por servicio mira EL SET de ese servicio, no el total de la reserva.
- Por defecto cada servicio es para TODOS (cero clics). "Solo para algunos" = asignación explícita
  (control "Para: Todos / X de N" por servicio, panel de tildes en línea). El subconjunto lo
  determinan SOLO las asignaciones explícitas; la cantidad propia del servicio NUNCA achica el set
  (evita falsos "para algunos" por los valores por defecto). Default seguro: sin asignar = pide todos.
- La cantidad de la reserva se autocompleta desde los servicios (sugerencia con "Usar", no pisa lo
  cargado; el total persistido sigue siendo fuente única).
- Backend: limpieza transaccional de asignaciones al borrar un servicio (en los dos caminos de
  borrado) con auditoría atómica (StageBusinessEvent, sin SaveChanges intermedio); endpoints nuevos
  `GET .../services/{tipo}/{id}/nominal-coverage` y `PUT .../services/{tipo}/{id}/assignments`
  (reemplazo atómico del set); auditoría de alta/baja/reemplazo de asignaciones sin exponer documento.
- Reviews: architect-reviewer Ready; backend-reviewer + security Approved w/comments (atomicidad
  cerrada); frontend-reviewer Approved.
- Pruebas finales: backend unit + architecture **1794/1794**; frontend build + **600/600**.

## Pendiente (lo que NO puedo hacer yo)

1. **Commit/push** (si Gastón lo aprueba) — el trabajo está en el working tree, sin commitear.
2. **Deploy** acumulado (lo hace Gastón).
3. **Integración Postgres** en el VPS (`run-tests-all`) antes del deploy.
4. Comentarios no-bloqueantes para antes de tener clientes reales: voucher por excepción de
   4 ojos (pre-existente), un par de tests extra (genérico, voucher resuelto/no-resuelto),
   mini-formulario en la card mobile (hoy puntea a la solapa Pasajeros).
