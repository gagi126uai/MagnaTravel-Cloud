/**
 * Lógica pura del flujo de anulación con VARIAS facturas en distintas monedas (ADR-042, 2026-07-01).
 *
 * Cuando una reserva tiene 2+ facturas de venta vivas, anularla emite UNA nota de crédito POR
 * FACTURA (cada una en su propia moneda). Es asincrónico contra AFIP y "todo o nada a nivel
 * ESTADO": si una nota sale y otra falla, la reserva queda "en revisión" (la que salió no se
 * deshace) hasta reintentar la que falta.
 *
 * Estas funciones son puras (sin React, sin llamadas HTTP) para poder testearlas con
 * `node --test` sin bundler. Las usan CancelarReservaInline.jsx (el flujo de anular) y
 * ReservaDetailPage.jsx (la franja "en revisión" cuando la anulación quedó a medias).
 *
 * Fuente de la spec: docs/ux/2026-07-01-anulacion-multifactura.md (6 estados, copy exacto).
 */

// ─── Clasificación de facturas / notas de crédito ────────────────────────────

/**
 * Determina si la anulación es "multi-factura" (2 o más facturas de venta vivas). Con 1 sola
 * factura sigue vigente el flujo mono-factura de siempre (sin freno "¿Seguro?" ni avance por
 * nota): regresión cero, tal como pide la spec.
 *
 * @param {Array<{currency: string, amount: number}>} saleInvoices - BookingCancellationDto.SaleInvoices
 * @returns {boolean}
 */
export function esAnulacionMultiFactura(saleInvoices) {
    return Array.isArray(saleInvoices) && saleInvoices.length >= 2;
}

/**
 * Cuenta las notas de crédito que TODAVÍA no terminaron bien (Pending o Failed). Es "cuántas
 * faltan" para que la anulación quede completa — la usa la franja "en revisión" para el texto
 * "falta emitir N nota(s) de crédito".
 *
 * @param {Array<{status: string}>} creditNotes - BookingCancellationDto.CreditNotes
 * @returns {number}
 */
export function contarNotasFaltantes(creditNotes) {
    if (!Array.isArray(creditNotes)) return 0;
    return creditNotes.filter((nota) => nota.status !== "Succeeded").length;
}

/**
 * Cuenta las notas que ya se resolvieron (Succeeded o Failed, sin importar el resultado). Es el
 * numerador del contador "1 de N" del estado PROCESANDO: cuántas ya terminaron de una forma u otra.
 *
 * @param {Array<{status: string}>} creditNotes
 * @returns {number}
 */
export function contarNotasResueltas(creditNotes) {
    if (!Array.isArray(creditNotes)) return 0;
    return creditNotes.filter((nota) => nota.status !== "Pending").length;
}

/**
 * True si TODAS las notas de crédito salieron bien (éxito total, Estado 3 de la spec).
 * Un array vacío NO cuenta como éxito (nada se emitió todavía).
 *
 * @param {Array<{status: string}>} creditNotes
 * @returns {boolean}
 */
export function todasLasNotasSalieronBien(creditNotes) {
    if (!Array.isArray(creditNotes) || creditNotes.length === 0) return false;
    return creditNotes.every((nota) => nota.status === "Succeeded");
}

/**
 * True si al menos una nota de crédito fue rechazada por AFIP (dispara el cartel "en revisión").
 *
 * @param {Array<{status: string}>} creditNotes
 * @returns {boolean}
 */
export function algunaNotaFallo(creditNotes) {
    if (!Array.isArray(creditNotes)) return false;
    return creditNotes.some((nota) => nota.status === "Failed");
}

/**
 * True si todavía queda alguna nota "emitiendo" (Pending) — mientras esto sea true, el polling
 * del estado PROCESANDO sigue consultando al backend.
 *
 * @param {Array<{status: string}>} creditNotes
 * @returns {boolean}
 */
export function hayNotaPendiente(creditNotes) {
    if (!Array.isArray(creditNotes)) return false;
    return creditNotes.some((nota) => nota.status === "Pending");
}

// ─── Formateo de moneda y texto (copy dinámico, sin sumar $ + US$) ───────────

/**
 * Símbolo corto de la moneda para armar frases como "Nota de crédito en $".
 * Regla dura multimoneda (2026-06-09): ARS y USD nunca se mezclan ni se suman.
 *
 * @param {string} currency - "ARS" | "USD" | otro código ISO
 * @returns {string}
 */
export function etiquetaMonedaSimbolo(currency) {
    if (currency === "USD") return "US$";
    if (currency === "ARS") return "$";
    return currency || "";
}

/**
 * Arma el resumen de monedas para el aviso previo y el "¿Seguro?": "(una en $ y una en US$)"
 * cuando hay 2 monedas distintas, o "(N facturas en US$)" cuando todas son la misma moneda.
 *
 * @param {Array<{currency: string}>} items - facturas o notas de crédito (solo importa `currency`)
 * @returns {string} - Ej: "(una en $ y una en US$)" | "(3 facturas en US$)" | ""
 */
