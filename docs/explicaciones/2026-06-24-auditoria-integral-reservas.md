# 2026-06-24 — Auditoría integral del módulo Reservas (lo visible y lo invisible)

Pedido de Gastón: dejar de parchar lo que se ve y revisar **todo el funcionamiento** de reservas, con criterio, incluyendo lo que no se ve. Se hizo: 4 mapas estructurales (estados, plata-display, deshacer, capacidades) + 6 auditorías de correctitud (plata-math, fiscal, integridad/concurrencia, proveedor/comisiones, ciclo/casos-borde) + Fase 1 ejecutada. Todo por lectura de código real (file:line).

## Meta-conclusión (con criterio)
El backend tiene **los ladrillos correctos casi en todos lados** (la matemática de la plata es sana; la política de capacidades es fuente única; el sobre fiscal a AFIP arma bien IVA/multimoneda). Los incendios vienen de **3 huecos estructurales**:
1. **Hechos derivados calculados en varios lugares con bases distintas** → divergen (ejes de plata, "facturado neto", estado).
2. **Operaciones de plata/estado/fiscal sin transacción envolvente** y recálculos que no se disparan tras anular → datos a medias o rancios.
3. **El front se inventa reglas en vez de confiar en el backend** → botones fantasma/faltantes y el callejón de "anular".

---

## BLOQUEANTES (bugs reales; plata/fiscal/datos)

**B-FISCAL-1 — La NC de anulación se cuenta doble en el cuadre.** "Facturado neto"/"Disponible para facturar" dan negativo o inflado tras anular (la NC resta como viva Y la factura original se excluye). Surface: detalle, chip, franja por moneda, extracto. `ReservaService.cs:214,313,2086,2229` (filtro IsLive); `InvoiceService.cs:1382,2505`; el test `ReservaInvoicingCuadreCalculatorTests.cs:47-58` enmascara el bug. El guard `hasLiveCae` (`ReservaService.cs:1522`) ya excluye NCs bien — falta propagar ese criterio al cuadre. No deja pasar un CAE prohibido (alimenta aviso, no bloqueo duro), pero los números fiscales se muestran mal.

**B-FISCAL-2 — El envío a AFIP de la factura principal puede emitir CAE duplicado.** `AfipService.ProcessInvoiceJob` no tiene idempotencia/recuperación (sí la tiene el camino de NC parcial). Si AFIP autoriza pero se pierde la respuesta (crash/timeout), queda CAE real en AFIP pero PENDING local; una re-ejecución del job saca un segundo CAE. Mitigante: el reintento humano rechaza PENDING; requiere re-ejecución por máquina (garantía de Hangfire no confirmable desde código).

**B-FISCAL-3 — Anulación multi-operador emite una sola ND.** La emisión lee montos a nivel reserva, no por línea de operador. Dos multas (op A ARS, op B USD) → una sola ND. El código lo documenta como pendiente. Recomendado: bloquear ND automática si >1 operador con multa confirmada hasta tener emisión por línea.

**B-PROV-1 — Al anular la reserva entera, la deuda con el operador queda STALE (inflada).** `BookingCancellationService.ConfirmAsync` no recalcula deuda de proveedor (sí lo hace el cancelar-un-servicio en `:419`). La deuda agregada sigue contando servicios anulados hasta que algo más toque al proveedor o un Admin recalcule a mano. La **comisión del vendedor** también queda colgada hasta que el job de AFIP procese la NC.

**B-INTEG-1/2/3 — Operaciones de plata/estado no son "todo o nada".** Cobro+conversión de sobrepago, retiros de saldo a favor con egreso de caja, y la cancelación (NC+estado+servicios) encadenan varios SaveChanges (varios disparados por la auditoría, que commitea) **sin transacción**. Crash a mitad: sobrepago duplicado, plata fuera de caja con operación a medias, o reserva anulada sin NC. Las áreas nuevas (FC4 saldo a favor, bolsillo de crédito con xmin+CHECK, emisión fiscal) **sí** están protegidas. (Concurrencia pura = latente sin usuarios simultáneos; atomicidad = puede dispararse con 1 solo usuario si se corta el proceso.)

