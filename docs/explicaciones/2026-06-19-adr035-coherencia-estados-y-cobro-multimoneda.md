# 2026-06-19 — ADR-035: coherencia de estados + cobro en moneda real

## El problema de fondo
Gastón venía "apagando incendios": cada arreglo destapaba otra incoherencia. La causa raíz
era que **la regla de "qué se puede hacer en cada estado de la reserva" estaba dispersa en ~5
lugares y la pantalla no la respetaba**, y el cobro multimoneda forzaba pesos cuando la reserva
era de una sola moneda extranjera. No era un bug suelto: era falta de un modelo coherente.

Arrancó además con una sorpresa: **los 14 commits del barrido del 18/06 nunca se habían pusheado**
(quedaron locales), así que Gastón había desplegado código viejo y "no se arreglaba nada". Se
pushearon, redesployó y el barrido quedó OK. Lección: al cerrar, verificar que lo commiteado esté
PUSHEADO.

## La solución: ADR-035 (docs/architecture/adr/ADR-035-*.md)

### Modelo de 3 grupos (validado por Gastón y por investigación de ERPs de turismo)
- **EN ARMADO** (Cotización, Presupuesto, En gestión): servicios/pasajeros/fechas editables libremente.
- **EN FIRME** (Confirmada, En viaje, A liquidar): editar solo con autorización (candado existente); se cobra y se factura.
- **CERRADOS** (Finalizada, Perdido, Cancelada, Esperando reembolso): **todo solo lectura** (bloqueo de raíz; ninguna autorización lo abre).

Una sola política de dominio `ReservaCapabilities.For(status)` devuelve, por acción, `{allowed, reason}`.
El backend la impone como **primera compuerta** (antes del candado de autorización y de los guards
fiscales, que quedan intactos) y el DTO la expone para que la UI apague botones sin reimplementar la regla.

### Qué se arregló
1. **Cobro en la moneda real**: arranca en la moneda de la reserva (no más ARS forzado); link "pagar en
   otra moneda" para el cobro cruzado con tipo de cambio. Aplica en el detalle de la reserva y en la
   bandeja de cobranza (el backend expone `monedaPrincipal`/`porMoneda` en la worklist).
2. **Botones según estado**: facturar/cobrar/editar no se ofrecen donde el estado no corresponde.
3. **Solo-lectura coherente**: servicios + pasajeros + fechas bloqueados en estados cerrados.
4. **En viaje ya no se cancela** (decisión de Gastón; estándar de la industria).
5. **Cancelar sin factura**: si la reserva nunca se facturó, se cancela directo (sin nota de crédito).
6. **Reabrir para facturar** (Finalizada → A liquidar) con motivo obligatorio; el botón aparece solo si
   la finalizada **no** tiene factura (si ya está facturada, no aplica).
7. **Servicios coherentes**: en una Perdida muestran "Anulado", en una Cancelada "Cancelado".
8. **Estética**: fuera los "mensajitos debajo de cada botón" (que Gastón rechazó al verlos) → un solo
   cartel de estado arriba + botones grises pelados; encabezado alineado; chips de plata diferenciados
   del badge de estado (rótulo "Pago:") para que no parezca que hay dos estados.

### Qué confirmó el agente de ERP (turismo)
- Nuestro modelo coincide con el estándar (GDS Amadeus/Sabre/Travelport + back-office Juniper/Dolphin/Tramada).
- **Una reserva tiene UN solo estado de ciclo de vida.** "Finalizada + Facturada + Pagada" son **tres ejes
  ortogonales** (operativo/fiscal/financiero), correcto, como SAP (GBSTK/LFSTK/FKSTK) y Odoo. No es doble estado.
- No dejar reabrir una finalizada **con factura CAE** es correcto: el comprobante es inmutable, se corrige con NC.
- **Las correcciones de plata se hacen desde la cuenta corriente del cliente** (con documentos de ajuste) y la
  reserva refleja el saldo. = próximo bloque grande.

## Estado técnico
- Commits: `8f94adf` (política + cobro multimoneda + cancelar sin factura + reabrir + worklist),
  `724eae9` (candado de servicios + en viaje no se cancela), `45e3210` (pasajeros/fechas solo lectura +
  limpieza visual). HEAD main `45e3210`, pusheado.
- **Sin migración.** Backend unit 2132/2132, front 697/697.
- Cadena de revisión: el lote `8f94adf` pasó architect → architect-reviewer (C1-C6) → backend+security
  (Approved w/comments, hueco legacy de edit/delete cobro cerrado) → frontend-reviewer (5 hallazgos cerrados).
  Los lotes `724eae9` y `45e3210` **quedaron sin review formal** (Gastón cortó las revisiones por sobre-ceremonia).

## Pendiente
1. Gastón: desplegar `45e3210` (git pull + `bash scripts/ops/deploy.sh`; sin migración).
2. Reviews de los lotes `724eae9` y `45e3210`.
3. Próximo bloque: **correcciones de plata desde la cuenta corriente del cliente** que impacten en la reserva.
4. Preguntas finas: ¿"A liquidar" también deja de ser cancelable (el viaje ya pasó)?; "en viaje no cancela"
   por reserva o por servicio; "A liquidar" frena el cobro al cliente o solo lo del operador; camino de vuelta
   de una "cancelada por error".
