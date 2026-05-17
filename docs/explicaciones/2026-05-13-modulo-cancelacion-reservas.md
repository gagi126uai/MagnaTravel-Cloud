# Sesión 2026-05-13 — Módulo de cancelación de reservas

Esta es una explicación en simple de qué se decidió en esta sesión. Si querés el documento técnico completo, está en `docs/architecture/adr/ADR-002-cancellation-refund.md`. Acá no hay código ni jerga.

---

## 1. El problema que teníamos

MagnaTravel hoy puede vender un viaje, pero cuando el cliente lo cancela el sistema **no sabe manejarlo bien**. Pasan 3 cosas:

### Problema 1 — La caja queda "rota"

Cuando una reserva se cancela, el sistema emite una **Nota de Crédito** (es como decirle a AFIP "esta factura ya no vale"). Pero cuando vos le devolvés la plata al cliente en mano, **ese egreso no aparece en el Libro de Caja**. Entonces el dinero que sale del bolsillo de la agencia queda "invisible" para la contabilidad.

Ejemplo:
- Vendiste un viaje a $1000.
- Cliente lo cancela. Le devolvés $800 en mano.
- En el Libro de Caja figuran los $1000 que entraron, pero **no figuran los $800 que salieron**.
- Tu caja real tiene $200, pero el sistema dice que tenés $1000.

### Problema 2 — No hay un proceso para la cancelación

La cancelación no es un evento de un segundo. Es un **proceso con 4 momentos**:

- **Momento 0 (T0)**: el cliente te dice "lo cancelo".
- **Momento 1 (T1)**: AFIP te confirma que la Nota de Crédito quedó registrada.
- **Momento 2 (T2)**: el operador (hotel, aerolínea, etc) te devuelve la plata, normalmente con descuento.
- **Momento 3 (T3)**: vos decidís con el cliente qué hacer con esa plata (¿se la das en mano? ¿queda como crédito a favor? ¿se la transferís?).

Hoy el sistema modela "cancelar" como **un solo botón**. No registra los 4 momentos, no sabe en cuál estás, y no sabe si pasó algo raro en el camino.

### Problema 3 — Reglas fiscales no contempladas

Hay reglas del negocio que el sistema no respeta:

- El operador casi **nunca** devuelve el 100% (siempre se queda con algo de penalidad).
- Las retenciones que te hace el operador (de IVA, Ganancias, Ingresos Brutos) **no tienen que descontárselas al cliente** — son crédito fiscal de la agencia.
- El cliente puede dejar la plata como **saldo a favor** y retirarla **en varios pedacitos** a lo largo del tiempo. Sin fecha de vencimiento.
- Si el operador NUNCA devuelve la plata, vos tampoco le devolvés al cliente (regla del negocio).

---

## 2. La solución que decidimos

Construir un **módulo nuevo** dentro del sistema que se llama "Cancelación y Refund". Va a tener:

### 6 tablas nuevas en la base de datos

1. **BookingCancellation** — La cancelación en sí. Una por reserva. Sabe en qué momento está (T0, T1, T2 o T3).
2. **OperatorRefundReceived** — Cada vez que un operador te devuelve plata, se registra acá.
3. **OperatorRefundAllocation** — Cuando el operador te devuelve un cheque que cubre varias cancelaciones, se reparte acá. (Ejemplo: el operador te manda $5000 que cubre 3 cancelaciones de $1000, $2000 y $2000.)
4. **DeductionLine** — Cada descuento que aplica el operador (penalidad, retención, gasto bancario). Una línea por descuento.
5. **ClientCreditEntry** — El saldo a favor del cliente. Vive en la ficha del cliente.
6. **ClientCreditWithdrawal** — Cada vez que el cliente retira plata de su saldo. Puede haber muchos retiros.

### El flujo nuevo

Imaginate vendiste un viaje a $1000:

