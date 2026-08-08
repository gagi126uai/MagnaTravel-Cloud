/**
 * Formulario de Hotel dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Maneja los dos caminos definidos en el mockup y la guía UX de Gastón:
 *   - Producto existente: el usuario elige del buscador → operador + costo/venta
 *     se precargan EN AMARILLO (sugeridos, editables). Se guarda con rateId.
 *   - Producto nuevo: el usuario hace clic en "crear nuevo" → aparece un recuadro
 *     violeta con Nombre/Ciudad/Operador. Se guarda con newCatalogProduct.
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — decisión Gastón ronda 1):
 *   Buscador · Operador · Entrada · Salida · Noches (calculadas) · Habitaciones · Pasajeros
 *   Régimen · Tipo de habitación (OBLIGATORIOS — decisión Gastón 2026-06-06, fix bug 400)
 *   Costo por noche · Venta por noche · Moneda
 *
 * Total = noches × habitaciones × precio por noche (decisión Gastón 2026-06-06).
 *
 * Footer: "Venta $X · Ganás $Y  + Más detalles" | "Cancelar" + "Guardar"
 *
 * Detrás de "+ Más detalles" (plegado por defecto):
 *   Confirmación del operador · Dirección
 *
 * Por qué Régimen y Habitación están a la vista y son obligatorios:
 *   CreateHotelRequest / UpdateHotelRequest exigen string RoomType y string MealPlan
 *   (NO nullables). Con null o vacío el backend responde 400. Los selects con default
 *   garantizan que siempre se envíe un valor válido. Decisión UX aprobada por Gastón.
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve Costo por noche + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás mostrar "$0").
 *     El buscador le muestra el precio de VENTA de la última vez (salePrice).
 */

