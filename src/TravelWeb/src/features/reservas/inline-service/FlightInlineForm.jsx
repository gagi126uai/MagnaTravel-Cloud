/**
 * Formulario de Aéreo dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador de ruta/aerolínea · Operador/consolidador · Ida · Vuelta · Pasajeros
 *   Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Aeropuerto/ciudad de salida y llegada · Horarios del vuelo (Sale/Llega ida y vuelta) ·
 *   PNR · Números de ticket · Escalas · Equipaje
 *
 * Obra "PDF completo" (2026-08-13, corrección Round 3 14/08 — el dueño eligió que los
 * horarios vivan DENTRO de "Más detalles", no en la zona principal): los 4 horarios viajan
 * por OutboundDepartureTime/OutboundArrivalTime/ReturnDepartureTime/ReturnArrivalTime,
 * NUNCA por departureTime/arrivalTime — ver la nota "SEMÁNTICA INTOCABLE" en
 * buildFlightPayload (ServiceInlineCard.jsx) para el porqué.
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio total × pasajeros (si se ingresa precio unitario).
 * NOTA: El aéreo puede venderse como precio cerrado (consolidado) o por pasajero;
 * usamos precio total de venta directo (como el modal viejo) para no asumir unitarización.
 */

import { useState, useEffect } from "react";
import { Plane, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { MoneyInput } from "../../../components/ui/MoneyInput";
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
    resolverOperadorSugeridoParaProductoNuevo,
    sanitizarCantidadPositiva,
} from "./inlineServiceFormHelpers";
import { useVariantPriceSuggestion } from "./useVariantPriceSuggestion";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic";
import { VariantSuggestionHint } from "./VariantSuggestionHint";
import { useSeleccionPendienteDelTipo } from "./useSeleccionPendienteDelTipo";

// D13 (spec 2026-08-10): campos de fecha de ESTE form, [desde, hasta] — Aéreo tiene ida/vuelta.
const CAMPOS_FECHA_VUELO = ["departureDate", "returnDate"];