**B-INTEG-4 — Job nocturno promueve "En viaje"/cierra con saldo rancio.** Re-lee solo Status, no Balance; si un cajero cambia un cobro entre la query y el commit, el job promueve una reserva ya no paga (viola candado de pago duro). `Reserva` no tiene xmin.

**B-CICLO-1 — Reserva puede quedar Confirmada con 0 pasajeros.** Borrar pasajeros tras confirmar no está bloqueado y el motor no mira pasajeros. El voucher sí está protegido (exige titular), pero el estado queda inconsistente.

**(YA ARREGLADO HOY, sin desplegar)**
- "Pagada" sin cobrar: causa raíz (señal de actividad usaba venta cotizada en vez de confirmada) corregida en los 3 sitios + tests.
- "Confirmada" que se movía/regresaba sola: eliminada la regresión automática y el bounce nocturno; ahora queda Confirmada + marcada "revisar cambios"; gate de viaje por la marca.
- Botones de cobro que fallaban en Finalizada; plata mal mostrada (saldo a favor en rojo); chip que decía Saldado/Pagada sin confirmación.

**(DIAGNOSTICADO, pendiente arreglo)**
- Anular en gestión: callejón sin salida. El botón solo va por el camino de NC (que exige factura); la baja simple existe pero no tiene botón. Criterio de Gastón: sin cobros→baja directa; con cobros→saldo a favor; con factura→NC.

---

## IMPORTANTES
- Job nocturno: sin guard de solapamiento (doble ejecución duplica auditoría); una fila mala aborta toda la corrida (las fases siguientes no corren).
- KPIs: "cobrado este mes" se infla con saldo a favor aplicado (no filtra AffectsCash); "pendiente de facturar" resta montos de distinta moneda.
- Pago al operador por servicio: no valida que la moneda del pago coincida con la del servicio, ni tope por servicio → el casillero "pagado al operador" puede mentir en multimoneda.
- Fechas imposibles (fin antes que inicio) no validadas en Aéreo/Traslado/Paquete/Genérico (sí Hotel/Asistencia). Se cuela a cabecera y voucher.
- Front se inventa reglas: "FullyInvoiced" oculta "Emitir factura" y congela vouchers contra ADR-037; "Anular" del encabezado mira `canCancel` sin `canAnnul`; capacidad fantasma `canDelete` que el backend no manda.
- Dos definiciones de "facturado neto" (bandeja global vs detalle) → inconsistentes tras anular.
- Factura A sin chequeo de coherencia letra↔condición IVA del receptor (riesgo de rechazo AFIP).
- Reprogramar viaje permite mover el itinerario al pasado sin avisar.

## DECISIONES PARA GASTÓN (no son bugs)
- Fiscal (se resuelven por investigación informando a Gastón): clasificación de multa, fecha del comprobante (hoy `DateTime.Now` local), monto de ND como fuente de verdad, condición IVA default, exento/margen.
- Gate de "En viaje" por cambio de precio sin revisar (Fase 1): hoy también frena el avance — confirmar.
- Concepto único de "Deshacer reserva" en la UI (necesita gate UX).

## PLAN DE FONDO (en fases, cada una entregable y verificada)
- **Fase 1 (HECHA, sin desplegar):** Confirmada no se mueve sola + raíz "Pagada".
- **Fase 2 — Plata y fiscal correctos:** NC doble-conteo; recalcular deuda proveedor + comisión al anular; envolver en transacción cobro/retiro/cancelación; idempotencia del envío a AFIP; bloquear ND multi-operador.
- **Fase 3 — Deshacer de una vez + reglas de datos:** concepto único de anular (sin callejón); guard de último pasajero; validar fechas imposibles; aviso reschedule al pasado.
- **Fase 4 — La pantalla obedece al backend:** sacar reglas inventadas del front; una sola verdad de la plata en todos los carteles; job nocturno con guard de solapamiento + saldo fresco + tolerancia a fila mala.
