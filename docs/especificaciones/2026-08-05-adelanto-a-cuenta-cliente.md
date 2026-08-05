# SPEC PARA FIRMA DE GASTON — no construir

## Adelanto a cuenta del cliente

**Fecha:** 2026-08-05
**Autor:** travel-agency-domain-expert
**Hereda decisiones ya cerradas de Gastón (2026-07-04, NO se reabren en esta spec):**
memoria `adelanto-a-cuenta-cliente-hallazgo-2026-07-04`, decisiones (a) se construye después
del norte multimoneda, (b) se recibe desde En Gestión (no desde cotización/presupuesto),
(c) vive en la cuenta del cliente como saldo a favor, con marca blanda a la reserva.

**Regla del documento:** cada punto trae UNA recomendación (no un menú). Lo fiscal está
marcado aparte y no se da por cerrado — lo cierra un contador matriculado.

---

## 1. El problema, con un ejemplo de todos los días

Un cliente viene a la agencia, todavía no confirmó nada con el operador (vuelo sin emitir,
hotel sin reservar en firme), pero quiere asegurarse el lugar y te dice "te dejo 50 mil pesos
ahora, el resto cuando esté todo cerrado". Hoy, en el sistema, esa reserva está En Gestión y
como ningún servicio está confirmado todavía, la deuda que calcula el sistema es CERO. Si
intentás cargar un cobro contra esa reserva, el sistema lo rechaza (y está bien que lo
rechace: no se puede cobrar algo que todavía no es una venta firme — regla del prepago puro).

El problema es que **hoy no existe ningún lugar donde anotar esa plata que el cliente
efectivamente dejó**. Si el vendedor la recibe igual, queda en un papel, en un WhatsApp o en
la memoria de alguien — sin rastro, sin recibo, sin caja. Eso es un agujero real: la agencia
tiene la plata pero el sistema no se entera.

Esta spec resuelve **solo eso**: cómo se anota, dónde vive, y cómo esa plata se convierte
después en el pago de una venta firme.

## 2. Cómo entra el adelanto

**Recomendación:** se carga desde dos lugares, la misma acción — la ficha del cliente O la
ficha de la reserva En Gestión (igual que hoy "usar saldo a favor" tiene entrada desde las
dos fichas). Nunca desde una cotización o un presupuesto — ahí todavía no hay ni siquiera un
cliente que se comprometió a nada (decisión (b), ya cerrada).