// ─── Clases CSS (mismas que HotelInlineForm para coherencia visual — molde ".campo"
// de la maqueta firmada 2026-08-11, con su versión oscura: ver el comentario largo
// en HotelInlineForm.jsx, donde vive el original) ─────────────────────────────
const INPUT_BASE = "w-full py-2 px-2.5 text-[13px] border rounded-[7px] bg-white text-slate-800 focus:outline-none focus:ring-1 focus:border-primary focus:ring-primary disabled:bg-slate-50 disabled:text-slate-400 dark:bg-slate-900 dark:text-slate-100 dark:disabled:bg-slate-800/60 dark:disabled:text-slate-500";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-300 dark:border-slate-600`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50 dark:border-amber-600/70 dark:bg-amber-900/25 dark:text-amber-100`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-300 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default dark:border-slate-600 dark:bg-slate-800/60 dark:text-slate-300`;
const LABEL_BASE = "block text-[11px] font-semibold tracking-wide text-slate-500 mb-1 dark:text-slate-400";

// ─── Recuadro violeta para vuelo nuevo ───────────────────────────────────────

/**
 * Recuadro que aparece cuando el usuario crea una ruta/aerolínea nueva.
 * Campos mínimos: nombre/identificador (ej: "AEP-MDQ LATAM") + operador.
 *
 * `supplierSugerido`/`onSupplierTouched` (D13-bis, spec 2026-08-10): ver NewHotelBox.
 */
function NewFlightBox({ newProduct, onChange, suppliers, supplierSugerido, onSupplierTouched }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4 dark:border-violet-700 dark:bg-violet-950/20">
            <div className="flex items-center gap-2 mb-3">
                <Plane className="w-4 h-4 text-violet-600 dark:text-violet-400" />
                <span className="text-sm font-semibold text-violet-700 dark:text-violet-300">
                    Ruta nueva — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700 dark:bg-violet-900/50 dark:text-violet-300">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Ruta / aerolínea *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: AEP–IGR LATAM"
                        required
                        data-testid="new-flight-name"
                        aria-label="Ruta o aerolínea nueva"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador / consolidador *</label>
                    <select
                        className={supplierSugerido ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => {
                            onChange({ ...newProduct, supplierPublicId: event.target.value });
                            onSupplierTouched?.();
                        }}
                        required
                        data-testid="new-flight-supplier"
                        aria-label="Operador o consolidador del vuelo nuevo"
                    >
                        <option value="">Seleccioná...</option>
                        {suppliers.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
            </div>
        </div>
    );
}

// ─── Componente principal FlightInlineForm ────────────────────────────────────

export function FlightInlineForm({
    reservaId,
    form,
    setForm,
    suppliers,
    isEditing,
    // Salto de solapa (spec 2026-08-10, D1..D13) — ver ServiceInlineCard.jsx.
    onSelectOtherType,
    seleccionPendiente,
    onConsumirSeleccionPendiente,
    // Fix #4 (auditoría 2026-08-10): levantados a ServiceInlineCard — ver HotelInlineForm
    // para la explicación completa de por qué (sobreviven al remount de cambiar de solapa).
    camposSugeridos,
    setCamposSugeridos,
    precioTocadoPorElUsuario,
    setPrecioTocadoPorElUsuario,
    monedaTocadaPorElUsuario,
    setMonedaTocadaPorElUsuario,
    // Fix regresión #1+#6 (re-review 2026-08-10): APARTE de camposSugeridos — ver el
    // comentario largo en ServiceInlineCard.jsx donde se declara.
    camposTocadosAMano,
    setCamposTocadosAMano,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Cálculo de ganancia: precio de venta - costo (solo si tiene permiso de ver costos)
    const ventaTotal = redondearDinero(Number(form.salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(form.netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos en esos campos.
    // cabinClass también se considera: si viene persistido del backend, expandimos la sección.
    // isDirect/includes* (spec 2026-08-12, §1): datos del PDF de presupuesto — también cuentan
    // para abrir la sección sola. isDirect vive como string ("" / "true" / "false") en el form
    // porque es el value de un <select>; los 3 casilleros son boolean.
    // Obra "PDF completo" (2026-08-13, spec §4 + corrección Round 3 14/08): origen/destino
    // Y los 4 horarios (Sale/Llega ida y vuelta) también abren "Más detalles" solos si ya
    // tienen valor — los horarios viven DENTRO del acordeón (bloque "Horarios del vuelo",
    // pegado al de aeropuertos), no en la zona principal.
    const tieneDetallesExistentes = Boolean(
        form.pnr || form.ticketNumber || form.baggage || form.scheduleNotes || form.cabinClass ||
        form.isDirect || form.includesBackpack || form.includesCarryOn || form.includesCheckedBag ||
        form.origin || form.originCity || form.destination || form.destinationCity ||
        form.outboundDepartureTime || form.outboundArrivalTime ||
        form.returnDepartureTime || form.returnArrivalTime
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // ─── Sugerencia POR CABINA (spec 2026-08-07, §3.3 / M-15 / V9=A / V10=A) ──────────
    // En Aéreo la variante es la cabina — ver HotelInlineForm para la explicación
    // completa del patrón "se acomoda sola mientras no la toques".
    const campoPrecioVariante = canSeeCost ? "netCost" : "salePrice";
    const { suggestion: sugerenciaVariante } = useVariantPriceSuggestion({
        ratePublicId: form.rateId,
        supplierId: form.supplierId,
        cabinClass: form.cabinClass,
    });
    const [hintVariante, setHintVariante] = useState(null);

    useEffect(() => {
        if (!form.rateId) {
            // Fix ronda 3: sin producto elegido todavía no hay nada que sugerir ni que
            // limpiar — evita pintar de amarillo un casillero vacío que nadie sugirió.
            setHintVariante(null);
            return;
        }
        const resultado = resolverCamposAlCambiarVariante({
            estaPrecioTocado: precioTocadoPorElUsuario,
            estaMonedaTocada: monedaTocadaPorElUsuario,
            suggestion: sugerenciaVariante,
            // Fix #8 (auditoría 2026-08-10): ver HotelInlineForm — nunca vaciar un
            // precio que ya tiene valor.
            precioActual: form[campoPrecioVariante],
        });
        setHintVariante(resultado.hintText);
        if (resultado.debeActualizarPrecio || resultado.debeActualizarMoneda) {
            setForm((prev) => ({
                ...prev,
                ...(resultado.debeActualizarPrecio ? { [campoPrecioVariante]: resultado.price } : {}),
                ...(resultado.debeActualizarMoneda ? { currency: resultado.currency || prev.currency } : {}),
            }));
        }
        if (resultado.debeActualizarPrecio) {
            setCamposSugeridos((prev) => ({ ...prev, [campoPrecioVariante]: true }));
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [sugerenciaVariante]);

    // C5: si el operador sugerido no está en la lista de operadores de la reserva,
    // lo agregamos dinámicamente para que el <select> no quede con nada seleccionado
    const supplierListaIds = new Set(suppliers.map((s) => s.publicId || s.PublicId));
    const supplierSugeridoFuera =
        camposSugeridos.supplierId &&
        form.supplierId &&
        !supplierListaIds.has(form.supplierId);
    const suppliersFull = supplierSugeridoFuera
        ? [
              {
                  publicId: form.supplierId,
                  // Sin supplierName no mostramos el ID interno recortado (eso es un dato
                  // técnico que un usuario no programador no puede leer) — mejor un texto
                  // genérico que igual identifica que hay un operador sugerido (2026-08-03).
                  name: form.supplierName || "Operador sugerido",
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult, interpretacion) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Regla transversal (auditoría 2026-08-10, #1): un campo se reemplaza si está
        // vacío O si NO fue tocado a mano por el vendedor (`camposTocadosAMano`) —
        // nunca si lo tipeó/eligió a mano. Fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, NO `camposSugeridos` (ese es solo el amarillo).
        const { patch: patchVenta, sugeridos: sugeridosVenta } = resolverPatchDeVentaDelCatalogo({
            sale,
            canSeeCost,
            formActual: form,
            camposTocadosAMano,
            campoVenta: "salePrice",
            campoCosto: "netCost",
        });

        // Fix C-5(a) (review 2026-08-10): tampoco pisa lo tipeado a mano la precarga de
        // la frase (D13).
        const { patch: patchFrase, sugeridos: sugeridosFrase } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: Boolean(sale.supplierPublicId), camposFecha: CAMPOS_FECHA_VUELO, formActual: form, camposTocadosAMano }
        );

        setForm((prev) => ({
            ...prev,
            // D13: nombre limpio del producto elegido, nunca la frase entera.
            routeName: resolverNombreEnCasillero(catalogResult, prev.routeName),
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            ...patchVenta,
            ...patchFrase,
        }));

        setCamposSugeridos({
            supplierId: false,
            netCost: false,
            salePrice: false,
            currency: false,
            departureDate: false,
            returnDate: false,
            ...sugeridosVenta,
            ...sugeridosFrase,
        });
        // Fix residual (re-review 2026-08-10, ítem A): sembrar desde el mapa persistente,
        // no apagar a ciegas — si el campo que sigue la variante ya estaba tocado a mano,
        // ese toque tiene que sobrevivir a la selección o la sugerencia por cabina lo pisa
        // 300ms después (ver el comentario largo en HotelInlineForm.jsx).
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
        // El renglón gris de abajo ya NO sale de acá: lo arma la sugerencia POR CABINA
        // (useVariantPriceSuggestion), que se dispara sola apenas rateId queda seteado.
    };

    // Salto de solapa (D3/D7, spec 2026-08-10): pendiente elegida desde OTRA solapa.
    useSeleccionPendienteDelTipo({
        seleccionPendiente,
        serviceType: "Aereo",
        onSeleccionar: handleSelectExisting,
        onConsumida: onConsumirSeleccionPendiente,
    });

    const handleCreateNew = (searchText, interpretacion) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que el vendedor NUNCA tocó a mano (fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, no `camposSugeridos`).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, netCost: form.netCost, salePrice: form.salePrice, currency: form.currency },
            camposTocadosAMano,
            { supplierId: "", netCost: "", salePrice: "", currency: "ARS" }
        );

        // D13-bis (spec 2026-08-10, fix "crear nuevo pelado"): fechas de la frase, misma
        // función/guardas que handleSelectExisting. El operador va aparte (al recuadro
        // violeta, ver abajo) — acá no hay `sale`, así que se fuerza a "ya hay operador"
        // para que la función no toque el campo genérico `supplierId` (oculto mientras
        // se crea un producto nuevo).
        const { patch: patchFechas, sugeridos: sugeridosFechas } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: true, camposFecha: CAMPOS_FECHA_VUELO, formActual: form, camposTocadosAMano }
        );

        // El operador del recuadro "ruta nueva" arranca SIEMPRE vacío — si la frase trajo
        // un operador REAL (matcheado por el motor), se precarga ahí, editable.
        const supplierSugeridoDelNuevo = resolverOperadorSugeridoParaProductoNuevo(interpretacion);

        setForm((prev) => ({
            ...prev,
            routeName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: supplierSugeridoDelNuevo },
            ...camposLimpios,
            ...patchFechas,
        }));
        // `supplierId` se reusa para el amarillo del Operador del recuadro violeta
        // (D13-bis) — los dos campos nunca conviven en pantalla.
        setCamposSugeridos({
            supplierId: Boolean(supplierSugeridoDelNuevo),
            netCost: false,
            salePrice: false,
            currency: false,
            departureDate: false,
            returnDate: false,
            ...sugeridosFechas,
        });
        // Mismo criterio que en handleSelectExisting (fix residual, ítem A).
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            routeName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        // Fix #6 (auditoría 2026-08-10): CUALQUIER tecleo desvincula el rateId — los
        // amarillos que quedaban pintados ya no corresponden a ninguna selección viva
        // (el VALOR se queda, deja de ser "sugerencia"). Antes esto solo pasaba si el
        // vendedor borraba TODO el texto.
        setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false, departureDate: false, returnDate: false });
        // Fix REGRESIÓN #1+#6 (re-review 2026-08-10): estos dos NO se tocan acá — antes
        // se apagaban en cada tecleo del buscador por error, dejando que la sugerencia
        // por variante pisara un precio recién tocado a mano. Solo los apaga elegir/
        // crear un producto (como siempre) o el onChange del propio campo.
        // `camposTocadosAMano` tampoco se toca: tipear en el buscador no es tocar a
        // mano ningún campo puntual.
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (ruta o aerolínea) === */}
            <ProductSearchField
                reservaId={reservaId}
                serviceType="Aereo"
                value={form.routeName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onSelectOtherType={onSelectOtherType}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                esEdicion={isEditing}
                rateId={form.rateId}
                supplierIdElegido={form.supplierId}
                label="Ruta / aerolínea"
                placeholder="Ej: AEP–IGR, LATAM, Aerolíneas..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewFlightBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                    supplierSugerido={camposSugeridos.supplierId}
                    onSupplierTouched={() => setCamposSugeridos((prev) => ({ ...prev, supplierId: false }))}
                />
            )}

            {/* === OPERADOR / CONSOLIDADOR (solo si no es producto nuevo) === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-operador">Operador / consolidador</label>
                    <select
                        id="flight-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, supplierId: true }));
                        }}
                        data-testid="flight-supplier"
                        aria-label="Operador o consolidador"
                    >
                        <option value="">Seleccioná un operador...</option>
                        {suppliersFull.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
            )}

            {/* === FECHAS IDA · VUELTA + PASAJEROS + ÁMBITO === */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-ida">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Ida
                    </label>
                    <input
                        id="flight-ida"
                        type="date"
                        // Amarillo (D13, spec 2026-08-10) cuando la fecha salió de la frase
                        // tipeada en el buscador.
                        className={camposSugeridos.departureDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.departureDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, departureDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, departureDate: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, departureDate: true }));
                        }}
                        data-testid="flight-ida"
                        aria-label="Fecha de ida"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-vuelta">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Vuelta
                    </label>
                    <input
                        id="flight-vuelta"
                        type="date"
                        className={camposSugeridos.returnDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.returnDate || ""}
                        min={form.departureDate || undefined}
                        onChange={(event) => {
                            const nuevaFechaVuelta = event.target.value;
                            setForm((prev) => ({
                                ...prev,
                                returnDate: nuevaFechaVuelta,
                                // Fix reviewer (14/08): si el vendedor BORRA la fecha de vuelta, los
                                // casilleros "Sale vuelta"/"Llega vuelta" quedan apagados en pantalla
                                // pero sin esto seguían viajando en el payload — un horario de vuelta
                                // sin vuelta. Los limpiamos junto con la fecha, no solo los deshabilitamos.
                                ...(nuevaFechaVuelta ? {} : { returnDepartureTime: "", returnArrivalTime: "" }),
                            }));
                            setCamposSugeridos((prev) => ({ ...prev, returnDate: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, returnDate: true }));
                        }}
                        data-testid="flight-vuelta"
                        aria-label="Fecha de vuelta (vacío si solo hay ida)"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="flight-pasajeros"
                        type="text"
                        inputMode="numeric"
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: sanitizarCantidadPositiva(event.target.value) }))}
                        placeholder="1"
                        data-testid="flight-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
                {/* Semáforo de DNI vencido para cabotaje (2026-08-03, spec firmada P3/P4): marca
                    de ámbito, A LA VISTA en la misma línea que fechas y pasajeros (P4=A: si no se
                    ve, no se marca, y sin marca el aviso de DNI vencido nunca aparece). Default
                    "Sin definir": no dispara ningún aviso. Solo existe en el Aéreo (P3=A). */}
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-ambito">Vuelo</label>
                    <select
                        id="flight-ambito"
                        className={INPUT_NORMAL}
                        value={form.geographicScope || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, geographicScope: event.target.value }))}
                        data-testid="flight-ambito"
                        aria-label="Ámbito geográfico del vuelo (Nacional o Internacional)"
                    >
                        <option value="">Sin definir</option>
                        <option value="Nacional">Nacional (dentro del país)</option>
                        <option value="Internacional">Internacional</option>
                    </select>
                </div>
            </div>

            {/* === PRECIOS + MONEDA + FECHA LÍMITE DE EMISIÓN ===
                La fecha de emisión va A LA VISTA (no en "Más detalles") — decisión guía UX. */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-4" : "grid-cols-2 sm:grid-cols-3"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="flight-costo">Costo</label>
                        <MoneyInput
                            id="flight-costo"
                            className={camposSugeridos.netCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.netCost || ""}
                            onChange={(nuevoValor) => {
                                setForm((prev) => ({ ...prev, netCost: nuevoValor }));
                                setCamposSugeridos((prev) => ({ ...prev, netCost: false }));
                                setCamposTocadosAMano((prev) => ({ ...prev, netCost: true }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "netCost").
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            data-testid="flight-costo"
                            aria-label="Costo total del vuelo"
                        />
                        {/* Renglón gris POR CABINA (spec 2026-08-07, §3.3): dice si el precio
                            es de esta cabina o de una parecida (V9=A). */}
                        <VariantSuggestionHint text={hintVariante} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-venta">Venta</label>
                    <MoneyInput
                        id="flight-venta"
                        className={camposSugeridos.salePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.salePrice || ""}
                        onChange={(nuevoValor) => {
                            setForm((prev) => ({ ...prev, salePrice: nuevoValor }));
                            setCamposSugeridos((prev) => ({ ...prev, salePrice: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, salePrice: true }));
                            // "Venta" solo es la variante rastreada para quien NO ve costos
                            // (campoPrecioVariante === "salePrice"); con permiso de costos
                            // es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        required
                        data-testid="flight-venta"
                        aria-label="Precio de venta total"
                    />
                    {!canSeeCost && <VariantSuggestionHint text={hintVariante} />}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="flight-moneda">Moneda</label>
                    <select
                        id="flight-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, currency: true }));
                            setMonedaTocadaPorElUsuario(true);
                        }}
                        data-testid="flight-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
                {/* Campo "Límite de emisión" eliminado en F2 (Próximos Inicios).
                    El aviso de la campanita se calcula desde firstStartDate (backend).
                    emissionDeadline NO se mantiene en el estado ni en el payload. */}
            </div>

            {/* === MÁS DETALLES: PNR · Nº ticket · Horarios/escalas · Equipaje === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-primary hover:opacity-80 transition-colors dark:text-primary"
                    data-testid="flight-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        {/* Aeropuertos (spec 2026-08-13, §2): PRIMER bloque, arriba del PNR — describe
                            el vuelo, como Cabina, no es un dato de gestión. Nunca la palabra "IATA" en
                            pantalla (jerga prohibida): el label en criollo + el placeholder alcanzan.
                            Ninguno obligatorio — el PDF arma "EZE · BUENOS AIRES" con lo que haya. */}
                        <div className="sm:col-span-2 grid grid-cols-2 sm:grid-cols-4 gap-3">
                            <div>
                                <label className={LABEL_BASE} htmlFor="flight-origen">Aeropuerto de salida</label>
                                <input
                                    id="flight-origen"
                                    type="text"
                                    maxLength={3}
                                    className={INPUT_NORMAL}
                                    value={form.origin || ""}
                                    onChange={(event) => setForm((prev) => ({ ...prev, origin: event.target.value.toUpperCase() }))}
                                    placeholder="EZE"
                                    data-testid="flight-origen"
                                />
                            </div>
                            <div>
                                <label className={LABEL_BASE} htmlFor="flight-origen-ciudad">Ciudad de salida</label>
                                <input
                                    id="flight-origen-ciudad"
                                    type="text"
                                    className={INPUT_NORMAL}
                                    value={form.originCity || ""}
                                    onChange={(event) => setForm((prev) => ({ ...prev, originCity: event.target.value }))}
                                    placeholder="Buenos Aires"
                                    data-testid="flight-origen-ciudad"
                                />
                            </div>
                            <div>
                                <label className={LABEL_BASE} htmlFor="flight-destino">Aeropuerto de llegada</label>
                                <input
                                    id="flight-destino"
                                    type="text"
                                    maxLength={3}
                                    className={INPUT_NORMAL}
                                    value={form.destination || ""}
                                    onChange={(event) => setForm((prev) => ({ ...prev, destination: event.target.value.toUpperCase() }))}
                                    placeholder="MIA"
                                    data-testid="flight-destino"
                                />
                            </div>
                            <div>
                                <label className={LABEL_BASE} htmlFor="flight-destino-ciudad">Ciudad de llegada</label>
                                <input
                                    id="flight-destino-ciudad"
                                    type="text"
                                    className={INPUT_NORMAL}
                                    value={form.destinationCity || ""}
                                    onChange={(event) => setForm((prev) => ({ ...prev, destinationCity: event.target.value }))}
                                    placeholder="Miami"
                                    data-testid="flight-destino-ciudad"
                                />
                            </div>
                        </div>
                        {/* Horarios del vuelo (spec 2026-08-13 §1, corrección Round 3 14/08): el
                            dueño eligió la opción B — los 4 horarios van ADENTRO de "+ Más detalles",
                            pegados al bloque de aeropuertos (juntos arman el tramo), NO en la zona
                            principal. Van por OutboundDepartureTime/OutboundArrivalTime/
                            ReturnDepartureTime/ReturnArrivalTime — NUNCA por departureTime/arrivalTime
                            (ver la nota "SEMÁNTICA INTOCABLE" en buildFlightPayload, ServiceInlineCard.jsx).
                            Ninguno es obligatorio: vacío = no informado, el PDF simplemente no imprime
                            esa línea. Los de "vuelta" quedan apagados sin fecha de vuelta cargada — sin
                            fecha no hay tramo que horariar (mismo criterio que ya usan los demás
                            casilleros dependientes de otro campo, sin cartelito por P-15). */}
                        <div className="sm:col-span-2">
                            <p className={LABEL_BASE}>Horarios del vuelo</p>
                            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                                <div>
                                    <label className={LABEL_BASE} htmlFor="flight-ida-sale">Sale ida</label>
                                    <input
                                        id="flight-ida-sale"
                                        type="time"
                                        className={INPUT_NORMAL}
                                        value={form.outboundDepartureTime || ""}
                                        onChange={(event) => setForm((prev) => ({ ...prev, outboundDepartureTime: event.target.value }))}
                                        data-testid="flight-ida-sale"
                                    />
                                </div>
                                <div>
                                    <label className={LABEL_BASE} htmlFor="flight-ida-llega">Llega ida</label>
                                    <input
                                        id="flight-ida-llega"
                                        type="time"
                                        className={INPUT_NORMAL}
                                        value={form.outboundArrivalTime || ""}
                                        onChange={(event) => setForm((prev) => ({ ...prev, outboundArrivalTime: event.target.value }))}
                                        data-testid="flight-ida-llega"
                                    />
                                </div>
                                <div>
                                    <label className={LABEL_BASE} htmlFor="flight-vuelta-sale">Sale vuelta</label>
                                    <input
                                        id="flight-vuelta-sale"
                                        type="time"
                                        className={INPUT_NORMAL}
                                        value={form.returnDepartureTime || ""}
                                        disabled={!form.returnDate}
                                        onChange={(event) => setForm((prev) => ({ ...prev, returnDepartureTime: event.target.value }))}
                                        data-testid="flight-vuelta-sale"
                                    />
                                </div>
                                <div>
                                    <label className={LABEL_BASE} htmlFor="flight-vuelta-llega">Llega vuelta</label>
                                    <input
                                        id="flight-vuelta-llega"
                                        type="time"
                                        className={INPUT_NORMAL}
                                        value={form.returnArrivalTime || ""}
                                        disabled={!form.returnDate}
                                        onChange={(event) => setForm((prev) => ({ ...prev, returnArrivalTime: event.target.value }))}
                                        data-testid="flight-vuelta-llega"
                                    />
                                </div>
                            </div>
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="flight-pnr">Código de reserva (PNR)</label>
                            <input
                                id="flight-pnr"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.pnr || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, pnr: event.target.value.toUpperCase() }))}
                                placeholder="ABC123"
                                data-testid="flight-pnr"
                                aria-label="Código de reserva PNR"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="flight-ticket">Números de ticket</label>
                            <input
                                id="flight-ticket"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.ticketNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, ticketNumber: event.target.value }))}
                                placeholder="0741234567890"
                                data-testid="flight-ticket"
                                aria-label="Números de ticket"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            {/* Ex "Horarios y escalas" (spec 2026-08-13, §3): mismo campo de texto
                                libre, mismo dato guardado (scheduleNotes) — solo cambia el nombre para
                                no competir con los casilleros de hora nuevos (P-16: un dato no se dice
                                dos veces). Lo ya cargado en este campo no se toca ni se migra. */}
                            <label className={LABEL_BASE} htmlFor="flight-horarios">Escalas</label>
                            <input
                                id="flight-horarios"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.scheduleNotes || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, scheduleNotes: event.target.value }))}
                                placeholder="Ej: Escala de 1h en Panamá · Cambia de avión"
                                data-testid="flight-horarios"
                                aria-label="Escalas"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="flight-equipaje">Equipaje</label>
                            <input
                                id="flight-equipaje"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.baggage || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, baggage: event.target.value }))}
                                placeholder="Ej: 1 pieza 23kg + 1 de mano"
                                data-testid="flight-equipaje"
                                aria-label="Equipaje incluido"
                            />
                        </div>
                        {/* "Qué lleva incluido" (spec 2026-08-12, §1): 3 casilleros aparte del texto
                            libre de Equipaje de arriba — son datos ESTRUCTURADOS para que el PDF de
                            presupuesto pueda armar íconos/lista, cosa que un texto libre no permite.
                            Ninguno es obligatorio: si quedan los 3 destildados, el PDF simplemente no
                            muestra esa línea (nunca inventa un dato). accent-color con el ÚNICO azul
                            de acción del sistema, igual que el resto de los checkboxes de la app. */}
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE}>Qué lleva incluido</label>
                            <div className="flex flex-wrap gap-x-5 gap-y-2">
                                <label className="inline-flex items-center gap-1.5 text-[13px] text-slate-700 dark:text-slate-300">
                                    <input
                                        type="checkbox"
                                        className="h-4 w-4 rounded border-slate-300 accent-primary dark:border-slate-600"
                                        checked={Boolean(form.includesBackpack)}
                                        onChange={(event) => setForm((prev) => ({ ...prev, includesBackpack: event.target.checked }))}
                                        data-testid="flight-incluye-mochila"
                                    />
                                    Mochila o bolso personal
                                </label>
                                <label className="inline-flex items-center gap-1.5 text-[13px] text-slate-700 dark:text-slate-300">
                                    <input
                                        type="checkbox"
                                        className="h-4 w-4 rounded border-slate-300 accent-primary dark:border-slate-600"
                                        checked={Boolean(form.includesCarryOn)}
                                        onChange={(event) => setForm((prev) => ({ ...prev, includesCarryOn: event.target.checked }))}
                                        data-testid="flight-incluye-carryon"
                                    />
                                    Equipaje de cabina (carry on)
                                </label>
                                <label className="inline-flex items-center gap-1.5 text-[13px] text-slate-700 dark:text-slate-300">
                                    <input
                                        type="checkbox"
                                        className="h-4 w-4 rounded border-slate-300 accent-primary dark:border-slate-600"
                                        checked={Boolean(form.includesCheckedBag)}
                                        onChange={(event) => setForm((prev) => ({ ...prev, includesCheckedBag: event.target.checked }))}
                                        data-testid="flight-incluye-valija"
                                    />
                                    Valija despachada
                                </label>
                            </div>
                        </div>
                        <div>
                            {/*
                             * Cabina: opcional, igual que en el modal viejo (ServiceFormModal:377-386).
                             * Opciones y values exactos copiados del modal para coherencia con el backend.
                             */}
                            <label className={LABEL_BASE} htmlFor="flight-cabina">Cabina</label>
                            <select
                                id="flight-cabina"
                                className={INPUT_NORMAL}
                                value={form.cabinClass || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, cabinClass: event.target.value }))}
                                data-testid="inline-flight-cabin-class"
                                aria-label="Clase de cabina del vuelo"
                            >
                                <option value="">Sin especificar</option>
                                <option value="Economy">Economy</option>
                                <option value="Premium">Premium Economy</option>
                                <option value="Business">Business</option>
                                <option value="First">Primera Clase</option>
                            </select>
                        </div>
                        {/* "¿Cómo es el vuelo?" (spec 2026-08-12, §1): al lado de Cabina, mismo
                            renglón (grid de 2 columnas). Tri-estado real: "" = Sin especificar
                            (nunca tocado), "true" = Directo, "false" = Con escala(s) — el <select>
                            necesita strings, el payload lo traduce a bool|null. */}
                        <div>
                            <label className={LABEL_BASE} htmlFor="flight-es-directo">¿Cómo es el vuelo?</label>
                            <select
                                id="flight-es-directo"
                                className={INPUT_NORMAL}
                                value={form.isDirect ?? ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, isDirect: event.target.value }))}
                                data-testid="flight-es-directo"
                                aria-label="Si el vuelo es directo o con escalas"
                            >
                                <option value="">Sin especificar</option>
                                <option value="true">Directo</option>
                                <option value="false">Con escala(s)</option>
                            </select>
                        </div>
                    </div>
                )}
            </div>

            {/* Los totales se exportan hacia el footer de ServiceInlineCard */}
        </div>
    );
}

// ─── Cálculo de totales exportado para el footer de ServiceInlineCard ─────────

/**
 * Calcula los totales del vuelo para mostrar en el footer.
 * El aéreo usa precio total (no se multiplica por días/noches).
 */
export function calcularTotalesVuelo({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}
