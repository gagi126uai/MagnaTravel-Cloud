# Cambio 3 — Ficha de reserva: filtro "mostrar servicios cancelados" + historial de estados

> Estado: **APROBADO por Gastón (2026-07-05)** — respondió P6-A, P7-A, P8-A (las recomendadas).
> Sin preguntas abiertas. Listo para frontend-senior.
> Reglas en la guía: sección "Servicios cancelados de la reserva: escondidos por defecto + historial de estados (2026-07-05)".
> Pedido de Gastón (2026-07-05): poder trackear qué se compró / canceló / confirmó / solicitó sin que la lista se llene de tachados.
> Componente: `ServiceList.jsx`.

## Qué dice la guía que aplica

- **Servicio cancelado = TACHADO en la lista, con motivo + quién + cuándo** (2026-06-13 /
  ciclo de vida #9). Ya está implementado: la fila queda `line-through`, con "Cancelado por X"
  y la fecha.
- **Contador "N de M servicios cancelados":** línea chiquita gris debajo del estado de la reserva,
  aparece SOLO si hay alguno cancelado; no cambia el color ni el estado de la reserva (2026-06-13).
  Ya existe (`calculateServiciosCanceladosResumen`).
- **El total al pie NO cuenta los cancelados** (2026-06-13). Se mantiene.
- **Estados de solo lectura:** cuando la reserva está En viaje / Finalizada / Perdida / Anulada, los
  botones de escritura de la lista se ocultan (2026-06-22). El filtro de "ver cancelados" es de solo
  lectura (no escribe nada), así que se muestra igual en esos estados.

## Decisiones tomadas (Gastón, 2026-07-05)

- **P6-A:** los cancelados van **escondidos por defecto**; control "Ver también los cancelados (N)"
  los muestra. Cuando se muestran, se ven tachados con motivo + quién + cuándo (como hoy).
- **P7-A:** el control va **arriba de la lista, al lado del título "Servicios"**.
- **P8-A:** el historial de estados va en un enlace **"Ver historial"** por servicio, que despliega
  los pasos (estado + fecha + quién) **en línea**.

## Diseño final aprobado

**Cancelados escondidos por defecto**, con el control arriba, y el historial detrás de un enlace
"Ver historial" que se despliega en línea.

```
Cabecera de la solapa Servicios:
┌──────────────────────────────────────────────────────────────────────────────┐
│ Servicios Contratados                    [ Ver también los cancelados (2) ▾ ]  │
│                                                   [ + Agregar Servicio ]        │
├──────────────────────────────────────────────────────────────────────────────┤
│ Tipo    Descripción         Fecha        Estado        Precio      Acciones     │
│ 🏨      Hotel Maitei        10–15/07     Confirmado    $205.000    Editar …     │
│         Operador: Despegar  · Ver historial ▾                                   │
│ ✈️      AR 1234 EZE–MIA     20/07        Solicitado    US$ 450     Editar …     │
└──────────────────────────────────────────────────────────────────────────────┘

Al tocar "Ver también los cancelados (2)" aparecen las filas tachadas (como hoy):
│ 🚌      Traslado aeropuerto  10/07       Cancelado     ~~$12.000~~                │
│         Cancelado por María el 08/07 · Motivo: el cliente no lo quiso            │

Al tocar "Ver historial ▾" en un servicio (P8-A), se despliega debajo, en línea:
│         ├─ 04/07  Solicitado al operador — por Juan                              │
│         ├─ 06/07  Confirmado por el operador — por Juan                          │
│         └─ 08/07  Cancelado — por María · Motivo: el cliente no lo quiso         │
```

Notas de coherencia:
- El control "Ver también los cancelados (N)" reusa el mismo conteo que el contador ya existente
  (nunca dos números que puedan diferir).
- El historial se abre **en línea, debajo del servicio** (nunca ventana flotante — regla dura
  "el modal me parece horrible").
- La línea "Cancelado por X" de la fila **se mantiene** aunque exista el historial (es el resumen
  rápido); el historial es el detalle completo, opcional.

## Estados de la pantalla

- **Sin cancelados:** el control "Ver también los cancelados" **no aparece** (igual que el contador).
- **Sin historial disponible** (servicio recién creado, un solo estado): el enlace "Ver historial"
  puede no aparecer o mostrar un único paso; a definir con el dato que exponga el backend.
- **Cargando / error:** los que ya tiene la solapa Servicios.

## Qué NO hay que hacer

- NO contar los cancelados en el total al pie (sigue como está).
- NO cambiar el estado ni el color de la reserva por tener servicios cancelados.
- NO mostrar el historial en una ventana flotante.

## Dependencia técnica (no es decisión de UX)

- El **historial de estados por servicio** (P8) necesita que el backend exponga la lista de pasos
  (estado, fecha, quién, motivo) por servicio. Hoy solo se proyectan `workflowStatus`, `cancelledAt`
  y `cancelledByUserName`. Sin ese dato, P8 se limita a lo que ya hay (opción P8-C).
