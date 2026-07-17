# Sesión 2026-07-16/17: ejecución completa del plan de coordinación de anulaciones y cuentas

## Qué pasó

Se ejecutó de punta a punta el plan en tandas (A-D) armado la noche anterior. Gaston
respondió todas las decisiones pendientes al arrancar (eligió la recomendación en
todas) y el resto del día fue construcción encadenada: cada tanda pasó por diseño
(cuando hacía falta), implementación, ronda de revisiones (servidor, pantalla,
exposición de datos y seguridad según correspondiera) y deploy con CI verde.

## Decisiones que tomó Gaston hoy (todas cerradas)

1. **Plata a favor**: SÍ se puede aplicar contra multas, y SÍ hay neteo automático
   en devoluciones (se descuenta todo lo que debe y se devuelve la diferencia).
2. **Cartel de multa cobrada**: con fecha de cobro; "Agregar otro cargo" detrás de
   un "Más ▾"; el Deshacer sigue disponible SIEMPRE (la regla del 14/07 quedó
   vigente — ganó sobre el pedido de esconderlo a los 15 días).
3. **Extracto profesional**: todo adentro del extracto (anuladas con contra-asiento
   y multas como renglones — DEROGA la decisión del 15/07), foto de saldo única por
   moneda, 5 columnas (Documento fusionado), sin tarjetitas repetidas, sin
   antigüedad por ahora, clic en renglón → ficha de la reserva.
3b. **Cartel de multa**: arreglar YA que no veía los cobros (era un desalineo
   preexistente); el Deshacer con saldo aplicado pide revertir primero; el neteo
   descuenta multas Y deudas de viajes (la parte de deudas de viajes quedó como
   fase 2).
4. **Pantalla de plata a favor**: lista de multas y elige el usuario; la aplicación
   queda como renglón en el extracto; previa del neteo tal cual el mockup; se
   devuelve el neto completo sin teclear monto; textos del freno y del recibo tal
   cual lo propuesto.

## Lo deployado (5 pushes, todos con CI verde incluida integración Postgres)

1. **Tanda A** (`839c172`): servicio anulado intocable (candado servidor en
   DeleteGuards — cerraba pérdida de datos real — + sin botones en pantalla),
   renombre Cancelar→Anular en todos los textos de servicios, mensaje de condición
   fiscal que dice CUÁL ficha completar, aviso pasivo en la ficha del operador.
2. **Tanda B** (`e1d147e` + fix de tests `9c54652`): snapshot fiscal server-side
   único — la anulación total ya no acepta condiciones fiscales inventadas por la
   pantalla (buildSnapshotData eliminado); bloqueo INV-118/INV-120; CUITs al
   snapshot; moneda/TC de la factura ancla. Verificado contra PROD: 0 comprobantes
   emitidos con IVA mal; los 14 snapshots históricos quedan como evidencia
   congelada (sin backfill — todos tienen NC emitida).
3. **Tanda C** (`95138cc`): fórmula ÚNICA del pendiente de una ND
   (DebitNoteOutstandingRules/Lookup) — el cartel de la ficha, el listado y la
   cuenta del cliente ahora ven los cobros de verdad; cartel gris con tilde
   "Multa del operador cobrada / Se cobró por completo el DD/MM."; el cálculo del
   Deshacer NO se migró a propósito (acuña plata; espera firma fiscal).
4. **Tanda D1 backend** (`a59cb97`): aplicar saldo a favor contra multas (puente
   interno sin comprobante ARCA, FIFO, gate CAE, tope, misma moneda) + neteo
   atómico en devoluciones (Ley 25.345 sobre el neto) + reversa + guard del
   Deshacer que además cierra un agujero preexistente (deshacer una ND con cobro
   real en efectivo perdía la plata del cliente). El gate de exposición bloqueó 2
   textos (nombre de flag y tokens en inglés) — corregidos y re-aprobado.
5. **Tandas D2 + D1 pantalla** (este commit): extracto profesional (foto de saldo
   con composición por moneda, 5 columnas, saldo corrido global estilo banco,
   anuladas y multas visibles, cierre al pie) + flujos de aplicar saldo a una
   multa y devolver con neteo + freno del Deshacer con "Ir a la cuenta del
   cliente".

## Bugs/incidentes del día y cómo se resolvieron

- El CI de la Tanda B falló DOS veces: la primera porque los tests de integración
  Postgres sembraban reservas sin datos fiscales (el fix nuevo las bloquea — se
  corrigieron los seeds, jamás el fix); la segunda porque un test viejo verificaba
  que el snapshot basura del front se RECHAZARA, y el contrato nuevo lo IGNORA
  (test reescrito al contrato nuevo). Hubo además una caída transitoria de la API
  de GitHub que ensució el diagnóstico.
- Los revisores de arquitectura atraparon en DISEÑO (antes de codear): que la
  reversa propuesta no hubiera funcionado (filtraba por método), que el candado del
  Deshacer necesitaba cubrir cobros en efectivo, que el backfill hubiera reescrito
  evidencia fiscal congelada (se eliminó del alcance), y el desalineo preexistente
  de "cuánto se cobró la multa" (dos fórmulas divergentes desde el 15/07).

## Anotado para tandas futuras (no bloqueante)

- Etiqueta "Multa por cobrar" con $0 en el contexto de reserva (ReservationDebtRules
  sigue mirando el balance congelado) + test multi-operador de IsFullyCollected.
- Fase 2 del neteo: descontar también deudas de reservas VIVAS.
- Cruce de monedas en la aplicación de saldo (TC congelado + diferencia de cambio
  como asiento interno; para RI el IVA de la diferencia de cambio es zona de
  litigio — DAT 31/2003 vs jurisprudencia — gate de contador antes de vender a RI).
- Migrar el cálculo del Deshacer a ND-based (acuña plata — necesita firma fiscal).
- Endurecer carreras concurrentes apply-vs-Deshacer y apply-vs-cobro-real antes de
  multi-usuario (hoy mono-usuario, riesgo teórico).
- Campo backend para distinguir NC de anulación vs NC parcial a nivel renglón del
  extracto (hoy el renglón no dice "(anulación)" porque sería inventar el motivo).
- "CANCELADO"→"ANULADO" también en `CancelReservaModal` título "Cancelar reserva"
  (quedó fuera del alcance de la Tanda A) + status crudos tipo "HK" en el fallback
  del chip de servicios.
- Los mensajes "retry loop exhausted" viejos de FC4 (código muerto preexistente).

## Cómo probar a mano (para Gaston)

1. Intentá borrar un servicio anulado → te frena con explicación.
2. Anulá una reserva con un operador sin condición fiscal → el mensaje te dice qué
   ficha completar (antes inventaba los datos y seguía).
3. Mirá una multa ya cobrada del todo en la ficha → cartel gris con tilde y fecha.
4. Cuenta corriente del cliente → foto de saldo única por moneda + extracto con
   las anuladas y multas adentro, saldo corrido tipo banco.
5. Usar saldo a favor → "Aplicar a una multa" (elegís cuál) y "Devolver por
   transferencia/efectivo" con el descuento automático de multas y la cuenta a la
   vista antes de confirmar.
6. Pendientes previos que siguen: probar registrar la factura del operador
   (hotfix de ayer) y completar la condición fiscal de Santa Catalina para
   destrabar la 1046.