export function formatearResumenMonedas(items) {
    if (!Array.isArray(items) || items.length === 0) return "";

    // Contamos cuántos items hay de cada moneda, preservando el orden de primera aparición
    // (para que "una en $ y una en US$" salga en el mismo orden que la lista de abajo).
    const conteoPorMoneda = new Map();
    for (const item of items) {
        const moneda = item.currency;
        conteoPorMoneda.set(moneda, (conteoPorMoneda.get(moneda) || 0) + 1);
    }

    const monedas = [...conteoPorMoneda.keys()];

    if (monedas.length === 1) {
        return `(${items.length} facturas en ${etiquetaMonedaSimbolo(monedas[0])})`;
    }

    const etiquetaCantidad = (cantidad) => (cantidad === 1 ? "una" : String(cantidad));
    const partes = monedas.map(
        (moneda) => `${etiquetaCantidad(conteoPorMoneda.get(moneda))} en ${etiquetaMonedaSimbolo(moneda)}`
    );
    return `(${partes.join(" y ")})`;
}

/**
 * Estado 0 (P1=A): texto del aviso previo con la lista de facturas.
 * Copy exacto de la spec, con N y el resumen de monedas armados dinámicamente.
 *
 * @param {Array<{currency: string}>} saleInvoices
 * @returns {string}
 */
export function construirTextoAvisoMultiFactura(saleInvoices) {
    const cantidad = Array.isArray(saleInvoices) ? saleInvoices.length : 0;
    const resumen = formatearResumenMonedas(saleInvoices);
    return (
        `Esta reserva tiene ${cantidad} facturas emitidas ${resumen}. Al anular se emite una ` +
        "nota de crédito por cada factura, cada una en su moneda."
    );
}

/**
 * Estado 1 (P2=A): texto del cartel "¿Seguro?" antes de emitir las notas.
 * N = cantidad de notas = cantidad de facturas (una NC por factura).
 *
 * @param {Array<{currency: string}>} saleInvoices
 * @returns {string}
 */
export function construirTextoConfirmacionMulti(saleInvoices) {
    const cantidad = Array.isArray(saleInvoices) ? saleInvoices.length : 0;
    const resumen = formatearResumenMonedas(saleInvoices);
    return (
        `Se van a emitir ${cantidad} notas de crédito en AFIP ${resumen}. Una vez emitidas no ` +
        "se pueden deshacer."
    );
}

/**
 * Nombre legible de UNA nota de crédito para la lista de avance: "Nota de crédito en $".
 *
 * @param {string} currency
 * @returns {string}
 */
export function etiquetaNotaCredito(currency) {
    return `Nota de crédito en ${etiquetaMonedaSimbolo(currency)}`;
}

/**
 * Ícono + texto de estado de UNA nota de crédito para la lista de avance (Estados 2 y 4).
 *
 * @param {string} status - "Pending" | "Succeeded" | "Failed"
 * @returns {{icono: string, texto: string}}
 */
export function estadoVisualNota(status) {
    if (status === "Succeeded") return { icono: "✔", texto: "emitida" };
    if (status === "Failed") return { icono: "✗", texto: "no salió" };
    return { icono: "⏳", texto: "emitiendo…" }; // Pending (o estado desconocido: degradación segura)
}

/**
 * Estado 3 (P4=A): encabezado del cartel de éxito total.
 * "Se emitieron N notas de crédito (una en $ y una en US$)."
 *
 * @param {Array<{currency: string}>} creditNotes
 * @returns {string}
 */
export function construirTextoExitoMulti(creditNotes) {
    const cantidad = Array.isArray(creditNotes) ? creditNotes.length : 0;
    const resumen = formatearResumenMonedas(creditNotes);
    return `Se emitieron ${cantidad} notas de crédito ${resumen}.`;
}

/**
 * Estado 4 (P5=A): encabezado del cartel "en revisión" cuando la anulación quedó a medias.
 * Para el caso más común (1 salió, 1 falló) usa el copy EXACTO de la spec. Para reservas con
 * más de 2 facturas (poco común) generaliza el mismo mensaje sin perder el sentido.
 *
 * @param {Array<{status: string}>} creditNotes
 * @returns {string}
 */
export function construirTextoEncabezadoRevision(creditNotes) {
    const exitosas = Array.isArray(creditNotes) ? creditNotes.filter((n) => n.status === "Succeeded").length : 0;
    const fallidas = Array.isArray(creditNotes) ? creditNotes.filter((n) => n.status === "Failed").length : 0;

    if (exitosas === 1 && fallidas === 1) {
        return (
            "La reserva quedó EN REVISIÓN: una nota de crédito salió bien y la otra no. " +
            "La que salió no se deshace."
        );
    }

    return (
        `La reserva quedó EN REVISIÓN: ${exitosas} de ${exitosas + fallidas} notas de crédito ` +
        "salieron bien. Las que salieron no se deshacen."
    );
}

/**
 * Estado 5 (P6=A): texto de la franja naranja al reabrir una reserva con la anulación a medias.
 * "En revisión — anulación a medias, falta emitir N nota(s) de crédito."
 *
 * @param {number} cantidadFaltante
 * @returns {string}
 */