import { useState, useEffect } from "react";
import { Hotel, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { formatCurrency } from "../../../lib/utils";
import { ProductSearchField } from "./ProductSearchField";
import { resolverCamposALimpiarAlCrearNuevo } from "./inlineServiceFormHelpers";
import { FreeTextWithMemoryField } from "../../rates/components/FreeTextWithMemoryField";
import { useVariantPriceSuggestion } from "./useVariantPriceSuggestion";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic";
import { VariantSuggestionHint } from "./VariantSuggestionHint";

// ─── Helpers de formato ──────────────────────────────────────────────────────

/**
 * Calcula la cantidad de noches entre checkIn y checkOut.
 * Devuelve 0 si alguna fecha falta o es inválida.
 */
function calcularNoches(checkIn, checkOut) {
    if (!checkIn || !checkOut) return 0;
    const inicio = new Date(checkIn);
    const fin = new Date(checkOut);
    const diferencia = Math.ceil((fin - inicio) / (1000 * 60 * 60 * 24));
    return diferencia > 0 ? diferencia : 0;
}

/**
 * Formatea un número como precio en la moneda del servicio.
 * ej: formatearPrecio(48000, "ARS") → "$48.000,00"
 *
 * Bug #26 (Tanda 4, 2026-07-24): antes SIEMPRE mostraba "$" con formato es-AR sin
 * mirar la moneda real del servicio — un hotel en USD mostraba "$1.200,00" (parecía
 * pesos) en vez de "US$1.200,00". Delegamos en formatCurrency() (lib/utils.js), el
 * formateador canónico del proyecto, pasándole SIEMPRE la moneda explícita: si no se
 * pasa, formatCurrency() cae a su default legacy (USD), que acá rompería el default
 * histórico de este formulario (ARS). currency = "ARS" solo si el llamador no la pasó.
 */
function formatearPrecio(valor, currency = "ARS") {
    return formatCurrency(valor, currency);
}

/**
 * Redondea un valor monetario a 2 decimales (mismo criterio que el backend y el modal viejo).
 */
function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

// ─── Clases CSS reutilizables ─────────────────────────────────────────────────

const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
// Amarillo: campo precargado como sugerencia (editable) — mockup estilo .sugerido
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
// Calculado: solo lectura con estilo gris punteado — mockup estilo .calc
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Componente NewHotelBox ───────────────────────────────────────────────────

/**
 * Recuadro violeta que aparece cuando el usuario elige "crear nuevo hotel".
 * Campos: Nombre · Ciudad/destino (OBLIGATORIA) · Operador.
 * La Ciudad es el arma principal contra duplicados (guía UX).
 */
function NewHotelBox({ newProduct, onChange, suppliers }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Hotel className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Hotel nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div>
                    <label className={LABEL_BASE}>Nombre *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Nombre del hotel"
                        required
                        data-testid="new-hotel-name"
                        aria-label="Nombre del hotel nuevo"
                    />
                </div>
                <div>
                    {/* Ciudad OBLIGATORIA — es el arma principal contra duplicados (guía UX D6) */}
                    <label className={LABEL_BASE}>Ciudad / destino *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.city || ""}
                        onChange={(event) => onChange({ ...newProduct, city: event.target.value })}
                        placeholder="Ciudad (ej: Posadas)"
                        required
                        data-testid="new-hotel-city"
                        aria-label="Ciudad del hotel nuevo"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador *</label>
                    <select
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
                        required
                        data-testid="new-hotel-supplier"
                        aria-label="Operador del hotel nuevo"
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

// ─── Componente principal HotelInlineForm ─────────────────────────────────────

export function HotelInlineForm({ form, setForm, suppliers, isEditing }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Noches calculadas automáticamente a partir de las fechas (guía UX: "le gusta el conteo")
    const noches = calcularNoches(form.checkIn, form.checkOut);

    // Habitaciones: mínimo 1 (el campo nunca puede quedar en 0 o vacío para el cálculo)
    const habitaciones = Math.max(Number(form.rooms) || 1, 1);

    // Total = noches × habitaciones × precio por noche (decisión Gastón 2026-06-06:
    // el tarifario guarda el precio de UNA habitación UNA noche, y el sistema multiplica).
    const factorTotal = Math.max(noches, 0) * habitaciones;
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost ? redondearDinero((Number(form.unitNetCost) || 0) * factorTotal) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" plegado por defecto. Se abre automáticamente si ya hay datos
    // (por ejemplo, al editar un hotel que ya tiene confirmación o dirección cargada).
    // Régimen y Tipo de habitación ya NO están aquí: subieron a la vista principal.
    // operatorPaymentDeadline NO se chequea: el campo fue eliminado en F2 y siempre es undefined.
    const tieneDetallesExistentes = Boolean(
        form.confirmationNumber || form.address
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // Cuando el usuario elige un hotel existente del buscador, precargamos operador
    // y precio de la última venta EN AMARILLO (sugeridos, editables — mockup Momento 3).
    // Los campos sugeridos se marcan con isSuggested para pintar el fondo amarillo.
    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        unitNetCost: false,
        unitSalePrice: false,
        currency: false,
    });

    // ─── Sugerencia POR HABITACIÓN (spec 2026-08-07, §3.3 / M-15 / V9=A / V10=A) ───────
    // El campo de precio que este usuario ve y edita: costo para quien tiene permiso de
    // verlo, venta para el resto (misma regla que F-14 en el resto de la pantalla).
    const campoPrecioVariante = canSeeCost ? "unitNetCost" : "unitSalePrice";

    // Se re-consulta cada vez que cambia el producto elegido O la combinación que define
    // la variante (habitación, régimen, nombre fino) — es EXACTAMENTE lo que dispara el
    // "se acomoda sola" de V10=A.
    const { suggestion: sugerenciaVariante } = useVariantPriceSuggestion({
        ratePublicId: form.rateId,
        supplierId: form.supplierId,
        roomType: form.roomType,
        mealPlan: form.mealPlan,
        roomCategory: form.roomCategory,
    });
    const [hintVariante, setHintVariante] = useState(null);

    // Flags EXPLÍCITOS de "el vendedor escribió acá a mano" (fix ronda 2 de review —
    // hallazgos #4 y #5). Antes se derivaban de `camposSugeridos`, que también da "no
    // sugerido" para un casillero vacío que NUNCA se tocó (bloqueaba la precarga amarilla
    // de V9=A) y no distinguía precio de moneda como territorios separados (elegir la
    // moneda a mano se pisaba solo con volver a acomodar el precio, V10=A).
    //
    // Fix ronda 3 (BLOQUEANTE): en modo edición arrancan en `true`, NO en `false`. El
    // precio/moneda que trae un servicio YA GUARDADO no es una sugerencia del sistema —
    // es un dato del vendedor (de esta sesión o de una anterior), y por V10=A "lo que
    // escribiste vos no se toca nunca". Sin este seed, el efecto de abajo corría en el
    // MONTAJE con `sugerenciaVariante` todavía en null (la consulta real recién se acaba
    // de disparar y no resolvió) y lo interpretaba como "no hay precio para esta
    // habitación", BORRANDO el costo/venta que `buildHotelFormInitial` ya había cargado.
    // Solo se apagan de nuevo al elegir OTRO producto (nuevo contexto, nueva decisión —
    // ver handleSelectExisting/handleCreateNew/handleSearchChange más abajo); un cambio
    // de habitación DURANTE la edición no las reactiva a propósito — mover el precio de
    // un servicio ya guardado sin que el vendedor lo pida sigue siendo territorio
    // prohibido, edite lo que edite después.
    const [precioTocadoPorElUsuario, setPrecioTocadoPorElUsuario] = useState(isEditing);
    const [monedaTocadaPorElUsuario, setMonedaTocadaPorElUsuario] = useState(isEditing);

    // useEffect con dependencia en `sugerenciaVariante`: corre cada vez que llega una
    // respuesta nueva del hook (que ya viene debounced). NO se agregan los flags de
    // "tocado" ni campoPrecioVariante a las deps a propósito (eslint-disable): si el
    // usuario tipea en OTRO campo mientras tanto, no queremos relanzar este efecto — solo
    // nos importa reaccionar cuando cambia la sugerencia en sí.
    useEffect(() => {
        if (!form.rateId) {
            // Fix ronda 3: sin producto elegido todavía no hay NADA que sugerir ni que
            // limpiar. Cortamos acá para no pintar de amarillo (más abajo, vía
            // camposSugeridos) un casillero vacío que nadie sugirió — pasaba en TODA
            // ficha de servicio nueva, apenas se montaba, antes de elegir nada.
            setHintVariante(null);
            return;
        }
        const resultado = resolverCamposAlCambiarVariante({
            estaPrecioTocado: precioTocadoPorElUsuario,
            estaMonedaTocada: monedaTocadaPorElUsuario,
            suggestion: sugerenciaVariante,
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
            // Sigue siendo territorio del sistema (recién se acomodó solo): la próxima vez
            // que cambie la habitación, se puede volver a acomodar.
            setCamposSugeridos((prev) => ({ ...prev, [campoPrecioVariante]: true }));
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [sugerenciaVariante]);

    // C5: si el operador sugerido (de la última venta del buscador) NO está en la lista
    // de operadores de la reserva, lo agregamos como opción dinámica para que el <select>
    // no quede en amarillo con ninguna fila seleccionable.
    // Solo aplicamos cuando el campo fue marcado como sugerido (camino "eligió del buscador").
    const supplierListaIds = new Set(suppliers.map((s) => s.publicId || s.PublicId));
    const supplierSugeridoNoEstaEnLista =
        camposSugeridos.supplierId &&
        form.supplierId &&
        !supplierListaIds.has(form.supplierId);
    const suppliersFull = supplierSugeridoNoEstaEnLista
        ? [
              {
                  publicId: form.supplierId,
                  // supplierName viene del resultado del buscador si el backend lo incluye.
                  // Sin supplierName no mostramos el ID interno recortado (eso es un dato
                  // técnico que un usuario no programador no puede leer) — mejor un texto
                  // genérico que igual identifica que hay un operador sugerido (2026-08-03).
                  name: form.supplierName || "Operador sugerido",
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult) => {
        // Tomamos la sugerencia del lastSale (venta real) o del rateFallback (campos del Rate)
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        const supplierPublicId = sale.supplierPublicId || "";
        const unitSalePrice = sale.salePrice != null ? String(sale.salePrice) : "";
        // Si no tiene permiso de ver costos, netCost viene null del backend → no tocar el campo
        const unitNetCost = canSeeCost && sale.netCost != null ? String(sale.netCost) : form.unitNetCost;
        const currency = sale.currency || "ARS";

        setForm((prev) => ({
            ...prev,
            // El nombre y la ciudad del hotel ya se mostraban en el campo de búsqueda;
            // al elegir, confirmamos nombre + ciudad del resultado del catálogo
            hotelName: catalogResult.name || prev.hotelName,
            city: catalogResult.subtitle || prev.city,
            rateId: catalogResult.ratePublicId,
            // Limpiamos el modo "producto nuevo" porque ahora el usuario eligió uno existente
            newCatalogProduct: null,
            supplierId: supplierPublicId,
            // Guardamos el nombre del proveedor sugerido (C5) por si no está en la lista de la reserva
            supplierName: sale.supplierName || null,
            unitSalePrice,
            unitNetCost: canSeeCost ? unitNetCost : prev.unitNetCost,
            currency,
        }));

        // Marcamos los campos que vinieron como sugerencia para pintar el fondo amarillo
        setCamposSugeridos({
            supplierId: Boolean(supplierPublicId),
            unitNetCost: canSeeCost && sale.netCost != null,
            unitSalePrice: Boolean(sale.salePrice),
            currency: Boolean(sale.currency),
        });
        // Producto NUEVO recién elegido: ninguno de los dos campos fue tocado todavía en
        // esta decisión — el sistema vuelve a tener vía libre para acomodarlos solos.
        setPrecioTocadoPorElUsuario(false);
        setMonedaTocadaPorElUsuario(false);
        // El renglón gris de abajo ya NO sale de acá: lo arma la sugerencia POR HABITACIÓN
        // (useVariantPriceSuggestion), que se dispara sola apenas rateId queda seteado.
    };

    const handleCreateNew = (searchText) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que TODAVÍA son sugerencia sin tocar (ver resolverCamposALimpiarAlCrearNuevo).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, unitNetCost: form.unitNetCost, unitSalePrice: form.unitSalePrice, currency: form.currency },
            camposSugeridos,
            { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" }
        );
        setForm((prev) => ({
            ...prev,
            hotelName: searchText,
            // Limpiamos el rateId porque ahora vamos al path "producto nuevo"
            rateId: null,
            newCatalogProduct: {
                name: searchText,
                city: "",
                supplierPublicId: "",
            },
            ...camposLimpios,
        }));
        // Los campos que quedaron limpios dejan de ser "sugeridos"; los preservados ya
        // estaban en false (si no, se habrían limpiado), así que todo queda en false.
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
        setPrecioTocadoPorElUsuario(false);
        setMonedaTocadaPorElUsuario(false);
        // "Crear nuevo" no tiene rateId: la sugerencia por habitación se apaga sola (el hook
        // no consulta sin producto elegido).
    };

    // Cuando el usuario escribe en el buscador después de haber elegido un producto,
    // limpiamos el rateId (C4): si no lo hacemos, el payload llevaría el id del hotel
    // viejo mientras el texto del input ya apunta a otro nombre.
    // También limpiamos el producto nuevo si borra todo el texto.
    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            hotelName: texto,
            // Siempre limpiamos el rateId al tipear: el usuario tiene que volver a elegir
            // del dropdown para que el producto quede vinculado de nuevo.
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        // Si borra el texto, también limpiamos los sugeridos (no hay producto seleccionado)
        if (!texto) {
            setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false });
            setPrecioTocadoPorElUsuario(false);
            setMonedaTocadaPorElUsuario(false);
        }
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (primer campo — mockup Momento 1) === */}
            <ProductSearchField
                serviceType="Hotel"
                value={form.hotelName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing} // Al editar, el producto no cambia (solo los datos del servicio)
                label="Hotel"
                placeholder="Escribí el nombre del hotel..."
            />

            {/* === RECUADRO DE HOTEL NUEVO (solo aparece si el usuario elige "crear nuevo") === */}
            {form.newCatalogProduct && (
                <NewHotelBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                />
            )}

            {/* === OPERADOR (campo aparte del buscador; amarillo si fue sugerido) === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-operador">Operador</label>
                    <select
                        id="hotel-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                        }}
                        data-testid="hotel-supplier"
                        aria-label="Operador del hotel"
                    >
                        <option value="">Seleccioná un operador...</option>
                        {/* suppliersFull incluye el sugerido si no estaba en la lista original (C5) */}
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

            {/* === FECHAS + NOCHES + HABITACIONES + PASAJEROS (segunda fila) === */}
            {/* 5 columnas: Entrada · Salida · Noches (calc) · Habitaciones · Pasajeros */}
            <div className="grid grid-cols-2 sm:grid-cols-5 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-checkin">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Entrada
                    </label>
                    <input
                        id="hotel-checkin"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.checkIn || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, checkIn: event.target.value }))}
                        data-testid="hotel-checkin"
                        aria-label="Fecha de entrada"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-checkout">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Salida
                    </label>
                    <input
                        id="hotel-checkout"
                        type="date"
                        className={INPUT_NORMAL}
                        value={form.checkOut || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, checkOut: event.target.value }))}
                        min={form.checkIn || undefined}
                        data-testid="hotel-checkout"
                        aria-label="Fecha de salida"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Noches</label>
                    {/* Calculado automáticamente — solo lectura (mockup estilo .calc) */}
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={noches > 0 ? noches : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Cantidad de noches: ${noches}`}
                        data-testid="hotel-noches"
                    />
                </div>
                <div>
                    {/* Habitaciones: default 1, mínimo 1. Afecta el total (noches × hab × precio/noche) */}
                    <label className={LABEL_BASE} htmlFor="hotel-habitaciones">Habitaciones</label>
                    <input
                        id="hotel-habitaciones"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.rooms || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, rooms: event.target.value }))}
                        placeholder="1"
                        data-testid="hotel-habitaciones"
                        aria-label="Cantidad de habitaciones"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="hotel-pasajeros"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
                        placeholder="1"
                        data-testid="hotel-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
            </div>

            {/* === RÉGIMEN + TIPO DE HABITACIÓN + CATEGORÍA (obligatorios los dos primeros) === */}
            {/* Razón: CreateHotelRequest/UpdateHotelRequest tienen RoomType y MealPlan como
                string no-nullable. Con null o vacío el backend responde 400. Los selects con
                default garantizan que SIEMPRE se envíe un valor válido. Decisión Gastón 2026-06-06.
                Categoría (roomCategory) es OPCIONAL: es el nombre fino ("Superior", "Vista al
                mar") que junto con Régimen y Tipo arma la variante que el tarifario recuerda
                por separado (spec 2026-08-07, §5.2 — texto libre CON MEMORIA). */}
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-regimen">
                        Régimen *
                    </label>
                    <select
                        id="hotel-regimen"
                        className={INPUT_NORMAL}
                        value={form.mealPlan || "Desayuno"}
                        onChange={(event) => setForm((prev) => ({ ...prev, mealPlan: event.target.value }))}
                        required
                        data-testid="inline-hotel-meal-plan"
                        aria-label="Régimen de comidas del hotel"
                    >
                        <option value="Solo Alojamiento">Solo alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pension">Media pensión</option>
                        <option value="Pension Completa">Pensión completa</option>
                        <option value="All Inclusive">All inclusive</option>
                    </select>
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-tipo-habitacion">
                        Tipo de habitación *
                    </label>
                    <select
                        id="hotel-tipo-habitacion"
                        className={INPUT_NORMAL}
                        value={form.roomType || "Doble"}
                        onChange={(event) => setForm((prev) => ({ ...prev, roomType: event.target.value }))}
                        required
                        data-testid="inline-hotel-room-type"
                        aria-label="Tipo de habitación del hotel"
                    >
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Twin">Twin</option>
                        <option value="Triple">Triple</option>
                        <option value="Cuadruple">Cuádruple</option>
                        <option value="Familiar">Familiar</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <FreeTextWithMemoryField
                        id="hotel-categoria"
                        serviceType="Hotel"
                        label="Categoría"
                        placeholder="Ej: Superior, Vista al mar"
                        value={form.roomCategory}
                        onChange={(texto) => setForm((prev) => ({ ...prev, roomCategory: texto }))}
                    />
                </div>
            </div>

            {/* === PRECIOS + MONEDA (tercera fila) === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {/* Costo por noche: solo visible para quien tiene permiso de ver costos */}
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="hotel-costo-noche">Costo por noche</label>
                        <input
                            id="hotel-costo-noche"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.unitNetCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.unitNetCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, unitNetCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, unitNetCost: false }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "unitNetCost") — tocarlo a
                                // mano lo saca del territorio del sistema para siempre.
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            placeholder="0,00"
                            data-testid="hotel-costo-noche"
                            aria-label="Costo por noche"
                        />
                        {/* Renglón gris POR HABITACIÓN (spec 2026-08-07, §3.3): reemplaza al
                            genérico "Último precio" de la venta del producto — este SÍ sabe
                            si el precio es de esta habitación o de una parecida (V9=A). */}
                        <VariantSuggestionHint text={hintVariante} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-venta-noche">Venta por noche</label>
                    <input
                        id="hotel-venta-noche"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.unitSalePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.unitSalePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, unitSalePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, unitSalePrice: false }));
                            // "Venta" solo es la variante rastreada por el sistema para quien
                            // NO ve costos (campoPrecioVariante === "unitSalePrice"); con
                            // permiso de costos es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        placeholder="0,00"
                        required
                        data-testid="hotel-venta-noche"
                        aria-label="Precio de venta por noche"
                    />
                    {/* Sin permiso de costos, el renglón gris POR HABITACIÓN va acá (no hay campo de costo a la vista) */}
                    {!canSeeCost && <VariantSuggestionHint text={hintVariante} />}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-moneda">Moneda</label>
                    <select
                        id="hotel-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            setMonedaTocadaPorElUsuario(true);
                        }}
                        data-testid="hotel-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* === MÁS DETALLES (plegado por defecto — sin cartelitos ni "(opcional)") === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="hotel-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div>
                            <label className={LABEL_BASE} htmlFor="hotel-confirmacion">Confirmación del operador</label>
                            <input
                                id="hotel-confirmacion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.confirmationNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, confirmationNumber: event.target.value }))}
                                placeholder="Número o código de confirmación"
                                data-testid="hotel-confirmacion"
                                aria-label="Número de confirmación del operador"
                            />
                        </div>
                        {/* Campo "Fecha límite de seña/pago" eliminado en F2 (Próximos Inicios).
                            El aviso de la campanita se calcula desde firstStartDate (backend),
                            no desde un campo manual. Sin campo = sin dato viejo que desincronizar. */}
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="hotel-direccion">Dirección</label>
                            <input
                                id="hotel-direccion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.address || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, address: event.target.value }))}
                                placeholder="Dirección del hotel"
                                data-testid="hotel-direccion"
                                aria-label="Dirección del hotel"
                            />
                        </div>
                    </div>
                )}
            </div>

            {/* Los totales (ventaTotal, costoTotal, ganancia) se calculan en este componente
                y se consumen en el footer de ServiceInlineCard vía los exports de abajo. */}
        </div>
    );
}

// Exportamos los calculadores para que ServiceInlineCard los use en el footer
export { calcularNoches, redondearDinero, formatearPrecio };
