# 2026-06-12 — Bloque grande de backend: la plata, ARCA y los leads

Gastón pidió arreglar "de un solo saque" tres desconexiones grandes del sistema (cobranza/facturación, cuentas corrientes de clientes, leads) más una auditoría general con ojos de ERP y la revisión de ARCA. Todo backend; el frontend se encara después con el gate de UX.

## Qué se hizo (7 commits en main, todos pusheados)

### 0. Cierre de lo de ayer
- Commiteados los 2 fixes del 2026-06-12 anterior (id feo del saldo a favor + dashboard colgado por BNA) que estaban en el working tree. **El deploy de eso sigue pendiente en el VPS.**

### 1. ADR-023 — Fuente única de la plata (3 tandas, diseñado + revisado 2x + implementado + revisado por tanda)
- **T1 Saldo de cliente unificado**: una sola regla en TODAS las pantallas — el saldo cuenta solo reservas en firme (En gestión / Confirmada / En viaje / A liquidar). Antes había ~5 cálculos divergentes; el "Saldo Actual" grande sumaba hasta canceladas y cotizaciones. **El número visible BAJA a propósito** (decisión del dueño OPS-MONEY-001). El Excel "Cuentas por Cobrar" estaba silenciosamente vacío (leía un campo zombie) y se reescribió por cliente+moneda. El límite de crédito ya no se acepta por API (desaparece de la pantalla en la tanda de front). Fix: editar un cliente ya no borra su tipo de documento.
- **T2 Cobranza desde el libro**: GET /payments/history lee la plata de CashLedgerEntry (la misma fuente que Caja — nunca más difieren). Anulaciones visibles (par que netea 0), pagos a proveedor incluidos (enmascarados sin see_cost, decidido sobre el SourceType crudo), moneda real en cada fila, el puente de sobrepago desaparece por construcción, facturas TODAS visibles marcadas por estado (decisión del dueño). Fix de integridad: restaurar un cobro vuelve a asentar en el libro.
- **T3 Permisos**: Caja → caja.view (Vendedor NO la ve — decisión del dueño); clientes completo → clientes.view/edit, cuenta corriente además cobranzas.view; leads → crm.view/edit (Colaborador queda fuera A PROPÓSITO — decisión del dueño). Ningún seed de rol tocado.

### 2. ADR-024 — ARCA con datos reales (spec fiscal verificada + implementado + revisado)
- **El comprobante ahora dice la verdad sobre el cliente**: DocTipo sale del tipo de documento real (pasaporte ya no sale como DNI argentino; CUIT con dígito verificador validado) y la Condición de IVA del receptor sale del cliente (antes viajaba null → todo el mundo salía "Consumidor Final"; obligatorio por RG 5616). Un solo algoritmo en el repo: `ArcaReceptorResolver`.
- Auditoría: las facturas registran quién las emitió (sellado server-side, no spoofeable) y cuándo recibieron CAE; las NC/ND automáticas también estampan el actor.
- **Vínculo básico cobro→factura**: columna nueva `Payment.LinkedInvoiceId` (migración aditiva `Adr024_M1`), informativo, validado misma-reserva. NO usa RelatedInvoiceId (el review de arquitectura detectó que eso congelaba el cobro por los guards).

### 3. Leads — el circuito cierra (backend)
- Una reserva creada desde un lead (`SourceLeadPublicId`) queda linkeada y marca el lead **Ganado** (regla única compartida con el camino de cotizaciones; no reabre Perdido).
- Convertir lead a cliente: pasa los datos del viaje a las notas del cliente nuevo (no pisa clientes existentes) y mueve Nuevo→Contactado. Nunca marca Ganado (Ganado = venta).
- Formulario público de paquetes: ya no duplica leads (dedup por teléfono normalizado; el input anónimo solo agrega una actividad, jamás modifica campos; respuesta constante sin enumeración).
- Normalizador de teléfono único (webhook WhatsApp + conversión + público).
- Código muerto borrado (CreateQuoteDraftAsync).

