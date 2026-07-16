# Sesión 2026-07-16 (parte 2): bugs del checklist de Gaston + plan de coordinación de anulaciones

## Qué pasó

Gaston probó lo deployado y encontró varios problemas. En vez de parchar uno por uno
("apagar incendios"), se investigó todo en paralelo con 4 agentes de solo lectura
(3 de código + 1 experto en ERPs con internet) y se armó un plan coordinado en tandas.

## Arreglado y deployado en la misma sesión

**Facturas del operador rotas (500 en cada intento)** — commit `7623810`.
- Síntoma: "Ocurrió un error inesperado" al registrar la factura del operador.
- Causa (confirmada en los logs de prod vía ops-diagnostico): los atributos de
  validación de `SupplierInvoiceCreateRequest` estaban como `[property: Required]`
  en records con constructor primario. ASP.NET exige que vayan en el PARÁMETRO;
  con `property:` tira `InvalidOperationException` al validar el body, antes de
  llegar a la lógica. Tercera aparición del mismo patrón (2026-06-06 catálogo).
- Arreglo: 3 records de facturas del operador + 1 caso latente
  (`PublicPackageLeadRequest`, formulario público del catálogo).
- Prevención: `RecordValidationAttributePlacementTests` — guardián por reflexión
  que escanea TODOS los DTOs y hace fallar la suite si el patrón vuelve a entrar.
- Descartado por evidencia antes de llegar al fix: migraciones sin aplicar (las 4
  están en prod, verificado por SQL de solo lectura), mismatch de payload, N+1.

## Diagnósticos cerrados (para las tandas de mañana)

1. **Cartel de multa nunca se cierra**: el estado "Done" solo mira si la ND tiene
   CAE, nunca si se cobró — aunque el dato de cobro ya existe en otro rincón del
   backend. Fix diseñado: exponer `IsFullyCollected` y mostrar el cartel como
   cerrado (gris/tilde) con el Deshacer a perfil bajo sus 15 días.
2. **"No pudimos determinar la condición fiscal"**: la devolución parcial hace lo
   CORRECTO (lee el dato real; el operador estaba con datos fiscales pendientes).
   La anulación TOTAL nunca falla porque INVENTA los datos (hardcodea RI/Consumidor
   Final en `penaltyPayload.js`). Riesgo fiscal silencioso → tanda B.
3. **Borrar servicio cancelado**: bug real con pérdida de datos posible — un
   cancelado sin confirmación previa se borra físicamente dejando huérfanas las
   líneas de cancelación. El backend no chequea el status al borrar.
4. **Informe ERP** (SAP/Odoo/NetSuite): extracto = fecha/doc/debe/haber/saldo
   corrido, por moneda, anulados visibles, sin botones; multas = partidas abiertas;
   crédito aplicable contra cualquier deuda; neteo en devolución = estándar;
   cancelado nunca se borra. Hallazgos propios: el extracto por reserva ya existe
   en backend sin pantalla, y las reservas anuladas quedan fuera del extracto del
   cliente (por eso la multa "flota").

## El plan (aprobado en concepto, arranca mañana)

- **A** (directo): servicio cancelado intocable (UI + servidor), renombre
  Cancelar→Anular (~25 textos), mensaje fiscal que diga cuál ficha falta.
- **B** (fiscal de fondo): snapshot fiscal server-side único para ambos caminos.
- **C** (UX gate): multa cobrada se ve cerrada.
- **D** (decisiones de Gaston): 1) saldo a favor contra multas + neteo en la
  devolución (recomendado SÍ, quedó SIN RESPONDER); 2) extracto profesional +
  rediseño de la cuenta corriente del cliente (va con mockups).

Memoria de retomo: `proximo-retomo-2026-07-16-plan-coordinacion-anulaciones.md`.
