/**
 * Formulario de Aéreo dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador de ruta/aerolínea · Operador/consolidador · Ida · Vuelta · Pasajeros
 *   Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   PNR · Números de ticket · Horarios y escalas · Equipaje
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
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
    resolverOperadorSugeridoParaProductoNuevo,
} from "./inlineServiceFormHelpers";
import { useVariantPriceSuggestion } from "./useVariantPriceSuggestion";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic";
import { VariantSuggestionHint } from "./VariantSuggestionHint";
import { useSeleccionPendienteDelTipo } from "./useSeleccionPendienteDelTipo";

// D13 (spec 2026-08-10): campos de fecha de ESTE form, [desde, hasta] — Aéreo tiene ida/vuelta.
const CAMPOS_FECHA_VUELO = ["departureDate", "returnDate"];

// ─── Clases CSS (mismas que HotelInlineForm para coherencia visual) ──────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Recuadro violeta para vuelo nuevo ───────────────────────────────────────

/**
 * Recuadro que aparece cuando el usuario crea una ruta/aerolínea nueva.
 * Campos mínimos: nombre/identificador (ej: "AEP-MDQ LATAM") + operador.
 *
 * `supplierSugerido`/`onSupplierTouched` (D13-bis, spec 2026-08-10): ver NewHotelBox.
 */
function NewFlightBox({ newProduct, onChange, suppliers, supplierSugerido, onSupplierTouched }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Plane className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Ruta nueva — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
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
    const tieneDetallesExistentes = Boolean(
        form.pnr || form.ticketNumber || form.baggage || form.scheduleNotes || form.cabinClass
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
                            setForm((prev) => ({ ...prev, returnDate: event.target.value }));
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
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
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
                        <input
                            id="flight-costo"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.netCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.netCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, netCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, netCost: false }));
                                setCamposTocadosAMano((prev) => ({ ...prev, netCost: true }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "netCost").
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            placeholder="0,00"
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
                    <input
                        id="flight-venta"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.salePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.salePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, salePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, salePrice: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, salePrice: true }));
                            // "Venta" solo es la variante rastreada para quien NO ve costos
                            // (campoPrecioVariante === "salePrice"); con permiso de costos
                            // es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        placeholder="0,00"
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
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="flight-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
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
                            <label className={LABEL_BASE} htmlFor="flight-horarios">Horarios y escalas</label>
                            <input
                                id="flight-horarios"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.scheduleNotes || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, scheduleNotes: event.target.value }))}
                                placeholder="Ej: Sale 10:30 AEP · Escala 1h MDZ · Llega 15:20 IGR"
                                data-testid="flight-horarios"
                                aria-label="Horarios y escalas"
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
