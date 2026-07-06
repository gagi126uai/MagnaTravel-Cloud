# Cambio 2 — Búsqueda escrita por reserva y/o cliente en el filtro del listado

> Estado: **APROBADO por Gastón (2026-07-05)** — respondió P4-A y P5-A (las recomendadas).
> Sin preguntas abiertas. Listo para frontend-senior.
> Reglas en la guía: sección "Listado de reservas: pestaña Anuladas aparte + buscador global (2026-07-05)".
> Pedido textual de Gastón (2026-07-05): "búsqueda de reservas por reserva y/o cliente ESCRITO en el filtro".

## Hallazgo importante (verificado en el código)

**Buena parte de esto YA existe y YA funciona.** El listado ya tiene un casillero de búsqueda
("Buscar reservas…", arriba a la derecha), y el backend ya busca por **tres cosas a la vez**
(`ReservaService.ApplyReservaSearch`):

- **número de reserva** (`NumeroReserva`),
- **nombre / título de la reserva** (`Name`),
- **nombre del cliente / pagador** (`Payer.FullName`).

O sea: escribir "García" ya encuentra las reservas de la familia García, y escribir "1042" ya
encuentra la #R-1042. El pedido de Gastón, en lo básico, **ya está cumplido**.

## Decisiones tomadas (Gastón, 2026-07-05)

1. **Alcance (P4-A):** al escribir en el casillero, el buscador busca en **TODAS las reservas,
   cualquier estado y cualquier fecha**, ignorando la pestaña y el período que estén puestos. Al
   limpiar la búsqueda, la lista vuelve a respetar la pestaña y el período elegidos.
2. **Texto del casillero (P5-A):** pasa a decir **"Buscar por N° de reserva o cliente…"** (reemplaza
   "Buscar reservas…").

## Qué dice la guía que aplica

- La búsqueda vive en la barra de filtros del listado que ya existe (no se inventa un lugar nuevo).
- Regla general 2026-06-05: nada de leyendas largas ni textos aclarativos; el casillero se explica
  solo con su texto gris de adentro.

## Diseño final aprobado

**Búsqueda global** + **texto que nombra las dos cosas**. Cuando el vendedor escribe en el casillero,
se busca en TODAS las reservas (cualquier estado, cualquier fecha); los filtros de pestaña y período
quedan como están, pero el resultado de la búsqueda no se recorta por ellos.

```
Barra de filtros del listado (ya existe):
┌─────────────────────────────────────────────────────────────────────────────┐
│ Por [creación ▾]  [Mes a Mes ▾] ◀ Julio 2026 ▶     🔍 Buscar por N° de       │
│                                                        reserva o cliente…     │
└─────────────────────────────────────────────────────────────────────────────┘

Al escribir "García" (P4-A, búsqueda global):
┌─────────────────────────────────────────────────────────────────────────────┐
│ 🔍 García                                            Mostrando resultados de  │
│                                                      todas las reservas       │
├─────────────────────────────────────────────────────────────────────────────┤
│ #R-1042  Fam. García   🚫 Anulada     12/06/26   …                            │
│ #R-0988  Fam. García   ✅ Finalizada  02/01/26   …                            │
│ #R-1310  García, Juan  ⚙️ En gestión  hoy         …                           │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Estados de la pantalla

- **Mientras tipea:** ya hay debounce (300 ms); no se dispara en cada tecla.
- **Sin resultados:** el vacío que ya existe ("No se encontraron reservas. Intentá ajustar los
  filtros de búsqueda.").
- **Error / base caída:** el `DatabaseUnavailableState` que ya existe.

## Qué NO hay que hacer

- NO agregar un segundo casillero separado "por cliente" (con uno solo alcanza; el backend ya cruza
  las tres cosas). Sería más ruido.
- NO construir el cambio de alcance (P4) sin la respuesta: si Gastón elige B, no se toca nada del
  backend y solo cambia el texto (P5).

## Dependencia técnica (no es decisión de UX)

- Si Gastón elige **P4-A (global)**: cuando hay texto de búsqueda, el backend debe **ignorar la
  pestaña y el período** (o el front manda una vista "todas" + sin filtro de fecha). Es un ajuste
  chico; no cambia ninguna decisión de UX.