## Decisiones del dueño selladas hoy
1. Saldo = solo reservas en firme. 2. Límite de crédito fuera de la vista. 3. Orden: Plata → ARCA → Leads. 4. Vendedor NO ve Caja. 5. Leads = Vendedor y Admin (Colaborador fuera). 6. Historial muestra TODAS las facturas marcadas por estado.

## Decisiones técnicas documentadas (riesgos aceptados)
- **S1 (leads)**: un usuario con reservas.edit puede linkear/ganar un lead vía SourceLeadPublicId sin crm.edit. Aceptado hoy (GUID no adivinable, sin roles de vendedor separados). Reevaluar si se separan roles.
- Deep-link 403: un Vendedor que navegue directo a /cash, o un rol sin clientes.view a /customers/:id/account, ve la página pedir datos y recibir 403 (el router del front no gatea inline). Se pule en la tanda de front.
- Doble-restore concurrente de un cobro: protegido por el índice único parcial de Postgres (InMemory no lo aplica). Mono-usuario → riesgo nulo.
- Reserva CERRADA con saldo impago no aparece como deuda en ninguna pantalla (pre-existente, ahora consistente). Avisado al dueño.
- Cuenta corriente: TotalBalance (neto) puede diferir de CurrentBalance (solo positivos) si hay sobrepago en una reserva en firme — se resuelve al definir la pantalla por moneda en la tanda de front.

## Tests
1394 → **1570/1570** unit verde (89 tests nuevos: saldo único 10, historial desde libro 13, permisos 15, ARCA 43+1, leads 21... y ajustes). Integración Postgres NO corrida (pendiente pre-deploy).

## PENDIENTES
**Para Gastón:**
1. **Deploy en VPS** (git pull + bash scripts/ops/deploy.sh). Corre la migración `Adr024_M1` (aditiva, columna nullable — bajo riesgo). Antes, ideal: correr los tests de integración en el VPS (`bash scripts/ops/run-tests-all.sh`).
2. **Verificar Configuración → ARCA**: que la condición del emisor diga **Monotributo** (el default de fábrica es "Responsable Inscripto" → emitiría tipo B en vez de C).
3. **Contador**: firmar las 8 asunciones de ADR-024 §10 (sobre todo: extranjero con pasaporte; y si la NC debe heredar EXACTAMENTE los datos del cliente de la factura original — hoy re-lee el cliente vivo, pre-existente) + homologación ARCA testing antes de confiar la emisión con receptor real.

**Para el desarrollo (tanda de FRONT, todo con UX gate):**
- Cuenta corriente por moneda + saldo a favor visible (el backend ya manda ReceivableByCurrency/CreditBalanceByCurrency y el front los ignora) + sacar la tarjeta de límite de crédito + sacar CreditLimit del DTO.
- Cobranza: mostrar moneda, anulaciones, pagos a proveedor (subtítulo proveedor), vínculo cobro→factura, estados de factura bien marcados.
- Botón "Crear presupuesto desde lead" cableado a SourceLeadPublicId (hoy llama a un endpoint 410 y pierde el vínculo) + formulario de edición de lead + vendedor asignado/próximo seguimiento + formularios públicos de país que hoy descartan los datos (onSubmitLead vacío) + verificar que el CRM no use dangerouslySetInnerHTML sobre notas/actividades (S2).
- Caja = "este mes" con flecha (pendiente viejo, Gastón lo aprobó el 12/6 a la mañana).
- Pantallas rotas por deep-link 403 (gatear rutas en el router).

**Backend menor pendiente:** ítems de factura sugeridos desde los servicios de la reserva (D3) + Concepto/fechas de servicio del SOAP (D4, espera contador) + CanMisMonExt (D9, espera contador) + columna PhoneNormalized indexada si crece el volumen.