Datos que se cargan:
- Monto y moneda (pesos o dólares, las que ya maneja un cobro común).
- Medio de cobro (efectivo, transferencia, tarjeta — los mismos que ya existen).
- Reserva de referencia: **opcional**. Es una marca blanda ("este adelanto es para tal
  reserva"), no un candado. Si el cliente todavía no sabe para cuál viaje es, se deja vacío.
- Un motivo/nota libre.

Quién lo carga: la misma persona que hoy puede registrar un cobro (vendedor o cajero con
permiso de cobranza). No hace falta un permiso nuevo.

## 3. Dónde vive la plata

**Recomendación:** en el mismo bolsillo de saldo a favor que el cliente ya tiene hoy para
otras dos cosas: la plata que le queda cuando se cancela una reserva y le devuelven de más
(sobrepago). El adelanto es simplemente un **tercer motivo** por el que puede haber plata en
ese bolsillo — no se inventa un bolsillo nuevo ni un circuito paralelo.

Reglas que hereda de ese bolsillo, sin cambios:
- Es por moneda: un adelanto en dólares no tapa una deuda en pesos, y viceversa.
- No tiene vencimiento. El cliente lo puede dejar guardado el tiempo que quiera.
- Cada movimiento (alta, uso, devolución) queda con fecha, monto y quién lo hizo — nada se
  borra, todo queda a la vista con su historia completa.

## 4. Cómo se aplica a una reserva nueva

**Recomendación:** con el mismo botón "usar saldo a favor" que el cliente ya tiene hoy en su
cuenta. Cuando la reserva se confirma y nace una deuda real, el vendedor o cajero elige aplicar
el saldo a favor del cliente contra esa deuda — el sistema ya sabe hacer esto: agarra el saldo
más viejo primero, respeta la moneda, y nunca aplica más de lo que la reserva realmente debe.

Si el adelanto tenía la marca blanda "es para la reserva X", el sistema **sugiere** esa reserva
como destino, pero no la aplica solo — el vendedor/cajero siempre confirma antes de que la
plata se mueva. El sistema propone, la persona decide.

## 5. Qué comprobante se emite al recibirlo

**Recomendación (con reserva fiscal, ver más abajo): recibo de cobranza interno**, el mismo
documento en PDF que ya se emite hoy para cualquier cobro. **Nunca una factura, nunca un
voucher.** Todavía no hay una venta firme detrás de esa plata — facturar algo que puede no
llegar a concretarse sería un error fiscal y un dolor de cabeza si el cliente después no
confirma nada.

> **ALERTA — esto no está fiscalmente firme.** Una sesión anterior (2026-07-04) investigó el
> encuadre (que un anticipo que no fija el precio final no genera IVA, y que un recibo alcanza
> sin factura) pero eso fue un hallazgo de trabajo, no una validación profesional. **Antes de
> construir esto, hay que pasarlo por `travel-agency-accountant-argentina`** para confirmar
> IVA, monotributo vs responsable inscripto, percepciones provinciales y la forma exacta del
> recibo. El sistema no se presenta como la autoridad fiscal — eso lo firma un contador.

## 6. Si el cliente se arrepiente (devolución)

**Recomendación:** se usa el mismo mecanismo que ya existe para devolver saldo a favor —
efectivo o transferencia, con salida de caja y rastro completo. Se aplica el mismo tope legal
que ya rige cualquier devolución en efectivo (Ley 25.345): por encima de cierto monto, tiene
que ser por transferencia, no en mano. No hace falta inventar un circuito de devolución nuevo.

## 7. Qué se ve en la cuenta corriente del cliente

**Recomendación:** una línea más en el extracto que el cliente y el vendedor ya ven hoy, con
fecha, monto, moneda, medio de cobro y (si la tiene) la reserva de referencia. Al lado de las
líneas que ya existen por cancelación o por sobrepago — es el mismo extracto, un origen más
en la lista, no una pantalla aparte.

## 8. Casos que se resuelven solos (no hace falta preguntarle a Gastón)

- **El cliente deja un adelanto y la reserva se termina cancelando antes de aplicarlo a nada**:
  el saldo sigue vivo en la cuenta del cliente, ya sin atarse a esa reserva cancelada, listo
  para usarse en cualquier otra. No se pierde.
- **El cliente aplica solo una parte del adelanto**: el resto queda disponible en el bolsillo
  para la próxima vez — el sistema ya soporta usar un saldo a favor de a partes.
- **El negocio no se concreta nunca** (la reserva nunca llega a confirmarse): la plata sigue
  siendo del cliente, disponible sin vencimiento para cualquier reserva futura suya.
- **El adelanto está en una moneda distinta a la de la reserva final**: no se puede cruzar
  (un adelanto en dólares no paga una deuda en pesos directo) — mismo candado que ya existe
  hoy para no mezclar monedas por accidente.

## 9. Preguntas para Gastón

1. **¿Se puede dejar un adelanto sin ninguna reserva En Gestión detrás (plata "suelta" en la
   cuenta del cliente)?**
   Recomendación: NO, en esta primera versión. La decisión ya cerrada dice que nace desde En
   Gestión — dejarlo así evita abrir una puerta para cobrar plata sin ningún trámite iniciado
   detrás.

2. **¿Hay un tope de cuánto se puede dejar de adelanto (por ejemplo, no más del 30% de lo que
   se estima que va a costar el viaje) o queda libre?**
   Recomendación: libre, sin tope. Es plata del cliente que él decide dejar; no es una condición
   de venta que la agencia le imponga.

3. **¿El criterio fiscal (recibo, no factura) se re-confirma con el contador ANTES de firmar
   esta spec, o se firma el negocio ahora y lo fiscal se cierra en paralelo?**
   Recomendación: se firma el negocio ahora (no depende de lo fiscal), pero **no se pasa a
   construcción** hasta que `travel-agency-accountant-argentina` confirme el punto 5. El
   diseño técnico (`software-architect`) tampoco arranca sin esa confirmación, porque afecta
   cómo se modela el comprobante.

## 10. Siguiente paso si Gastón firma

En este orden, ninguno arranca sin esta spec firmada:
1. `travel-agency-accountant-argentina` — confirma el encuadre fiscal del punto 5.
2. `software-architect` — diseña cómo se guarda el cuarto origen del saldo a favor (reusando
   el mismo mecanismo que ya existe para cancelación, sobrepago y multa deshecha) y cómo se
   arma la pantalla de alta.
3. `software-architect-reviewer` — lo desafía antes de construir.
4. `backend-dotnet-senior` + `frontend-senior` (con gate UX de `ux-ui-disenador` antes de
   tocar cualquier pantalla).
5. Reviewers de cierre según el routing habitual (`backend-dotnet-reviewer`,
   `frontend-reviewer`, `security-data-risk-reviewer`, `data-exposure-reviewer`).

---

### Qué se leyó para escribir esta spec (evidencia, no invención)

- Memoria `adelanto-a-cuenta-cliente-hallazgo-2026-07-04` (punto de partida, re-confirmada
  contra el código actual, no repetida a ciegas).
- `src/TravelApi.Domain/Entities/ClientCreditEntry.cs` — el bolsillo de saldo a favor ya
  soporta 3 orígenes distintos con el mismo patrón; el adelanto es un cuarto origen, no un
  mecanismo nuevo.
- `src/TravelApi.Application/Interfaces/IClientCreditService.cs` — aplicar a otra reserva y
  devolver el saldo ya existen como operaciones completas.
- `src/TravelApi.Domain/Entities/Payment.cs` y
  `src/TravelApi.Application/Interfaces/IPaymentService.cs` — el recibo en PDF ya existe como
  pieza para cualquier cobro.
- `src/TravelApi.Infrastructure/Services/PaymentService.cs` (`CreatePaymentAsync`) — hoy un
  cobro exige una reserva con deuda real (`EnsureCollectable`); confirma el hueco exacto que
  esta spec cierra a nivel de negocio.
- `src/TravelApi.Domain/Reservations/ReservationDebtRules.cs` — confirma que el bloqueo actual
  es correcto por diseño (venta firme + saldo real) y que esta spec no debe tocarlo.
- `docs/estandares/2026-07-22-constitucion-producto-v1.md` — reglas F-6 (rastro), F-7 (prepago
  puro), F-9 (cobrable = venta firme + saldo real), F-11 (saldo a favor reutilizable, sin
  vencimiento), F-15 (lo fiscal lo firma un contador), P-21 (el sistema sugiere, no decide),
  PR-11 (contempla RI/Mono/Exento).
