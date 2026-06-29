# 2026-06-28/29 — Norte del producto + endurecer la cuenta del operador (Paso A desplegado)

## El cambio de fondo: de "apagar incendios" a un norte

El dueño (Gastón) venía sintiendo el desarrollo como "de nunca acabar": muchos roadmaps, bugs sueltos, sin dirección. Se paró la rueda y se fijó un **norte escrito** (memoria `producto-vision-norte-2026-06-28`):

- **Qué es:** un ERP para agencias de viajes minoristas, con IA externa (Gemini/DeepSeek), catálogo + web pública, y un **chatbot de WhatsApp con IA que ayuda al vendedor a cerrar ventas** (la IA atiende e intenta cerrar, el vendedor cierra), con pata fiscal AFIP. Dogfood en su agencia + vender a otras.
- **El orden (lo que saca el "infinito"):** (1) AHORA endurecer el core; (2) la IA que vende (el diferencial); (3) IA sola + web.
- **"Listo para vender v1"** = core sólido + primera IA que ayuda a vender.

Regla núcleo que pidió Gastón y quedó como guía permanente: **ser imparcial, juzgador, ponerme en contra si hace falta, y contrastar TODO con lo que está realmente programado** (memoria `feedback-imparcial-juzgador-contrastar-codigo`).

## Auditoría crítica de la reserva (la buena noticia)

Tres miradas (ERP / dominio / arquitectura) contra el código real (`docs/explicaciones/2026-06-28-auditoria-critica-core-reserva.md`): **el core está más sano de lo que se sentía** — la mayoría de los bloqueantes viejos ya estaban arreglados. Lo que queda como hueco real: la cuenta del operador tras cancelar, anular reservas multi-operador, `InvoicingMode` (reseller/intermediario) modelado pero muerto, sin PDF de presupuesto, sin plan de cuotas, sin liquidación de comisión.

## Primera tanda: terminar la cuenta del operador

La desconexión real reservas↔operadores es la **economía de la cancelación** del lado del operador (`docs/explicaciones/2026-06-28-analisis-desconexion-reservas-operadores.md`). Se decidió presentar la cuenta como **dos números separados que siempre cuadran**: "Le debo: $X" / "El operador me tiene que devolver: $Y" (la multa retenida es un tercer concepto, un costo).

Hecho y **desplegado** (HEAD `3af4adb`):
- **Fase 1** — confirmar la multa ahora descuenta esa multa del reembolso esperado (antes lo inflaba; había un comentario que mentía). Por moneda, sin mezclar.
- **Paso A** — el cierre **"el operador no cobró multa / devolvió todo"** (estado `PenaltyStatus.Waived`, no emite Nota de Débito, audit obligatorio) + **reversión Admin auditada** + el **botón en pantalla** (dos caminos claros, panel teal distinto del naranja de la ND, "deshacer" solo-Admin) + `capabilities.operatorPenaltyOutcome` + gateo de los botones por permiso.
- **Blindaje:** ~32 mensajes de error del flujo de cancelación que filtraban estados/IDs/operaciones internas → reescritos a español de negocio, con un **test-guardián** que revienta si reaparece una fuga (incluido el GUID, que fue el incidente fundacional).

Todo pasó por revisión (frontend / backend / seguridad / data-exposure) con verdicto Approved.

## Lo que falta (Pasos B y C) — bloqueado a propósito

Los dos números en el extracto + conciliar la devolución recibida en la cuenta del operador. La architecture-review frenó el diseño inicial (la "garantía de cuadre" era una tautología; hay una fuga real: el `SupplierCreditReconciler` convertiría un "por cobrar" en saldo a favor gastable, hay que arreglarlo ANTES; mantener el extracto de caja intacto + un bloque "circuito de cancelación" separado). Y necesita **4 definiciones del contador** (multa pass-through vs cargo propio = reseller/intermediario por operador; si el cierre sin multa necesita registro fiscal; naturaleza contable de la multa retenida; multimoneda multa≠pago≠reembolso) + la elección de Gastón de la perilla reseller/intermediario.

## Cómo retomar

Gatillo **"seguí el norte"** → leer la brújula (`producto-vision-norte-2026-06-28`) y continuar: si hay respuestas del contador, Pasos B/C; si no, otro item del core backlog.
