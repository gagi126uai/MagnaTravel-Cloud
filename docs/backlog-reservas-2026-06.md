# Backlog Reservas / Cancelaciones / Facturación — 2026-06-24

Documento vivo. Numeración estable. Estado deploy: HEAD `3260fe3`, pusheado, SIN desplegar.
Cola migraciones: `Adr036_M2` + `Adr037_M1` + `Adr038_M1` (+ las nuevas de este lote).

---

## A. YA HECHO Y SUBIDO (falta desplegar)
- A1. Botón "Confirmar multa del operador" arreglado. `eb08e35`
- A2. Botón "Anular" visible con factura/cobros + cartel "Factura en proceso". `8b441bf`/`5d6fd62`
- A3. Admin hace todo directo con rastro de auditoría. `46ef957`
- A4. Al anular, servicios → "Cancelado". `46ef957`
- A5. Chip "Esperando reembolso". `46ef957`
- A6. Moneda de la multa del operador (registro). `46ef957`
- A7. La UI no muestra internos en errores + regla en memoria. `3260fe3`

## B. ESTADOS TERMINALES (respuestas de Gastón aplicadas)
- B1. ~~Finalizada no vuelve atrás~~ → **DEJAR COMO ESTÁ** (se puede deshacer cierre). Cerrado.
- **B2. (HACER)** Servicios → "Finalizado" cuando la reserva termina (Closed).
- **B3. (HACER)** Reserva terminal = solo lectura para vouchers y documentos (ver/reimprimir sí; agregar/modificar no).
- B4. ~~Anular factura más visible~~ → **DEJAR en la solapa Facturas**. Cerrado.

## C. INVESTIGADO (ERP) — decisiones
- **C1. (HACER, con arquitecto + contador)** Anular reserva con varias facturas → emitir 1 NC por factura (todo-o-nada). Gastón: "continuá por ahí".
- C2. Facturar solo si 100% saldada → **NO se cambia** (se mantiene ADR-037 + aviso de deuda). Gastón: "bueno" (acepta la recomendación). Cerrado.

## D. GRANDE / CON DISEÑO (Gastón: OK los cuatro → necesitan gate UX/arquitecto)
- D1. Pantalla de la pata del proveedor (destraba "Esperando reembolso").
- D2. Proveedores como Cuentas por Pagar (extracto por moneda, vencimientos, datos de pago).
- D3. PDF de la Nota de Débito (dónde verlo).
- D4. Limpiar "Solicitudes"/aprobaciones (dormidas + bandeja clara).

## E. FISCAL — RESOLVER POR INVESTIGACIÓN (NO contador; Gastón 2026-06-24)
Decisión: no se gatea en el contador; se resuelve investigando ARCA/ERP en internet, con cita.
- E1. ND pass-through: ya investigado (compatible como recupero; en Mono va en C sin IVA). OK seguir.
- E2. Multi-factura: 1 NC por factura, A→NC A, cada NC en la moneda de su factura (para C1).
- E3. Momento de emisión: ARCA = al percibir parcial o al concluir, lo que pase primero → se mantiene ADR-037 (facturar desde Confirmada). C2 cerrado.
- E4. Conectar moneda de la multa a la emisión/FX real de la ND (follow-up técnico).

## F. TÉCNICOS (los maneja el dev, no son decisiones de Gastón)
- F1/F2/F3: detalles internos (moneda en retenciones, atomicidad, validar migraciones al desplegar). Yo me ocupo.

## G. REGLAS DE ESTADO PRE-VENTA Y PERDIDA (nuevas, de Gastón — HACER)
- **G1. Perdida (Lost):** NO genera deuda con proveedores; sin "operador impago" ni avisos; sin carga de documentos.
- **G2. Cotización/Presupuesto:** sin "operador impago" ni avisos; NO genera deuda con proveedores (aún no se concretó).
- **G3. Cotización/Presupuesto:** NO se puede cancelar servicios (nada concretado).
- **G4. BUG:** presupuesto que el cliente aceptó + agregar 1 pasajero + "cliente aceptó" → "no se pudo actualizar el estado de la reserva". Arreglar el mensaje (confuso: aún no es "reserva", es presupuesto) Y el problema de fondo.
- **G5. Reprogramar viaje:** solo desde Confirmada en adelante; NO en Cotización/Presupuesto (ni terminal).
- **G6. (HACER) Caducidad de Presupuesto y Cotización:** duración configurable por plataforma (ej. 7/10/20/31 días, por separado presupuesto y cotización). Cuando pasan los días sin avanzar a nada → pasa solo a **Perdido**. Necesita: setting de días (presupuesto + cotización) + job que caduca + transición a Lost. (Config UI = gate UX.)

---

## H. BUGS / REVISAR MAÑANA (Gastón 2026-06-24, fin del día)
- **H1.** Una reserva NUEVA en "En gestión" muestra **"Pago: pagada"** sin haberse registrado ningún pago. El estado de cobro está mal calculado/mostrado cuando no hay cobros.
- **H2.** Al **emitir la factura no se ve de forma clara** (claridad/UX del flujo de emisión). Revisar cómo se muestra.
- **H3.** **"Confirmar multa del operador" se sigue viendo en estados que no corresponden.** Acotar a donde realmente aplica.
- **H4.** **AUDITORÍA GENERAL de AVISOS y "Operador impago":** revisar TODOS los lugares donde aparecen avisos y el aviso de "operador impago" y que NO se muestren cuando el estado de la reserva no lo amerita (pierden lógica). Analizarlas TODAS, no solo las obvias.

---

### HECHO Y DESPLEGABLE (HEAD b7a46a7): A* + lote chico + lote ciclo de vida (B2, B3, G1-G5)
### MAÑANA: G6 (caducidad presupuesto/cotización) + H1, H2, H3, H4 (bugs/auditoría de avisos)
### QUEUE: C1 (multi-factura, arquitecto) · D1-D4 (UX)
### FISCAL: por investigación (no contador), informando a Gastón — ver sección E y memoria no-gatear-contador
