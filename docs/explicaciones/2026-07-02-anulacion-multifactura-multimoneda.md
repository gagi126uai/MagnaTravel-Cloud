# 2026-07-02 — Anular reservas con varias facturas en distintas monedas (ADR-042)

## Qué problema había

Gastón intentó anular la reserva #F-2026-1031, que tiene dos facturas de verdad (una en pesos y una en dólares), y el sistema lo frenaba con un cartel: "Esta reserva tiene más de una factura emitida. Por ahora no se puede anular toda la reserva de una vez". Ese freno estaba puesto a propósito porque la funcionalidad no existía: había quedado anotada en el norte como "investigada, no construida". En esta tanda se construyó entera.

## Qué hace ahora el sistema

- **Al anular una reserva con varias facturas, emite una nota de crédito por cada factura, cada una en su moneda** (la de dólares en dólares con su tipo de cambio congelado, la de pesos en pesos), cada una asociada a su comprobante en AFIP.
- **Todo-o-nada**: la reserva queda Anulada solo cuando TODAS las notas salieron bien. Si una sale y otra falla, la que salió no se toca (una nota con CAE es un hecho fiscal), la reserva queda "En revisión" con una franja naranja, y hay un botón para **reintentar solo la que faltó** (seguro: no puede duplicar).
- **El saldo a favor del cliente queda en la moneda de la factura que pagó** (decisión con respaldo legal: arts. 765/766 del Código Civil post-DNU 70/2023 — una deuda en dólares se debe en dólares; hay fallos que obligaron a agencias a devolver USD aunque el cliente pagó pesos). Pago a cuenta sin imputar → queda en la moneda en que se pagó. Plata: el saldo es siempre lo efectivamente cobrado, nunca lo facturado.
- **La pantalla** (aprobada por Gastón, 7 decisiones): aviso con la lista de facturas y montos por moneda, confirmación "¿Seguro?", avance nota por nota (✔/⏳, "1 de 2"), éxito detallado con el saldo a favor por moneda, falla parcial en naranja con el motivo de AFIP y reintento, y franja "En revisión" al volver a entrar.

## Lo difícil de atrás (en criollo)

- **Candado anti-carrera**: si las dos notas vuelven de AFIP al mismo tiempo, antes podían "pisarse" y dejar la reserva trabada para siempre. Ahora hay un candado en la base de datos que las pone en fila. Se probó con tests de concurrencia REAL contra Postgres (que además destaparon y corrigieron un bug del propio candado).
- **La herramienta de administrador** que fuerza confirmaciones ahora trabaja nota por nota y no puede cerrar una anulación incompleta.
- **AFIP en dólares**: se verificó contra el esquema oficial de AFIP que el formato de lo que mandamos es correcto (quedó un test automático vigilándolo). El dato de "se cobra en moneda extranjera" ahora se guarda en cada factura y la nota de crédito lo copia exacto (si no coincidieran, AFIP puede rechazar).
- **Ningún mensaje técnico llega a la pantalla**: los motivos de rechazo de AFIP pasan por un filtro (texto claro pasa, ruido técnico se reemplaza por un mensaje amable), verificado por el gate de exposición de datos, que primero bloqueó por una fuga real y después aprobó.

## Números del cierre

- Suites: backend 3150/3150 unit + 207/207 integración Postgres; frontend 1680/1680 + build.
- Reviews: arquitectura (rechazó primero, aprobó rev.3), backend (rechazó, aprobó re-review), seguridad (bloqueó, aprobó re-review), frontend (aprobó con condiciones, aplicadas), data-exposure (bloqueó, aprobó re-review). Total: 4 bloqueantes de diseño + 4 de código + 1 de seguridad + 1 de exposición, todos corregidos y re-verificados.
- Migración `Adr042_M1` validada Up → Down → Up contra Postgres 16 local antes del deploy.
- Los 62 tests de integración que aparecieron rotos eran fixtures viejos desactualizados (el CI no los corría); quedaron al día.
- Commit `023eca9`, pipeline 28630829777 success, deploy al VPS automático.

## Incidente post-deploy (resuelto en minutos)

El primer deploy (`023eca9`) rompió la página de reserva en producción: `Cannot access 'ie' before initialization` al abrir cualquier reserva. Causa: el chequeo nuevo de "anulación en revisión" quedó ubicado ANTES de donde se declara la variable `reserva` en el componente — en desarrollo no explota, en el bundle minificado sí (TDZ). Lo doloroso: el mismo archivo ya tenía documentado un incidente idéntico (effect de ADR-031) dos líneas más abajo. Hotfix `277d54e` (mover el effect después del hook), verificado contra el bundle compilado antes de pushear. Datos intactos (error solo de render).

**Lección grabada en memoria**: 4 reviews + 3 suites verdes no alcanzan para cambios de pantalla — hay que correr la app REAL construida antes de deployar. Mejora anotada: ESLint `no-use-before-define` en TravelWeb.

## Qué queda pendiente

1. **Dogfood**: Gastón anula la #F-2026-1031. La NC en dólares contra AFIP real es la homologación de facto del circuito USD.
2. **Cuenta del operador** (tarea siguiente, decisiones ya tomadas por Gastón): reembolso muestra la multa, multa gestionable desde el operador, operador por servicio clickeable, $0 explicado, "Reembolsos operador" sale del menú → solapa en la ficha. Gate UX primero.
3. **Fugas viejas de mensajes técnicos** (fuera de esta tanda, anotadas): bandeja de NDs pendientes, notificación de anulación fallida, y el patrón `ex.Message` en Draft/Confirm del mismo controller.
4. **Extracto del cliente**: sigue sin commitear en el árbol (backend aprobado, frontend con review pendiente).