export function construirTextoFranjaEnRevision(cantidadFaltante) {
    const etiqueta = cantidadFaltante === 1 ? "nota" : "notas";
    return `En revisión — anulación a medias, falta emitir ${cantidadFaltante} ${etiqueta} de crédito.`;
}

// ─── Bloque 4 "anulación a medias" (2026-08-19, docs/ux/2026-08-19-...) ──────
// La franja "en revisión" (ReservaDetailPage) hoy solo se enciende con UNA condición del
// backend (canRetryCreditNotes), que mezcla dos causas reales: una nota RECHAZADA por ARCA
// (hay motivo textual) y una nota ATASCADA sin trabajo en curso (el job murió, sin motivo
// porque nunca llegó a fallar). Las dos siguen necesitando el botón "Emitir la nota que
// faltó" (Rama A, alarma). Lo nuevo es la Rama B: una nota Pendiente con el job TODAVÍA
// trabajándola (nadie tiene que hacer nada, se resuelve sola) — antes no mostraba nada.

/**
 * Rama B (nueva, "en curso"): true cuando la anulación tiene alguna nota de crédito
 * Pendiente, NINGUNA Rechazada, y el backend NO habilita el reintento todavía (el job de
 * emisión la sigue trabajando en este mismo instante). Es el caso "no hace falta que hagas
 * nada, en un rato lo vas a ver reflejado solo" — sin alarma, sin botón.
 *
 * Mutuamente excluyente con la Rama A: si `bc.canRetryCreditNotes` es true, esto siempre
 * da false (esa reserva ya muestra la alarma, no las dos franjas a la vez).
 *
 * @param {{ canRetryCreditNotes?: boolean, creditNotes?: Array<{status: string}> }|null} bc
 *   BookingCancellationDto tal como lo devuelve cancellationsApi.getByReserva.
 * @returns {boolean}
 */
export function esAnulacionEnCursoTranquila(bc) {
    if (!bc) return false;
    if (bc.canRetryCreditNotes) return false;
    return hayNotaPendiente(bc.creditNotes) && !algunaNotaFallo(bc.creditNotes);
}

/**
 * Arma la línea de UNA factura para la lista por factura de la Rama A (alarma). Reemplaza a
 * `NotasCreditoProgressList` en ESE contexto puntual: ahí la unidad es "una factura, con su
 * propio resultado", no "una moneda" (una reserva puede tener 2 facturas en la MISMA
 * moneda, así que agrupar por moneda como hace `estadoVisualNota` no alcanza acá).
 *
 * Formato EXACTO de la spec (P-13: el motivo de ARCA se muestra TAL CUAL lo manda el motor,
 * nunca parafraseado):
 *   - Succeeded            → "{label} — nota de crédito emitida"
 *   - Failed con motivo    → "{label} — la nota no salió. ARCA respondió: «{motivo}»"
 *   - Failed sin motivo    → "{label} — la nota no salió." (defensivo: nunca se inventa
 *     una cita que el motor no mandó)
 *   - Pending (atascada — la única forma de llegar Pending a la Rama A es que el job murió)
 *                          → "{label} — la nota todavía no salió."
 *
 * @param {{ originatingInvoiceComprobanteLabel?: string, status: string, arcaErrorMessage?: string|null }} nota
 *   Un item de BookingCancellationDto.creditNotes.
 * @returns {{ icono: "✓"|"✗", texto: string }}
 */
export function describirNotaPorFactura(nota) {
    const label = nota?.originatingInvoiceComprobanteLabel || "Factura";

    if (nota?.status === "Succeeded") {
        return { icono: "✓", texto: `${label} — nota de crédito emitida` };
    }

    if (nota?.status === "Failed") {
        if (nota.arcaErrorMessage) {
            return { icono: "✗", texto: `${label} — la nota no salió. ARCA respondió: «${nota.arcaErrorMessage}»` };
        }
        return { icono: "✗", texto: `${label} — la nota no salió.` };
    }

    // Pending (atascada): sin motivo porque nunca llegó a fallar, nunca se inventa uno.
    return { icono: "✗", texto: `${label} — la nota todavía no salió.` };
}

/**
 * Convierte el diccionario de saldo a favor por moneda (ClientCreditByCurrency del backend) en
 * una lista ordenada de entradas para que el componente las formatee con formatCurrency. Filtra
 * las monedas en cero (si no hubo cobros que se conviertan en saldo a favor, no se muestra nada).
 *
 * @param {Record<string, number>} clientCreditByCurrency
 * @returns {Array<{currency: string, amount: number}>}
 */
export function entradasSaldoAFavor(clientCreditByCurrency) {
    if (!clientCreditByCurrency || typeof clientCreditByCurrency !== "object") return [];
    return Object.entries(clientCreditByCurrency)
        .filter(([, amount]) => Number(amount) > 0)
        .map(([currency, amount]) => ({ currency, amount: Number(amount) }));
}
