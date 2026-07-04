# 2026-07-04 (2da sesión) — La prueba del limbo y el hallazgo del "adelanto a cuenta"

Sesión sin código nuevo: se verificó el deploy de la madrugada, se acompañó la
prueba manual del arreglo del limbo, y del propio dogfood salió un hallazgo de
producto que quedó investigado y decidido (para construir más adelante).

## 1. El arreglo de anoche funciona

- Los dos envíos de la madrugada (`5c154d7` el arreglo, `28b0970` las docs)
  pasaron todas las pruebas y se desplegaron bien.
- La reserva #F-2026-1025 — la que disparó todo, trabada en "esperando
  reembolso" — **se cerró sola**, exactamente como debía.

## 2. La receta para probar el limbo de punta a punta

Para verificar el arreglo con una reserva nueva, el escenario es: reserva con
factura emitida, **sin ningún pago al operador**, anulada, y la pregunta de la
multa sin contestar. Pasos:

1. Reserva de prueba con un servicio. Editar el servicio y poner su campo
   **Estado = "Confirmado"**. Al confirmar el último servicio, la reserva pasa
   sola de "En gestión" a "Confirmada" (motor automático, no hay botón).
2. (Opcional) Estando Confirmada, editar cualquier cosa para que aparezca el
   cartel de "Dar OK" — sirve para verificar de paso que ese cartel ya no
   persigue a las anuladas.
3. **Emitir factura** al cliente (va a AFIP de verdad: monto chico). No hace
   falta cobrarle.
4. Sin registrar pagos al operador, **Anular**. Sale la nota de crédito y
   aparece "¿El operador te cobró una multa por anular?". **No contestar.**
5. A la mañana siguiente: la reserva tiene que estar **cerrada sola** (el
   barrido corre a la 1–2 AM hora argentina), con la ficha diciendo
   "Anulada — falta decidir la multa del operador" y los dos botones vivos.
6. Cierre: "No cobró nada / devolvió todo" cierra el paso de la multa **en el
   momento**.

Detalle de horarios que confundía: el barrido nocturno corre a las 4/5 AM
**UTC**, que acá son la 1 y las 2 de la madrugada. El deploy del arreglo entró
a las 4:31 AM argentina, por eso la noche del deploy el barrido viejo ya había
pasado y la 1025 recién se cerró en la corrida siguiente.

## 3. El hallazgo: no se puede registrar la seña del cliente

Probando, Gastón se topó con esto: reserva "En gestión", servicios pedidos al
operador pero sin confirmar → el sistema no deja registrar ningún cobro. ¿Y si
el cliente quiere dejar plata de adelanto mientras se gestiona el cupo?

Se investigó con tres miradas (ERP comparado, práctica del rubro, fiscal) y
las tres coincidieron:

- **Lo que está bien**: no poder cobrarle "deuda" al cliente por servicios sin
  confirmar es correcto (modelo prepago puro). Ningún cambio ahí.
- **El agujero real**: falta la figura del **adelanto a cuenta** (lo que SAP,
  NetSuite, Dynamics y Odoo llaman customer deposit / down payment). Hoy esa
  plata termina en un papelito, fuera del sistema.
- **Cuidado con la palabra "seña"**: antes de confirmar el servicio, la plata
  es siempre del cliente (adelanto reintegrable). Una "seña perdible" por algo
  no asegurado expone a la agencia (Código Civil: si falla quien la recibió,
  devuelve el doble).
- **Fiscal (verificado en normativa)**: si el adelanto **no congela el precio**,
  no nace IVA ni obligación de facturar — alcanza un recibo de cobranza. Si
  congelara precio, a una agencia inscripta le nace el IVA en el momento del
  cobro (art. 5, último párrafo, Ley de IVA). Por eso el diseño directamente
  no ofrece congelar precio.

### Decisiones tomadas (Gastón, 2026-07-04)

1. **Se construye después del norte multimoneda** (dólar real BNA + facturas
   USD). Quedó diseñado y documentado para no reinvestigar.
2. El adelanto se puede recibir **desde "En gestión"** (no en cotización ni
   presupuesto).
3. La plata vive en la **cuenta del cliente** (tercer origen del saldo a
   favor, junto a anulación y sobrepago), con una marca de qué reserva la
   motivó. Si la gestión fracasa, ya quedó como saldo a favor reutilizable —
   el circuito que ya existe.

Cuando se construya: pasa por arquitecto, gate de UX y los cuatro reviews, y
la formalidad exacta del recibo se valida con el contador matriculado.
