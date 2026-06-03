# Inventario de la reserva — ¿qué está simple y conectado, y qué falta?

**Fecha:** 2026-06-03. Recorrido read-only de cada operación de adentro de la reserva, contra la vara de "terminada en serio": **(1) Simple · (2) Conectada · (3) No se rompe · (4) Cerrada.**

> Método: lectura de estructura + endpoints (no exhaustiva línea por línea). Donde digo "a verificar", hay que confirmarlo al construir. La integridad de borrado se apoya en `DeleteGuards`/`MutationGuards`, que el análisis previo encontró sólidos.

Semáforo: ✅ bien · ⚠️ flojo / pesado · ❌ falta / roto

---

## 1. Servicios (agregar / editar / eliminar)
- **Simple:** ⚠️ **El más pesado.** El formulario (`ServiceFormModal`) tiene **2141 líneas**: 6 tipos (Hotel/Aéreo/Traslado/Paquete/Asistencia/Genérico), cada uno con su sección, selector de tarifario y precios manuales. Mucho para el usuario.
- **Conectada:** ✅ al agregar/editar/eliminar refresca la reserva (saldo y capacidad se actualizan).
- **No se rompe:** ✅ (base) — hay guardas (no borrar servicio ya facturado). *Mensajes claros: a verificar.*
- **Cerrada:** ⚠️ funciona, pero la complejidad esconde bordes.
- **Veredicto:** el principal candidato a **simplificar**.

## 2. Pasajeros (agregar / editar / eliminar)
- **Simple:** ✅ un formulario (`PassengerFormModal`, 312 líneas), de un paso, con **búsqueda por documento en AFIP** que autocompleta. Razonable.
- **Conectada:** ✅ refresca; la búsqueda AFIP conecta con el padrón.
- **No se rompe:** ✅ (base).
- **Cerrada:** ✅ / ⚠️ parece completo (estados a verificar).
- **Veredicto:** probablemente **el más sano**. Toque liviano.

## 3. Cobranzas (registrar / editar / eliminar) + comprobante de pago
- **Simple:** ✅ `PaymentModal` chico (158 líneas).
- **Conectada:** ✅ refresca el saldo (reforzado hoy con la fuente única). Emite/anula recibo de pago.
- **No se rompe:** ✅ guardas sólidas (no borrar un pago que ya tiene recibo).
- **Cerrada:** ✅.
- **Veredicto:** **sano.** Verificar y listo.

## 4. Comprobantes (emitir)
- Desde la reserva los comprobantes **solo se VEN** (lista + PDF). Para **emitir** hay que ir a Cobranzas (módulo aparte, tu hexágono).
- ⚠️ **Hoy es un callejón:** ves las facturas pero no podés emitir desde la reserva.
- **Decisión tuya:** ¿querés una **puerta** "Facturar" desde la reserva que abra el módulo ya parado en esta reserva (sin fusionar), o está bien que viva solo en Cobranzas?

## 5. Vouchers (emitir / editar / eliminar + externos)
- **Operaciones hoy:** generar interno ✅ · subir externo ✅ · emitir ✅ · rechazar ✅ · revocar (≈ eliminar) ✅.
- ❌ **Editar un voucher: NO existe.** ❌ **Editar/modificar un externo: NO existe.** → falta lo que pediste.
- **Simple:** ⚠️ `ReservaVoucherTab` tiene **1049 líneas** y varios modos/modales (generar/subir/emitir/rechazar/revocar). Complejo.
- **Veredicto:** **falta EDITAR** (interno y externo), y conviene **simplificar**.

## 6. Documentos (agregar / editar / eliminar)
- **Operaciones hoy:** agregar (subir) ✅ · descargar ✅ · eliminar ✅.
- ❌ **Editar / renombrar: NO existe.** → falta lo que pediste.
- **Simple:** ✅ `ReservaDocumentsTab` (334 líneas), acotado.
- **Conectada:** ✅ carga por reserva.
- **Veredicto:** **falta EDITAR (renombrar).** El resto, sano.

---

## Resumen en una mirada

| Operación | Simple | Conectada | No se rompe | Cerrada | Qué falta |
|---|---|---|---|---|---|
| Servicios | ⚠️ pesado | ✅ | ✅ | ⚠️ | simplificar (2141 líneas) |
| Pasajeros | ✅ | ✅ | ✅ | ✅ | nada grande |
| Cobranzas | ✅ | ✅ | ✅ | ✅ | nada grande |
| Comprobantes | — | ⚠️ callejón | ✅ | — | decisión: ¿puerta desde la reserva? |
| Vouchers | ⚠️ pesado | ✅ | a verif. | ⚠️ | **falta editar** (interno+externo) + simplificar |
| Documentos | ✅ | ✅ | ✅ | ⚠️ | **falta editar/renombrar** |

## Orden sugerido (vos decidís)
1. **Documentos** — agregar editar/renombrar. Chico, cierre rápido, gana impulso.
2. **Vouchers** — agregar editar (interno + externo) y simplificar la pantalla.
3. **Servicios** — el grande: simplificar el formulario de 2141 líneas.
4. **Pasajeros / Cobranzas** — verificar que estén redondos (toque liviano).
5. **Comprobantes** — decidir si va una "puerta" desde la reserva.

Cada uno se cierra de verdad (las 4 cosas), de a uno, mostrándote cada paso.