1. **El cliente cancela** → el sistema crea un `BookingCancellation` y emite la Nota de Crédito por **los $1000 completos** (no por menos).
2. **AFIP confirma la NC** → el sistema espera esta confirmación. Si AFIP tarda, hay un robot que pregunta cada 30 minutos.
3. **El operador te devuelve $800** (te clavó $200 de penalidad) → el sistema:
   - Registra los $800 como ingreso del operador.
   - Marca los $200 como **costo de la agencia** (no como menor saldo del cliente).
   - Crea el saldo a favor del cliente por **$1000** (lo que originalmente facturaste).
4. **El cliente decide qué hacer con sus $1000**:
   - Retirar todo en efectivo (con tope de Ley 25.345).
   - Dejar todo como saldo.
   - Llevarse $600 en efectivo + dejar $400 como saldo para otro viaje.
   - Aplicarlo directo a una nueva reserva.

Cada retiro queda registrado en `ClientCreditWithdrawal` y aparece en el Libro de Caja como egreso real.

---

## 3. Decisiones importantes que tomaste vos

Estas son las que cambian el juego. Las anoto porque las decidiste vos en la sesión, no las decidió un agente.

### Decisión 1 — La Nota de Crédito siempre va por el total

**Confirmaste con tu contador**: cuando emitís la NC, va por los $1000 facturados, **no por los $800 que el cliente realmente recibe**. Los $200 que se quedó el operador son problema entre vos y el operador, no entre vos y AFIP.

### Decisión 2 — Si hay 2 facturas en una reserva, no se puede cancelar

Si una reserva tiene 2 facturas activas (caso raro pero existe), **el sistema rechaza la cancelación** y obliga al Admin a consolidarlas primero. Como vos no usás factura E (exportación), esto no afecta el día a día.

### Decisión 3 — Servicios se llaman entre ellos directo

El sistema **no usa "MediatR"** ni ninguna librería de mensajes. Cuando un proceso necesita avisarle a otro, **se llaman directo** entre ellos (es como hablar por teléfono en vez de mandar carta). Más simple, menos código.

### Decisión 4 — MagnaTravel hoy es Monotributo, mañana puede ser RI

Hoy MagnaTravel está como **Monotributista**. Si en el futuro crece y pasa a **Responsable Inscripto**, el sistema tiene que adaptarse **sin reescribir código**. Cambiás un setting y se acomoda.

Esto es importante porque las retenciones funcionan distinto:
- Monotributo → **nadie te puede retener** (IVA, Ganancias, etc.).
- RI → te pueden retener y vos tenés crédito fiscal.

El sistema lee la condición fiscal de la agencia y ajusta las opciones disponibles en pantalla.

### Decisión 5 — La parte contable se hace al final

Las 10 preguntas que hay que mandarle a tu contador **no bloquean** el desarrollo. Empezamos a codear, los datos quedan registrados, y cuando el contador conteste las preguntas integramos sus respuestas. Las preguntas están listas en `docs/preguntas-contador-cancelacion-refund.md`.

---

## 4. Glosario rápido

| Palabra | Qué significa |
|---|---|
| **ADR** | Documento que registra una decisión técnica importante. ADR-001 es el primero, ADR-002 es el de cancelación. |
| **NC** | Nota de Crédito. Es como una factura al revés: cancela una factura previa. |
| **CAE** | Código que AFIP te da cuando aprueba una factura o NC. Sin CAE, la factura "no existe" para AFIP. |
| **AFIP / ARCA** | La agencia recaudadora argentina. Antes se llamaba AFIP, ahora ARCA (cambió por decreto en 2024). |
| **Aggregate root** | Una tabla principal que "manda" sobre las tablas relacionadas. Si querés modificar un dato, lo hacés a través de esta tabla. |
| **Invariante** | Una regla del negocio que **nunca** se puede romper. Ej: "una factura con CAE no se puede editar". |
| **Outbox** | Un patrón técnico para mandar mensajes confiables entre sistemas. No lo usamos para esto. |
| **Hangfire** | Una librería que corre tareas programadas (tipo "cada 30 minutos hacé X"). |
| **Monotributo / RI** | Dos categorías fiscales argentinas. Monotributo es para chicos, RI para más grandes. Cambia cómo funciona el IVA. |
| **EF Core** | La herramienta que usamos para hablar con la base de datos desde C#. |
| **PostgreSQL** | La base de datos que usa MagnaTravel. |

