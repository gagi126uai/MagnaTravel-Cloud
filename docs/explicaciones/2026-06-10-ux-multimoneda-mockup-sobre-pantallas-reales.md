# 2026-06-10 — UX de multimoneda: gate de diseño y mockup sobre las pantallas reales

## En simple (para Gastón)

Arrancamos el diseño de las pantallas de plata para que el sistema muestre **pesos y
dólares** (el motor por debajo ya estaba hecho; faltaba cómo se ve y se usa).

Pasó esto, en orden:

1. **Decidiste 8 cosas** sobre cómo querés ver las dos monedas (eligiendo siempre la
   opción recomendada): saldo de la reserva, lista de servicios, cómo se cobra, cuenta
   corriente del cliente y del proveedor, caja y reportes.
2. Frenaste antes de construir: querías **ver el dibujo** primero, y bien — el primer
   dibujo lo había hecho **sin mirar tu sistema real** (metí el cobro dentro de
   "Servicios", ignorando que vos ya tenés un **Estado de Cuenta** con los cobros y
   varias solapas dentro de la reserva).
3. Miré tus pantallas reales y **rehíce el dibujo encima de lo que ya usás**, y de paso
   le propuse mejoras a tu Estado de Cuenta.

**El dibujo para mirar:** `docs/ux/mockups/2026-06-09-multimoneda.html` (se abre en el
navegador, doble clic).

**Quedaron 5 preguntas tuyas pendientes** (están marcadas dentro del HTML y te las dejé
en el chat con mi recomendación de cada una). Cuando las respondas, paso todo a tu guía
de UX y armamos la construcción.

## Reglas duras del negocio/contador respetadas en todo el diseño

- Pesos y dólares **siempre separados**, nunca un total "convertido todo a una moneda".
- El sistema **nunca muestra "diferencia de cambio"** (eso lo reconoce el contador en el
  cierre, fuera del sistema).
- Si una reserva o un cliente es de **una sola moneda**, se ve **igual que hoy** (sin el
  doble bloque).
- **Enmascarado de costos**: quien no tiene permiso de ver costos no ve Inversión/Costo
  ni la cuenta corriente del proveedor; sí ve Saldo a cobrar y Recaudado.

## Las 8 decisiones aprobadas (2026-06-09)

1. **Tira de plata de la reserva**: con dos monedas, "Saldo a cobrar" se desdobla en
   "($)" y "(US$)" lado a lado, cada uno con su "de $X presupuestado"; Recaudado e
   Inversión también por moneda.
2. **Lista de servicios**: cada fila con la moneda pegada + total por moneda al pie.
3. **Ficha de cobro**: pasa de ventana modal a **ficha en línea** dentro de la página
   (como la carga de servicios). El recuadro de tipo de cambio aparece **solo** cuando
   el pago cruza de moneda (paga en una, baja deuda de otra); ahí pide tipo de cambio +
   fuente + fecha (obligatorios) y muestra "Se cancelan US$ X de la deuda".
4. **Cuenta corriente del cliente**: dos saldos arriba ("Debe en $/US$") + una sola
   lista de movimientos con la moneda en cada fila.
5. **Cuenta corriente del proveedor**: mismo formato que el cliente.
6. **Caja/arqueo**: dos cajas separadas (pesos y dólares).
7. **Reportes**: dos columnas lado a lado; rankings de deuda separados por moneda.

## Las 5 preguntas pendientes para Gastón (con recomendación)

1. Tira de plata: ¿4 cajitas en fila, o juntar las dos monedas dentro de cada cajita?
   (Rec.: juntar dentro de cada cajita.)
2. Cobro cruzado en el historial: ¿una fila + renglón aclarador, o dos filas? (Rec.: una
   fila con renglón aclarador.)
3. Factura de un servicio en dólares: ¿se muestra en US$ o en pesos? **Toca lo fiscal →
   confirmar con el contador aparte; no frenar el resto.**
4. Textos de botones: ¿unificar "Registrar Cobranza"/"Confirmar Pago" a un solo término?
   (Rec.: unificar a "cobro/cobranza".)
5. Fila de reserva con dos monedas en la cuenta corriente: ¿pegadas en un renglón o una
   abajo de la otra? (Rec.: una abajo de la otra.)

## Pantallas reales sobre las que se diseñó (para el implementador)

- `ReservaSummaryStrip.jsx` — tira de plata (Saldo a cobrar / Recaudado / Inversión).
  `formatCurrency` está hardcodeado a ARS: hay que pasarlo a moneda explícita.
- `ReservaDetailPage.jsx` — solapa "Estado de Cuenta" (botón Registrar Cobranza →
  `PaymentModal`; tabla Historial de Cobranzas y Comprobantes; tabla Documentos Fiscales
  AFIP). Solapas: Servicios · Pasajeros · Historial · Estado de Cuenta · Vouchers ·
  Documentos.
- `PaymentModal.jsx` — la ventana de cobro actual (Monto/Método/Notas) → se reemplaza por
  ficha en línea con moneda + imputación + bloque de tipo de cambio.
- `CustomerAccountPage.jsx` — cuenta corriente del cliente (tarjetas Ventas/Cobrado/
  Saldo + solapas Reservas/Pagos/Facturación).
- Cuenta corriente del proveedor, caja/tesorería y reportes: análogas (el calce fino de
  caja y reportes quedó marcado "a confirmar antes de construir").

## Estado del trabajo

- **Sin cambios de código.** Solo se creó/reescribió el mockup HTML y esta explicación.
- HEAD sigue en `8356bd1`. **Deploy todavía pendiente** (el VPS corre una versión
  anterior; los fixes de pantalla y la base de multimoneda no están desplegados).
- Multimoneda backend base (capas 1-2) hecho y verde; faltan capas 4/5/6 (registro de
  pago con moneda+TC en API, exponer por-moneda con enmascarado see_cost, consumidores).
- **Próximo paso**: Gastón responde las 5 preguntas → `ux-ui-disenador` escribe las
  decisiones en `docs/ux/guia-ux-gaston.md` y arma la spec → recién ahí se construye
  (frontend capa 7 + backend capas 4/5/6).