---

## 5. Qué cambia en el sistema

### Para el vendedor

- Botón nuevo "Cancelar reserva" con un proceso de varios pasos en vez de un solo botón.
- Pantalla donde ve el estado del refund del operador.
- Si la reserva tiene más de una factura, le aparece un mensaje claro: "no podés cancelar, hay que consolidar facturas primero, llamá al Admin".

### Para el cajero / administrativo

- Pantalla nueva para registrar el ingreso del operador.
- Puede dividir un ingreso entre varias cancelaciones.
- Tipifica cada descuento del operador (penalidad, retención, etc.).

### Para el Admin

- Aprueba reembolsos al operador.
- Recibe alertas cuando un refund físico supera el umbral de la Ley 25.345.
- Ve un reporte diario de egresos físicos.

### Para el cliente final

- Puede tener saldo a favor sin fecha de vencimiento.
- Puede retirar el saldo en varios pedacitos.
- Recibe la NC fiscal correcta.

---

## 6. Qué falta hacer (próximos pasos)

Está todo el diseño cerrado. Falta:

1. **Última revisión arquitectural** (10 min) — Un "agente revisor" lee el ADR-002 y confirma que está bien.
2. **Verificación técnica** (30 min) — Un agente de backend verifica que una técnica que vamos a usar (xmin de Postgres) funciona en la versión actual del proyecto.
3. **Programación FC1** (24-34 horas) — Crear las 6 tablas + máquina de estados + tests.
4. **Programación FC2** (14-18 horas) — Conexión con AFIP + el "robot" que revisa NCs cada 30 min + fix del bug de caja.
5. **Programación FC3** (10-14 horas) — Manejo del refund del operador.
6. **Programación FC4** (14-20 horas) — Manejo del saldo del cliente + retiros + Ley 25.345.

**Total estimado: 62-86 horas de trabajo real** (entre 2 y 3 semanas).

En paralelo (cuando puedas):
- Mandarle a tu contador las preguntas del documento `docs/preguntas-contador-cancelacion-refund.md`.

---

## 7. Archivos importantes que se crearon o modificaron hoy

| Archivo | Para qué sirve |
|---|---|
| `docs/architecture/adr/ADR-002-cancellation-refund.md` | El documento técnico completo del módulo. 660 líneas. **Lo leen los devs y agentes, no vos**. |
| `docs/architecture/adr/ADR-001-review-2026-05-12.md` | Documento que materializa el review previo del ADR-001 (uno anterior que estaba pendiente). |
| `docs/architecture/adr/ADR-001-domain-invariants.md` | Le cambié el status a "Cambios Requeridos" porque no estaba cerrado. |
| `docs/preguntas-contador-cancelacion-refund.md` | **Las 10 preguntas para mandarle a tu contador**, en lenguaje simple, agrupadas en 3 mensajes por prioridad. |
| `.claude/agent-memory/software-architect/project_cancellation_redesign_2026_05_13.md` | Documento técnico interno con el rediseño completo. Lo usan los agentes en futuras sesiones. |

---

## 8. Cosas a tener en cuenta

- **Empezar a codear esto te va a llevar varias sesiones**. No esperes terminar en una.
- **La parte contable se hace al final**. Eso significa que vas a poder probar el módulo sin tener todavía la confirmación del contador.
- **El sistema impositivo configurable** (Mono → RI) está apuntado como **Fase IMP** del roadmap. Hay 7 items adicionales para cuando crezcas, pero hoy no urgen.
- **Si en el medio cambia algo grande** (ej: tu contador dice que la NC va por neto en vez de por total), hay que volver al ADR-002 y ajustarlo antes de seguir codeando.

---

## 9. Tu rol en lo que viene

1. **Leer este documento** y avisarme si algo no se entiende.
2. **Decidir** cuándo arrancar FC1 (programación real).
3. **Mandar las preguntas** al contador cuando puedas — sin urgencia, pero antes de salir a producción real con volumen.
4. **Probar** el módulo cuando esté armado (vamos a hacer pruebas de cada momento T0 → T3).
