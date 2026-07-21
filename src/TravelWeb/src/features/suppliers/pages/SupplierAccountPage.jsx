import { useCallback, useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { hasPermission } from "../../../auth";
import {
    ArrowLeft,
    Building2,
    Phone,
    Mail,
    Plus,
    Search,
    Filter,
    Check,
    X,
    Layers,
    ExternalLink,
    Loader2,
    RefreshCw,
    TrendingUp,
    CreditCard,
    RotateCcw,
    Landmark,
    Settings,
    ChevronRight,
    FileText,
    AlertTriangle,
} from "lucide-react";
import { api } from "../../../api";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import {
    DataGrid,
    DataGridBody,
    DataGridCell,
    DataGridEmptyState,
    DataGridHeader,
    DataGridHeaderCell,
    DataGridHeaderRow,
    DataGridRow,
} from "../../../components/ui/DataGrid";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { showSuccess, showError, showConfirm } from "../../../alerts";
// P1 "circuito proveedor" (2026-07-21): mismo motivo/código que "anular servicio" (Tanda 7),
// reusado acá para el PATCH de "bajar el estado" desde la cuenta del proveedor. No se duplica
// el mapeo code → botón, se importa la misma lib pura.
import { resolverRechazoAnularServicio } from "../../reservas/lib/serviceCancellationGuard";
import { getPublicId } from "../../../lib/publicIds";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { SupplierExtractoSection } from "../components/SupplierExtractoSection";
import { PagarProveedorInline } from "../components/PagarProveedorInline";
import { UsarSaldoOperadorInline } from "../components/UsarSaldoOperadorInline";
import { ListaCuentasBancarias } from "../../../features/bank-accounts/components/ListaCuentasBancarias";
import { OperatorRefundsPendingSection } from "../components/OperatorRefundsPendingSection";
import { RegistrarReembolsoRecibidoInline } from "../components/RegistrarReembolsoRecibidoInline";
import { SupplierInvoicesSection } from "../components/SupplierInvoicesSection";
import { useOperatorRefundsPending } from "../hooks/useOperatorRefundsPending";
// CURRENCY_OPTIONS se reutiliza del alta de operador para mantener las etiquetas consistentes.
// No duplicamos el array: si el equipo agrega una moneda, se actualiza en un solo lugar.
import { CURRENCY_OPTIONS } from "../lib/nuevoOperadorLogic.js";
import { ordenarBloquesPesosPrimero, debeMostrarseEnGrisNeutro } from "../lib/supplierPageLogic.js";
import {
    OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR,
    HEREDA_CONFIGURACION_GENERAL,
    valorSelectDesdeOverride,
    overrideDesdeValorSelect,
} from "../../../lib/treasuryFxAssumedBy.js";
import { siguienteEstadoTreasuryFxOverride, puedeGuardarConTreasuryFxOverride } from "../lib/treasuryFxOverrideState.js";
// Configuracion de multas de cancelacion (2026-07-14, spec
// docs/ux/2026-07-14-config-multas-proveedor.md, Pieza 1): campo hermano del de arriba,
// mismo molde (valor real cargado aparte de GET /suppliers/{id}, bloqueo de Guardar
// hasta confirmarlo). Ver el comentario de `cargarConfiguracionAvanzadaOperador` más
// abajo: los dos campos comparten el MISMO fetch porque viven en la misma respuesta.
import {
    SUPPLIER_PENALTY_BEHAVIOR,
    OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR,
    valorSelectDesdePenaltyBehavior,
    penaltyBehaviorDesdeValorSelect,
} from "../../../lib/supplierPenaltyBehavior.js";
import { siguienteEstadoPenaltyBehavior, puedeGuardarConPenaltyBehavior } from "../lib/penaltyBehaviorState.js";
import { supplierDueState } from "../lib/supplierAging.js";

// Estado inicial vacío para la paginación de servicios.
const emptyPage = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

// Etiquetas en español para el enum de condición fiscal del backend.
// El backend almacena strings como "IVA_RESP_INSCRIPTO"; acá los hacemos legibles.
const TAX_CONDITION_LABELS = {
    IVA_RESP_INSCRIPTO: "Resp. Inscripto",
    MONOTRIBUTISTA: "Monotributista",
    IVA_EXENTO: "Exento",
    CONSUMIDOR_FINAL: "Cons. Final",
};

// ─── Los "dos números" del encabezado (Fase D, 2026-07-01) ───────────────────

// Rótulo de cada juego de recuadros. Orden de renderizado: pesos primero, dólares después
// (se ordena en el propio componente; este mapa solo da el texto).
const ROTULO_GRUPO_MONEDA = {
    ARS: "PESOS ($)",
    USD: "DÓLARES (US$)",
};

// Paleta de color por tipo de recuadro. "neutro" se usa siempre que el monto sea $0
// (no hay nada que remarcar) o cuando el usuario no tiene permiso de ver costos.
// Importante: "Me tiene que devolver" (naranja) y "Saldo a favor" (verde) usan colores
// DISTINTOS a propósito — son conceptos distintos (uno es un reclamo, el otro es plata gastable).
const PALETA_RECUADRO = {
    rojo: {
        caja: "border-rose-200 bg-rose-50/60 dark:border-rose-900/40 dark:bg-rose-950/20",
        texto: "text-rose-700 dark:text-rose-400",
    },
    naranja: {
        caja: "border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-950/20",
        texto: "text-amber-700 dark:text-amber-400",
    },
    verde: {
        caja: "border-emerald-200 bg-emerald-50 dark:border-emerald-900/40 dark:bg-emerald-950/20",
        texto: "text-emerald-700 dark:text-emerald-400",
    },
    neutro: {
        caja: "border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/20",
        texto: "text-slate-500",
    },
};

/**
 * Un recuadro individual ("Le debo" / "Me tiene que devolver" / "Saldo a favor").
 *
 * El color fijo del recuadro (rojo/naranja/verde) se apaga a gris neutro cuando el monto
 * es $0 (nada que remarcar) o cuando el usuario no tiene permiso de ver costos.
 */
function RecuadroSaldoOperador({ etiqueta, monto, esquema, puedeVerMontos, currency, testId }) {
    const paleta = debeMostrarseEnGrisNeutro(monto, puedeVerMontos)
        ? PALETA_RECUADRO.neutro
        : PALETA_RECUADRO[esquema];

    return (
        <div
            className={`inline-flex min-w-[9.5rem] flex-col rounded-lg border px-3 py-2 ${paleta.caja}`}
            data-testid={testId}
        >
            <span className={`text-xs font-bold ${paleta.texto}`}>{etiqueta}</span>
            <span className={`font-mono font-bold text-lg ${paleta.texto}`}>
                {puedeVerMontos ? formatCurrency(monto ?? 0, currency) : "—"}
            </span>
        </div>
    );
}

/**
 * Los "dos números" de la cuenta del operador + el saldo a favor, en tres recuadros por moneda.
 *
 * Reemplaza los chips viejos (BalanceHeaderChips) que leían el saldo de CAJA crudo
 * (balancesByCurrency[].balance) y pintaban "A favor" en verde cuando la caja quedaba en
 * negativo — mostrando como "plata para gastar" algo que en realidad el operador nos tiene
 * que DEVOLVER (bug de raíz corregido acá). Ahora lee los tres campos limpios que ya calcula
 * el backend por moneda (GET /suppliers/{id}/account/statement → currencies[]):
 *   - iTheyOwe   → "Le debo"              (rojo)    — lo que la agencia le tiene que pagar.
 *   - theyOweMe  → "Me tiene que devolver" (naranja) — reembolso pendiente por anulaciones.
 *                  NO es plata para gastar.
 *   - prepayment → "Saldo a favor"        (verde)   — plata a cuenta, gastable ya mismo.
 *
 * Pesos y dólares SIEMPRE en juegos separados (nunca se suman). Sin permiso de ver costos
 * (cobranzas.see_cost), los tres recuadros de cada moneda van en gris con "—".
 *
 * Props:
 *   - currencies: array de SupplierAccountStatementCurrencyBlockDto (o [] si no cargó aún)
 *   - loading: boolean — true mientras se pide /account/statement
 */
function SupplierBalanceThreeBoxesHeader({ currencies, loading }) {
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // Pesos primero, dólares después (y cualquier otra moneda al final), como pide la spec.
    const bloquesOrdenados = ordenarBloquesPesosPrimero(currencies);

    if (loading) {
        return (
            <p className="mt-3 text-xs text-slate-400" data-testid="header-saldos-cargando">
                Cargando saldos con el operador…
            </p>
        );
    }

    if (bloquesOrdenados.length === 0) return null;

    return (
        <div className="mt-3 space-y-3">
            {bloquesOrdenados.map((bloque) => (
                <div key={bloque.currency}>
                    <p className="mb-1.5 text-[11px] font-bold uppercase tracking-wider text-slate-400">
                        {ROTULO_GRUPO_MONEDA[bloque.currency] ?? bloque.currency}
                    </p>
                    <div className="flex flex-wrap gap-2">
                        <RecuadroSaldoOperador
                            etiqueta="Le debo"
                            monto={bloque.iTheyOwe}
                            esquema="rojo"
                            puedeVerMontos={puedeVerMontos}
                            currency={bloque.currency}
                            testId={`header-le-debo-${bloque.currency}`}
                        />
                        <RecuadroSaldoOperador
                            etiqueta="Me tiene que devolver"
                            monto={bloque.theyOweMe}
                            esquema="naranja"
                            puedeVerMontos={puedeVerMontos}
                            currency={bloque.currency}
                            testId={`header-me-tiene-que-devolver-${bloque.currency}`}
                        />
                        <RecuadroSaldoOperador
                            etiqueta="Saldo a favor"
                            monto={bloque.prepayment}
                            esquema="verde"
                            puedeVerMontos={puedeVerMontos}
                            currency={bloque.currency}
                            testId={`header-saldo-a-favor-${bloque.currency}`}
                        />
                    </div>
                </div>
            ))}
        </div>
    );
}

// ─── Formulario de edición del proveedor (solapa "Datos") ─────────────────────

/**
 * Formulario en línea para editar los datos de identidad del proveedor.
 *
 * Reemplaza el modal "Editar proveedor": el contenido se muestra directamente
 * dentro de la solapa "Datos" sin ninguna ventana flotante encima.
 * Regla de Gastón: "el modal me parece horrible" (guia-ux-gaston.md).
 *
 * Campos: razón social, moneda por defecto, CUIT, condición fiscal,
 * contacto, teléfono, email, dirección, y estado activo/inactivo.
 *
 * Props:
 *   - supplier: objeto del proveedor (viene del overview de la página).
 *   - onGuardado: callback al guardar exitosamente (recarga el overview para
 *     que el encabezado muestre el nombre/CUIT actualizado).
 */
function SupplierInlineEditForm({ supplier, onGuardado }) {
    const [formData, setFormData] = useState({
        name: "",
        contactName: "",
        taxId: "",
        taxCondition: "",
        address: "",
        email: "",
        phone: "",
        isActive: true,
        // defaultCurrency: moneda del carril de cuenta corriente de este operador.
        // ARS y USD son dos extractos separados; cambiar la moneda NO migra los movimientos históricos.
        defaultCurrency: "ARS",
        // defaultPaymentTermDays: campo del modelo (ADR-041) que NO se muestra en la UI
        // pero se incluye en el PUT para no pisarlo con null en un FULL overwrite.
        defaultPaymentTermDays: null,
        invoicingMode: 0,
    });
    const [saving, setSaving] = useState(false);

    // ADR-044 T4 (2026-07-10): excepción opcional de "quién asume el ajuste por el
    // dólar" para ESTE operador. OJO: el overview (`/suppliers/{id}/account`) NO
    // expone este campo — solo lo devuelve `GET /suppliers/{id}` — así que se busca
    // aparte. Arranca en `cargandoOverride=true` y el botón Guardar queda BLOQUEADO
    // hasta que termine: el PUT del operador asigna SIEMPRE este campo (a diferencia
    // de defaultCurrency/defaultPaymentTermDays, que si vienen vacíos no tocan nada),
    // así que guardar antes de conocer el valor real pisaría con null una excepción
    // ya cargada — justo el bug que hay que evitar.
    const [treasuryFxOverrideSelect, setTreasuryFxOverrideSelect] = useState(HEREDA_CONFIGURACION_GENERAL);
    const [cargandoOverride, setCargandoOverride] = useState(true);

    // Configuracion de multas de cancelacion (2026-07-14, spec
    // docs/ux/2026-07-14-config-multas-proveedor.md, Pieza 1): "¿Suele cobrar multa
    // cuando se anula?" — mismo criterio de bloqueo que el campo de arriba (nunca
    // guardar sin conocer el valor real, para no pisar una configuración ya cargada
    // con el default "no se sabe"). Arranca en Unknown mientras carga.
    const [penaltyBehaviorSelect, setPenaltyBehaviorSelect] = useState(SUPPLIER_PENALTY_BEHAVIOR.Unknown);
    const [cargandoPenaltyBehavior, setCargandoPenaltyBehavior] = useState(true);

    // "Más detalles" cerrado por defecto (spec 4.3.2): el campo del ajuste por el dólar
    // es una excepción rara — no ocupa espacio en la vista principal de la ficha.
    const [masDetallesAbierto, setMasDetallesAbierto] = useState(false);

    // Inicializa el formulario con los datos del proveedor cuando llegan del servidor.
    // Cada vez que el proveedor se recarga (handlePagoRegistrado llama loadOverview, etc.)
    // el formulario vuelve a los valores guardados. Esto está comentado para que quede claro.
    useEffect(() => {
        if (!supplier) return;
        setFormData({
            name: supplier.name || "",
            contactName: supplier.contactName || "",
            taxId: supplier.taxId || "",
            taxCondition: supplier.taxCondition || "",
            address: supplier.address || "",
            email: supplier.email || "",
            phone: supplier.phone || "",
            isActive: supplier.isActive ?? true,
            // Moneda por defecto: se recupera del backend.
            // Fallback ARS para operadores creados antes de que este campo existiera.
            defaultCurrency: supplier.defaultCurrency || "ARS",
            // Round-trip: preservamos el plazo de pago acordado (ADR-041) aunque no
            // lo mostremos en este form. Sin esto, el PUT lo pierde (full overwrite).
            defaultPaymentTermDays: supplier.defaultPaymentTermDays ?? null,
            invoicingMode: supplier.invoicingMode ?? 0,
        });
    }, [supplier]);

    // FIX F2 (gate de frontend, 2026-07-10): si esta carga falla, `cargandoOverride`
    // tiene que quedarse en `true` — NO reactivarlo en un `finally` incondicional. El
    // bug real: un `finally` que siempre hace `setCargandoOverride(false)` reactivaba
    // el botón "Guardar" aunque el fetch hubiera fallado, y `treasuryFxOverrideSelect`
    // seguía en su valor inicial ("hereda la config general") — si el operador tenía
    // una excepción real cargada, guardar en ese estado la pisaba con `null` sin que
    // el usuario se enterara. Por eso `cargandoOverride` solo se apaga en el camino de
    // ÉXITO; en el de error queda prendido y se muestra `errorCargaOverride` con un
    // botón "Reintentar" que vuelve a llamar a esta misma función.
    const [errorCargaOverride, setErrorCargaOverride] = useState(null);
    // Mismo criterio para "¿Suele cobrar multa cuando se anula?" (Pieza 1, 2026-07-14).
    const [errorCargaPenaltyBehavior, setErrorCargaPenaltyBehavior] = useState(null);

    // Busca el valor REAL de `treasuryFxAssumedByOverride` Y `penaltyBehavior` en UN
    // SOLO fetch — los dos campos viven en la MISMA respuesta (GET /suppliers/{id}, que
    // el overview de la cuenta NO expone). Antes esta función solo traía el primero; se
    // extendió acá para no duplicar una segunda llamada de red a la misma URL. Cada campo
    // sigue teniendo su propio estado de carga/error (ver los comentarios de arriba) por
    // si en el futuro alguno pasa a resolverse por otro endpoint — hoy están acoplados
    // solo porque comparten la respuesta, no por una razón de negocio.
    const cargarConfiguracionAvanzadaOperador = useCallback(async () => {
        if (!supplier) return;
        setCargandoOverride(true);
        setErrorCargaOverride(null);
        setCargandoPenaltyBehavior(true);
        setErrorCargaPenaltyBehavior(null);
        try {
            const detalle = await api.get(`/suppliers/${getPublicId(supplier)}`);

            // siguienteEstadoTreasuryFxOverride es la ÚNICA fuente de verdad de qué hacer
            // con el resultado (ver treasuryFxOverrideState.js) — el componente no repite
            // la regla, así nunca puede volver a divergir como pasó con el bug de F2.
            const siguienteOverride = siguienteEstadoTreasuryFxOverride({
                exito: true,
                selectValueNuevo: valorSelectDesdeOverride(detalle?.treasuryFxAssumedByOverride),
            });
            setCargandoOverride(siguienteOverride.cargandoOverride);
            setErrorCargaOverride(siguienteOverride.errorCargaOverride);
            setTreasuryFxOverrideSelect(siguienteOverride.treasuryFxOverrideSelect);

            // Mismo criterio para el campo nuevo (ver penaltyBehaviorState.js).
            const siguientePenaltyBehavior = siguienteEstadoPenaltyBehavior({
                exito: true,
                selectValueNuevo: valorSelectDesdePenaltyBehavior(detalle?.penaltyBehavior),
            });
            setCargandoPenaltyBehavior(siguientePenaltyBehavior.cargandoPenaltyBehavior);
            setErrorCargaPenaltyBehavior(siguientePenaltyBehavior.errorCargaPenaltyBehavior);
            setPenaltyBehaviorSelect(siguientePenaltyBehavior.penaltyBehaviorSelect);
        } catch (error) {
            const mensaje = getApiErrorMessage(error, null);

            const siguienteOverride = siguienteEstadoTreasuryFxOverride({ exito: false, errorMessage: mensaje });
            setCargandoOverride(siguienteOverride.cargandoOverride);
            setErrorCargaOverride(siguienteOverride.errorCargaOverride);
            // A propósito NO se llama a setTreasuryFxOverrideSelect acá: la función pura
            // no devuelve ese campo en la rama de error (ver treasuryFxOverrideState.js)
            // — el valor que el usuario ya veía queda intacto, nunca se resetea.

            const siguientePenaltyBehavior = siguienteEstadoPenaltyBehavior({ exito: false, errorMessage: mensaje });
            setCargandoPenaltyBehavior(siguientePenaltyBehavior.cargandoPenaltyBehavior);
            setErrorCargaPenaltyBehavior(siguientePenaltyBehavior.errorCargaPenaltyBehavior);
        }
    }, [supplier]);

    // Dispara la carga cada vez que cambia el operador. Efecto separado del que
    // inicializa `formData` (arriba) porque lee de un endpoint distinto — un fallo acá
    // no debe tumbar el resto del formulario.
    useEffect(() => {
        cargarConfiguracionAvanzadaOperador();
    }, [cargarConfiguracionAvanzadaOperador]);

    // FIX F2: si CUALQUIERA de las dos cargas falló, el cartel de error tiene que ser
    // VISIBLE de una — el botón "Guardar cambios" queda bloqueado y el usuario necesita
    // entender por qué sin tener que adivinar que hay que abrir "Más detalles" primero.
    const masDetallesEfectivamenteAbierto =
        masDetallesAbierto || Boolean(errorCargaOverride) || Boolean(errorCargaPenaltyBehavior);

    const handleChange = (campo) => (event) => {
        setFormData((anterior) => ({ ...anterior, [campo]: event.target.value }));
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        // ver comentario del estado: nunca guardar sin confirmar el valor real de los dos
        // campos "avanzados" (ajuste por el dólar + comportamiento con multas).
        if (!puedeGuardarConTreasuryFxOverride(cargandoOverride)) return;
        if (!puedeGuardarConPenaltyBehavior(cargandoPenaltyBehavior)) return;
        setSaving(true);
        try {
            await api.put(`/suppliers/${getPublicId(supplier)}`, {
                ...formData,
                // Siempre se manda el valor ACTUAL (tocado o no) — el PUT asigna este
                // campo siempre, así que omitirlo o mandar null "por defecto" borraría
                // una excepción real ya cargada para este operador.
                treasuryFxAssumedByOverride: overrideDesdeValorSelect(treasuryFxOverrideSelect),
                // Configuracion de multas de cancelacion (2026-07-14, Pieza 1): mismo
                // criterio — el PUT asigna SIEMPRE este campo (el backend lo defaultea a
                // Unknown si no viaja), así que hay que mandar el valor actual aunque el
                // usuario no lo haya tocado, para no pisar una config ya cargada.
                penaltyBehavior: penaltyBehaviorDesdeValorSelect(penaltyBehaviorSelect),
            });
            showSuccess("Datos del operador guardados correctamente.");
            if (onGuardado) onGuardado();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudieron guardar los datos del operador."), "Error al guardar");
        } finally {
            setSaving(false);
        }
    };

    const inputClass =
        "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white";
    const labelClass = "text-sm font-medium text-slate-700 dark:text-slate-300";

    return (
        <form onSubmit={handleSubmit} className="space-y-5">
            <div className="grid gap-4 sm:grid-cols-2">

                {/* Razón social: único campo obligatorio para guardar */}
                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Razón social *</label>
                    <input
                        type="text"
                        required
                        value={formData.name}
                        onChange={handleChange("name")}
                        placeholder="Ej: Despegar Argentina S.A."
                        className={inputClass}
                        data-testid="supplier-datos-name"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Plazo habitual de pago (días)</label>
                    <input
                        type="number"
                        min="0"
                        value={formData.defaultPaymentTermDays ?? ""}
                        onChange={(event) => setFormData((prev) => ({
                            ...prev,
                            defaultPaymentTermDays: event.target.value === "" ? null : Number(event.target.value),
                        }))}
                        className={inputClass}
                        data-testid="supplier-datos-payment-term"
                    />
                    <p className="text-xs text-muted-foreground">Sirve para ordenar vencimientos; no mueve plata ni bloquea pagos.</p>
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Cómo trabaja con la agencia</label>
                    <select
                        value={formData.invoicingMode}
                        onChange={(event) => setFormData((prev) => ({ ...prev, invoicingMode: Number(event.target.value) }))}
                        className={inputClass}
                        data-testid="supplier-datos-invoicing-mode"
                    >
                        <option value={0}>Compra y reventa</option>
                        <option value={1}>Intermediación (factura directo al cliente)</option>
                    </select>
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>CUIT</label>
                    <input
                        type="text"
                        value={formData.taxId}
                        onChange={handleChange("taxId")}
                        placeholder="20-12345678-9"
                        className={inputClass}
                        data-testid="supplier-datos-taxId"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Condición fiscal</label>
                    <select
                        value={formData.taxCondition}
                        onChange={handleChange("taxCondition")}
                        className={inputClass}
                        data-testid="supplier-datos-taxCondition"
                    >
                        <option value="">Seleccionar...</option>
                        <option value="IVA_RESP_INSCRIPTO">Resp. Inscripto</option>
                        <option value="MONOTRIBUTISTA">Monotributista</option>
                        <option value="IVA_EXENTO">Exento</option>
                        <option value="CONSUMIDOR_FINAL">Cons. Final</option>
                    </select>
                </div>

                {/* Moneda por defecto: define en qué carril de cuenta corriente operan los
                    servicios y pagos de este proveedor. ARS y USD son extractos separados;
                    cambiar este campo NO migra los movimientos ya registrados. */}
                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Moneda por defecto</label>
                    <select
                        value={formData.defaultCurrency}
                        onChange={handleChange("defaultCurrency")}
                        className={inputClass}
                        data-testid="supplier-datos-defaultCurrency"
                    >
                        {CURRENCY_OPTIONS.map((opt) => (
                            <option key={opt.value} value={opt.value}>
                                {opt.label}
                            </option>
                        ))}
                    </select>
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Contacto</label>
                    <input
                        type="text"
                        value={formData.contactName}
                        onChange={handleChange("contactName")}
                        placeholder="Nombre de la persona de contacto"
                        className={inputClass}
                        data-testid="supplier-datos-contactName"
                    />
                </div>

                <div className="space-y-2">
                    <label className={labelClass}>Teléfono</label>
                    <input
                        type="text"
                        value={formData.phone}
                        onChange={handleChange("phone")}
                        placeholder="+54 11 ..."
                        className={inputClass}
                        data-testid="supplier-datos-phone"
                    />
                </div>

                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Email</label>
                    <input
                        type="email"
                        value={formData.email}
                        onChange={handleChange("email")}
                        placeholder="contacto@operador.com"
                        className={inputClass}
                        data-testid="supplier-datos-email"
                    />
                </div>

                <div className="space-y-2 sm:col-span-2">
                    <label className={labelClass}>Dirección</label>
                    <input
                        type="text"
                        value={formData.address}
                        onChange={handleChange("address")}
                        placeholder="Calle y número, ciudad"
                        className={inputClass}
                        data-testid="supplier-datos-address"
                    />
                </div>

                {/* Toggle activo/inactivo: inactivo = no aparece en buscadores, pero mantiene historial */}
                <div className="sm:col-span-2 flex items-center gap-3 rounded-lg border border-slate-100 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/30 p-3">
                    <input
                        type="checkbox"
                        id="supplier-isActive"
                        checked={formData.isActive}
                        onChange={(event) =>
                            setFormData((anterior) => ({ ...anterior, isActive: event.target.checked }))
                        }
                        className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        data-testid="supplier-datos-isActive"
                    />
                    <label htmlFor="supplier-isActive" className={labelClass + " cursor-pointer"}>
                        Operador activo
                    </label>
                    <span className="text-xs text-muted-foreground">
                        {formData.isActive
                            ? "Activo — aparece en buscadores y se le pueden asignar servicios."
                            : "Inactivo — no aparece en buscadores, pero mantiene su historial."}
                    </span>
                </div>
            </div>

            {/* "Más detalles" — cerrado por defecto (ADR-044 T4, spec sección 4.3.2): el
                ajuste por el dólar es una excepción rara, no un dato de todos los días. */}
            <div className="border-t border-slate-100 dark:border-slate-800 pt-3">
                <button
                    type="button"
                    onClick={() => setMasDetallesAbierto((v) => !v)}
                    className="inline-flex items-center gap-1 text-xs font-semibold text-indigo-600 hover:text-indigo-700 dark:text-indigo-400"
                    data-testid="supplier-datos-mas-detalles-toggle"
                >
                    <ChevronRight
                        className={`h-3.5 w-3.5 transition-transform ${masDetallesEfectivamenteAbierto ? "rotate-90" : ""}`}
                        aria-hidden="true"
                    />
                    Más detalles
                    {(errorCargaOverride || errorCargaPenaltyBehavior) && (
                        <span className="ml-1 h-1.5 w-1.5 rounded-full bg-rose-500" aria-hidden="true" title="Hay un dato pendiente de cargar" />
                    )}
                </button>

                {masDetallesEfectivamenteAbierto && (
                    <div className="mt-3 space-y-4" data-testid="supplier-datos-mas-detalles-panel">
                        <div className="space-y-2">
                            <label className={labelClass} htmlFor="supplier-treasury-fx-override">
                                Ajuste por el dólar en sus multas
                            </label>

                            {errorCargaOverride ? (
                                // FIX F2: mientras la carga real falló, NO se muestra el select con un
                                // valor que podría no ser el correcto — solo el cartel + reintentar.
                                // El submit del form entero queda bloqueado (cargandoOverride sigue true).
                                <div
                                    role="alert"
                                    className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start justify-between gap-3"
                                    data-testid="supplier-datos-treasury-fx-override-error"
                                >
                                    <span>{errorCargaOverride}</span>
                                    <button
                                        type="button"
                                        onClick={cargarConfiguracionAvanzadaOperador}
                                        className="flex-shrink-0 rounded-lg border border-rose-300 bg-white px-2.5 py-1 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:bg-slate-800 dark:text-rose-300 dark:border-rose-700"
                                        data-testid="supplier-datos-treasury-fx-override-reintentar"
                                    >
                                        Reintentar
                                    </button>
                                </div>
                            ) : (
                                <>
                                    <select
                                        id="supplier-treasury-fx-override"
                                        value={treasuryFxOverrideSelect}
                                        onChange={(event) => setTreasuryFxOverrideSelect(event.target.value)}
                                        disabled={cargandoOverride}
                                        className={inputClass + " sm:max-w-sm"}
                                        data-testid="supplier-datos-treasury-fx-override-select"
                                    >
                                        {OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR.map((opcion) => (
                                            <option key={opcion.value} value={opcion.value}>
                                                {opcion.label}
                                            </option>
                                        ))}
                                    </select>
                                    <p className="text-xs text-muted-foreground">
                                        {cargandoOverride
                                            ? "Cargando el valor actual…"
                                            : "Por defecto usa la configuración general de Facturación. Solo cambialo si este operador necesita un criterio distinto."}
                                    </p>
                                </>
                            )}
                        </div>

                        {/* Configuracion de multas de cancelacion (2026-07-14, Pieza 1): campo nuevo
                            DEBAJO del ajuste por el dólar, mismo molde de carga/error/reintentar. Solo
                            SUGIERE un camino en el paso de la multa de una anulación (Pieza 2) — nunca
                            completa montos ni decide sola (regla dura de la spec). */}
                        <div className="space-y-2">
                            <label className={labelClass} htmlFor="supplier-penalty-behavior">
                                ¿Suele cobrar multa cuando se anula?
                            </label>

                            {errorCargaPenaltyBehavior ? (
                                <div
                                    role="alert"
                                    className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start justify-between gap-3"
                                    data-testid="supplier-datos-penalty-behavior-error"
                                >
                                    <span>{errorCargaPenaltyBehavior}</span>
                                    <button
                                        type="button"
                                        onClick={cargarConfiguracionAvanzadaOperador}
                                        className="flex-shrink-0 rounded-lg border border-rose-300 bg-white px-2.5 py-1 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:bg-slate-800 dark:text-rose-300 dark:border-rose-700"
                                        data-testid="supplier-datos-penalty-behavior-reintentar"
                                    >
                                        Reintentar
                                    </button>
                                </div>
                            ) : (
                                <>
                                    <select
                                        id="supplier-penalty-behavior"
                                        value={penaltyBehaviorSelect}
                                        onChange={(event) => setPenaltyBehaviorSelect(event.target.value)}
                                        disabled={cargandoPenaltyBehavior}
                                        className={inputClass + " sm:max-w-sm"}
                                        data-testid="supplier-datos-penalty-behavior-select"
                                    >
                                        {OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR.map((opcion) => (
                                            <option key={opcion.value} value={opcion.value}>
                                                {opcion.label}
                                            </option>
                                        ))}
                                    </select>
                                    <p className="text-xs text-muted-foreground">
                                        {cargandoPenaltyBehavior
                                            ? "Cargando el valor actual…"
                                            : "Esto solo resalta un camino cuando anulás. Nunca completa montos ni decide por vos."}
                                    </p>
                                </>
                            )}
                        </div>
                    </div>
                )}
            </div>

            <div className="flex items-center gap-3 pt-4 border-t border-slate-100 dark:border-slate-800">
                <button
                    type="submit"
                    disabled={
                        saving ||
                        !puedeGuardarConTreasuryFxOverride(cargandoOverride) ||
                        !puedeGuardarConPenaltyBehavior(cargandoPenaltyBehavior)
                    }
                    className="rounded-lg bg-indigo-600 px-5 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-lg shadow-indigo-500/25 transition-all disabled:opacity-50"
                    data-testid="supplier-datos-submit"
                >
                    {saving ? "Guardando..." : "Guardar cambios"}
                </button>
            </div>
        </form>
    );
}

// ─── Grilla de deuda por reserva ─────────────────────────────────────────────

/**
 * Solapa "Deuda por reserva" de la ficha del operador.
 *
 * Muestra la deuda pendiente con el operador, abierta por reserva y por moneda,
 * en formato DataGrid (una tabla limpia por moneda), calcando el estilo del Extracto.
 *
 * Regla dura multimoneda: NUNCA suma pesos con dólares.
 * Un bloque por moneda, una fila por reserva dentro de cada bloque.
 *
 * Enmascarado sin permiso cobranzas.see_cost:
 *   El backend devuelve los montos en 0. En pantalla mostramos "—" en gris neutro
 *   y un aviso único arriba para distinguir "sin permiso" de "no hay deuda".
 *
 * Props:
 *   - publicId: string — publicId del proveedor (para el endpoint)
 */
function SupplierDebtByReservaSection({ publicId, refreshKey }) {
    const [data, setData] = useState(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(null);

    // hasPermission("cobranzas.see_cost") incluye el check de isAdmin internamente.
    // Si es false, el backend devuelve 0 en todos los montos → mostramos "—" en gris
    // para distinguir "sin permiso" de "no hay deuda".
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    // Usamos useCallback para que cargarDeuda sea estable y podamos usarla
    // tanto en el useEffect inicial como en el botón "Reintentar".
    const cargarDeuda = useCallback(async () => {
        setLoading(true);
        setError(null);
        try {
            const response = await api.get(`/suppliers/${publicId}/account/debt-by-reserva`);
            setData(response);
        } catch (err) {
            // getApiErrorMessage extrae el mensaje seguro del backend (evita filtrar
            // mensajes internos o stack traces al usuario).
            setError(getApiErrorMessage(err, "No se pudo cargar la deuda por reserva."));
        } finally {
            setLoading(false);
        }
    }, [publicId]);

    // Carga al montar y cada vez que cambia el proveedor activo.
    useEffect(() => {
        cargarDeuda();
    }, [cargarDeuda, refreshKey]);

    if (loading) {
        return (
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="deuda-loading">
                <div className="border-b p-4 flex items-center gap-2">
                    <Layers className="h-5 w-5" />
                    <h2 className="font-semibold">Deuda por reserva</h2>
                </div>
                <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-400 dark:text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Cargando deuda…
                </div>
            </div>
        );
    }

    if (error) {
        return (
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="deuda-error">
                <div className="border-b p-4 flex items-center gap-2">
                    <Layers className="h-5 w-5" />
                    <h2 className="font-semibold">Deuda por reserva</h2>
                </div>
                <div className="flex flex-col items-center gap-3 py-10 text-center">
                    <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
                    <button
                        type="button"
                        onClick={cargarDeuda}
                        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        <RefreshCw className="h-3.5 w-3.5" />
                        Reintentar
                    </button>
                </div>
            </div>
        );
    }

    const reservas = data?.reservas || [];
    const anticipos = data?.advancesToAccount || [];
    const globales = data?.globalTotals || [];
    const hayDatos = reservas.length > 0 || anticipos.length > 0;

    if (!hayDatos) {
        return (
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="deuda-empty">
                <div className="border-b p-4 flex items-center gap-2">
                    <Layers className="h-5 w-5" />
                    <h2 className="font-semibold">Deuda por reserva</h2>
                </div>
                <div className="py-12 text-center">
                    <Layers className="mx-auto mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
                    <p className="text-sm text-slate-500 dark:text-slate-400">
                        No hay deuda registrada con este operador.
                    </p>
                </div>
            </div>
        );
    }

    // Pivot: construimos un bloque por moneda.
    // Usamos un Set para colectar las monedas en orden de aparición, poniendo USD al final.
    // Regla multimoneda: nunca mezclamos ARS y USD en el mismo bloque.
    const monedasSet = new Set([
        ...reservas.flatMap((r) => r.currencies.map((c) => c.currency)),
        ...anticipos.map((a) => a.currency),
    ]);
    const todasLasMonedas = [...monedasSet].sort((a, b) =>
        a === "USD" ? 1 : b === "USD" ? -1 : 0
    );

    return (
        <div className="space-y-4" data-testid="deuda-por-reserva-section">
            {/* Aviso único arriba cuando el usuario no tiene permiso de ver montos.
                Un solo aviso por toda la solapa (no uno por bloque).
                Explica que las "—" son por restricción de permisos, no porque no se deba nada. */}
            {!puedeVerMontos && (
                <div
                    className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300"
                    role="status"
                    data-testid="deuda-sin-permiso-aviso"
                >
                    No tenés permiso para ver los montos de deuda.
                </div>
            )}

            {/* Un bloque por moneda, calcado del patrón de BloqueExtractoProveedor */}
            {todasLasMonedas.map((moneda) => {
                // Filtramos las reservas que tienen línea en esta moneda
                // y adjuntamos la línea correspondiente para no buscarla de nuevo en cada fila.
                const reservasDeMoneda = reservas
                    .filter((r) => r.currencies.some((c) => c.currency === moneda))
                    .map((r) => ({
                        ...r,
                        lineaMoneda: r.currencies.find((c) => c.currency === moneda),
                    }));

                const anticipoDeMoneda = anticipos.find((a) => a.currency === moneda) || null;
                const totalDeudaDeMoneda = globales.find((g) => g.currency === moneda)?.amount ?? 0;

                return (
                    <BloqueDeudaProveedor
                        key={moneda}
                        currency={moneda}
                        reservas={reservasDeMoneda}
                        anticipo={anticipoDeMoneda}
                        totalDeuda={totalDeudaDeMoneda}
                        puedeVerMontos={puedeVerMontos}
                    />
                );
            })}
        </div>
    );
}

// ─── Bloque de una moneda ────────────────────────────────────────────────────

/**
 * Tabla de deuda para una moneda.
 * Cabecera con CurrencyBadge + nombre de moneda + total deuda a la derecha.
 * Cuerpo: una fila por reserva, más una fila opcional de "Anticipos a cuenta" al pie.
 *
 * Props:
 *   - currency: string — "ARS" | "USD"
 *   - reservas: array — reservas que tienen línea en esta moneda (con .lineaMoneda adjunto)
 *   - anticipo: object|null — { currency, amount } o null si no hay anticipos en esta moneda
 *   - totalDeuda: number — de globalTotals para esta moneda
 *   - puedeVerMontos: boolean — si el usuario puede ver montos (cobranzas.see_cost)
 */
function BloqueDeudaProveedor({ currency, reservas, anticipo, totalDeuda, puedeVerMontos }) {
    const nombreMoneda = currency === "USD" ? "Dólares" : "Pesos";

    // Color del total: rojo si debemos (>0), verde si pagamos de más (<0), gris si 0.
    // Sin permiso: siempre gris (la "—" no indica estado financiero).
    const colorTotal =
        !puedeVerMontos
            ? "text-slate-400 dark:text-slate-500"
            : totalDeuda > 0
                ? "text-rose-600 dark:text-rose-500"
                : totalDeuda < 0
                    ? "text-emerald-600 dark:text-emerald-500"
                    : "text-slate-400 dark:text-slate-600";

    return (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            {/* Cabecera: moneda + total deuda — misma estructura que BloqueExtractoProveedor */}
            <div className="flex items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
                <div className="flex items-center gap-2">
                    <CurrencyBadge currency={currency} />
                    <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
                        {nombreMoneda}
                    </span>
                </div>
                <div className="flex items-center gap-2">
                    <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">
                        Total deuda
                    </span>
                    {puedeVerMontos ? (
                        <span
                            className={`text-sm font-extrabold ${colorTotal}`}
                            data-testid={`deuda-total-${currency}`}
                        >
                            {formatCurrency(totalDeuda, currency)}
                        </span>
                    ) : (
                        <span
                            className="text-sm font-extrabold text-slate-400 dark:text-slate-500"
                            title="Sin permiso para ver montos"
                        >
                            —
                        </span>
                    )}
                </div>
            </div>

            {/* Tabla: columnas Reserva / Detalle / Compras / Pagado / Saldo */}
            <DataGrid density="compact" minWidth="760px">
                <DataGridHeader>
                    <DataGridHeaderRow>
                        <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                        <DataGridHeaderCell>Detalle</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Compras</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Pagado</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
                    </DataGridHeaderRow>
                </DataGridHeader>
                <DataGridBody>
                    {reservas.map((reserva) => (
                        <FilaDeudaReserva
                            key={reserva.reservaPublicId}
                            reserva={reserva}
                            currency={currency}
                            puedeVerMontos={puedeVerMontos}
                        />
                    ))}

                    {/* Fila de anticipos a cuenta: al pie del bloque de su moneda.
                        Los anticipos son pagos sin imputar a ninguna reserva → restan de la deuda total. */}
                    {anticipo && (
                        <FilaAnticipoCuenta
                            anticipo={anticipo}
                            currency={currency}
                            puedeVerMontos={puedeVerMontos}
                        />
                    )}

                    {/* Bloque vacío: no debería ocurrir (no construimos el bloque si no hay datos),
                        pero lo dejamos como safety net para evitar una tabla completamente vacía. */}
                    {reservas.length === 0 && !anticipo && (
                        <DataGridEmptyState colSpan={5} title="Sin deuda en esta moneda." />
                    )}
                </DataGridBody>
            </DataGrid>
        </div>
    );
}

// ─── Fila de reserva ─────────────────────────────────────────────────────────

/**
 * Una fila de la grilla de deuda: una reserva en una moneda específica.
 *
 * Props:
 *   - reserva: objeto con reservaPublicId, numeroReserva, fileName y lineaMoneda adjunto
 *   - currency: string
 *   - puedeVerMontos: boolean
 */
function FilaDeudaReserva({ reserva, currency, puedeVerMontos }) {
    const linea = reserva.lineaMoneda; // { currency, confirmedPurchases, totalPaid, balance }
    const saldo = linea?.balance ?? 0;

    // Color del saldo: rojo si se debe, verde si hay saldo a favor, gris si es cero.
    // Sin permiso: siempre gris.
    const colorSaldo =
        !puedeVerMontos
            ? "text-slate-400 dark:text-slate-500"
            : saldo > 0
                ? "text-rose-600 dark:text-rose-500"
                : saldo < 0
                    ? "text-emerald-600 dark:text-emerald-500"
                    : "text-slate-400 dark:text-slate-600";

    return (
        <DataGridRow data-testid={`deuda-reserva-${reserva.reservaPublicId}`}>
            {/* Reserva: Link real (Ctrl+click / nueva pestaña) con ícono externo.
                El publicId solo se usa para el href; el usuario ve el número visible. */}
            <DataGridCell>
                <Link
                    to={`/reservas/${reserva.reservaPublicId}`}
                    className="inline-flex items-center gap-1 font-bold text-primary hover:underline"
                    data-testid={`link-reserva-${reserva.reservaPublicId}`}
                >
                    {reserva.numeroReserva || "Ver reserva"}
                    <ExternalLink className="h-3 w-3" />
                </Link>
            </DataGridCell>

            {/* Detalle: nombre del file/expediente */}
            <DataGridCell className="text-slate-600 dark:text-slate-400">
                {reserva.fileName || "—"}
            </DataGridCell>

            {/* Compras: total de servicios confirmados en esta moneda */}
            <DataGridCell align="right">
                {puedeVerMontos ? (
                    <span className="font-mono">
                        {formatCurrency(linea?.confirmedPurchases ?? 0, currency)}
                    </span>
                ) : (
                    <span className="text-slate-400 dark:text-slate-500" title="Sin permiso para ver montos">—</span>
                )}
            </DataGridCell>

            {/* Pagado: total pagado al operador en esta moneda */}
            <DataGridCell align="right">
                {puedeVerMontos ? (
                    <span className="font-mono">
                        {formatCurrency(linea?.totalPaid ?? 0, currency)}
                    </span>
                ) : (
                    <span className="text-slate-400 dark:text-slate-500" title="Sin permiso para ver montos">—</span>
                )}
            </DataGridCell>

            {/* Saldo: en negrita, con color semántico. Sin permiso → "—" gris. */}
            <DataGridCell align="right">
                {puedeVerMontos ? (
                    <span className={`font-bold font-mono ${colorSaldo}`}>
                        {formatCurrency(saldo, currency)}
                    </span>
                ) : (
                    <span className="text-slate-400 dark:text-slate-500" title="Sin permiso para ver montos">—</span>
                )}
            </DataGridCell>
        </DataGridRow>
    );
}

// ─── Fila de anticipos a cuenta ──────────────────────────────────────────────

/**
 * Fila especial al pie del bloque de una moneda: los anticipos a cuenta.
 *
 * Los anticipos son pagos que la agencia le hizo al operador sin asignarlos a una reserva
 * concreta. Restan de la deuda total de esa moneda.
 * Se muestran en negativo en la columna Saldo; las columnas Reserva/Detalle/Compras/Pagado
 * están vacías porque no corresponden a ningún expediente específico.
 *
 * La etiqueta ocupa las primeras 4 columnas (colSpan={4}) y el monto va en la 5ta (Saldo).
 *
 * Props:
 *   - anticipo: { currency, amount }
 *   - currency: string
 *   - puedeVerMontos: boolean
 */
function FilaAnticipoCuenta({ anticipo, currency, puedeVerMontos }) {
    return (
        <DataGridRow
            interactive={false}
            className="bg-slate-50/40 dark:bg-slate-800/10"
            data-testid={`anticipo-${currency}`}
        >
            {/* La etiqueta se extiende por las columnas Reserva + Detalle + Compras + Pagado.
                Usamos colSpan nativo del td: DataGridCell extiende a td con {...props}. */}
            <DataGridCell
                colSpan={4}
                className="italic text-slate-500 dark:text-slate-400 text-xs"
            >
                Anticipos a cuenta (pagos sin reserva imputada)
            </DataGridCell>

            {/* El anticipo resta de la deuda → se muestra con "−" y en verde (es bueno para nosotros) */}
            <DataGridCell align="right">
                {puedeVerMontos ? (
                    <span className="font-bold font-mono text-emerald-600 dark:text-emerald-500">
                        − {formatCurrency(anticipo.amount, currency)}
                    </span>
                ) : (
                    <span className="text-slate-400 dark:text-slate-500" title="Sin permiso para ver montos">—</span>
                )}
            </DataGridCell>
        </DataGridRow>
    );
}

// ─── Editor de estado del servicio ───────────────────────────────────────────

// Mapeo de Type (en espanol, viene del backend) -> endpoint de status update.
// Si no esta mapeado (servicios genericos), no se permite editar inline aca.
const STATUS_ENDPOINT_BY_TYPE = {
    "Hotel": "hotel-bookings",
    "Vuelo": "flight-segments",
    "Traslado": "transfer-bookings",
    "Paquete": "package-bookings",
    "Asistencia": "assistance-bookings",
};

const STATUS_OPTIONS = ["Solicitado", "Confirmado", "Cancelado"];

function ServiceStatusEditor({ service, onUpdated, canEdit }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [value, setValue] = useState(service.status || "Solicitado");
    const [saving, setSaving] = useState(false);
    // P1 "circuito proveedor" (2026-07-21): cuando el PATCH de "bajar el estado" choca con el
    // candado R1 (el servicio ya tiene pagos al operador y la reserva no tiene factura de venta
    // para anclar el reembolso), el backend manda el MISMO code que "anular servicio". Guardamos
    // acá el mensaje real + si corresponde ofrecer el camino "Emitir factura" — la fila queda con
    // un aviso fijo (no un toast que desaparece) porque el usuario necesita el link a la reserva.
    const [bloqueoPagoSinFactura, setBloqueoPagoSinFactura] = useState(null);

    if (!endpoint || !canEdit) {
        // Servicio generico — no editable desde aca, mostramos texto plano
        return <span className="text-sm">{service.status || "-"}</span>;
    }

    const handleChange = async (e) => {
        const newStatus = e.target.value;
        if (newStatus === value) return;
        const previous = value;
        setValue(newStatus);
        setSaving(true);
        setBloqueoPagoSinFactura(null);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, { status: newStatus });
            showSuccess(`Estado actualizado a "${newStatus}"`);
            if (onUpdated) onUpdated();
        } catch (error) {
            // Revertir el valor optimista en la UI antes de mostrar el error.
            // Usamos getApiErrorMessage para evitar que strings de red en inglés
            // ("Failed to fetch", "Internal Server Error") lleguen al usuario.
            setValue(previous);
            const mensaje = getApiErrorMessage(error, "No se pudo actualizar el estado.");
            // Mismo código que "anular servicio" (CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA):
            // reusamos la lib de la Tanda 7 para no adivinar el motivo comparando texto.
            const rechazo = resolverRechazoAnularServicio(error);
            if (rechazo.boton === "emitir-factura") {
                // El aviso fijo de abajo ya cuenta el motivo con su link: un toast
                // encima seria señal duplicada compitiendo por la atencion.
                setBloqueoPagoSinFactura({ mensaje, reservaPublicId: service.reservaPublicId || null });
            } else {
                showError(mensaje, "No se pudo cambiar el estado");
            }
        } finally {
            setSaving(false);
        }
    };

    const colorClass = value === "Confirmado"
        ? "bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-950/30 dark:text-emerald-300 dark:border-emerald-800"
        : value === "Cancelado"
            ? "bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-950/30 dark:text-rose-300 dark:border-rose-800"
            : "bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-950/30 dark:text-amber-300 dark:border-amber-800";

    return (
        <div className="flex flex-col items-start gap-1">
            <select
                value={value}
                onChange={handleChange}
                disabled={saving}
                className={`rounded-md border text-xs font-bold px-2 py-1 ${colorClass} disabled:opacity-50`}
                title="Cambiar estado del servicio"
            >
                {STATUS_OPTIONS.map((opt) => (
                    <option key={opt} value={opt}>{opt}</option>
                ))}
            </select>
            {/* Aviso fijo (no un toast que desaparece): el usuario necesita el link a la reserva
                para poder resolver el bloqueo, así que lo dejamos visible en la fila. */}
            {bloqueoPagoSinFactura && (
                <div
                    className="max-w-[220px] rounded-md border border-rose-200 bg-rose-50 px-2 py-1.5 text-[11px] leading-snug text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
                    role="alert"
                    data-testid="status-editor-bloqueo-pago-sin-factura"
                >
                    <p>{bloqueoPagoSinFactura.mensaje}</p>
                    {bloqueoPagoSinFactura.reservaPublicId && (
                        <Link
                            to={`/reservas/${bloqueoPagoSinFactura.reservaPublicId}`}
                            state={{ irAFacturar: true }}
                            className="mt-1 inline-flex items-center gap-1 font-bold text-indigo-600 hover:underline dark:text-indigo-400"
                        >
                            <FileText className="h-3 w-3" />
                            Ir a la reserva a facturar
                        </Link>
                    )}
                </div>
            )}
        </div>
    );
}

// ─── Editor del código de confirmación del servicio ──────────────────────────

// Editor inline del codigo de confirmacion del proveedor (PNR para vuelos,
// ConfirmationNumber para el resto). Click para editar, Enter/blur para guardar,
// Esc para cancelar.
function ServiceConfirmationEditor({ service, onUpdated, canEdit }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [editing, setEditing] = useState(false);
    const [value, setValue] = useState(service.confirmation || "");
    const [saving, setSaving] = useState(false);

    // Mantener sincronizado si cambia el dato externo (refresh)
    useEffect(() => {
        if (!editing) setValue(service.confirmation || "");
    }, [service.confirmation, editing]);

    if (!endpoint || !canEdit) {
        return <span className="font-mono text-xs">{service.confirmation || "-"}</span>;
    }

    const save = async () => {
        const trimmed = value.trim();
        const previous = service.confirmation || "";
        if (trimmed === previous) {
            setEditing(false);
            return;
        }
        setSaving(true);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, {
                status: service.status || "Solicitado",
                confirmationNumber: trimmed,
            });
            showSuccess(trimmed ? `Codigo guardado: ${trimmed}` : "Codigo eliminado");
            setEditing(false);
            if (onUpdated) onUpdated();
        } catch (error) {
            // getApiErrorMessage normaliza el error y evita strings en inglés del runtime.
            showError(getApiErrorMessage(error, "No se pudo guardar el código."), "No se pudo guardar el código");
        } finally {
            setSaving(false);
        }
    };

    const cancel = () => {
        setValue(service.confirmation || "");
        setEditing(false);
    };

    if (!editing) {
        return (
            <button
                type="button"
                onClick={() => setEditing(true)}
                className="font-mono text-xs text-left hover:bg-accent px-1.5 py-0.5 rounded transition-colors"
                title="Editar codigo de confirmacion"
            >
                {service.confirmation || <span className="text-muted-foreground italic">(agregar)</span>}
            </button>
        );
    }

    return (
        <div className="inline-flex items-center gap-1">
            <input
                type="text"
                autoFocus
                value={value}
                disabled={saving}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => {
                    if (e.key === "Enter") save();
                    if (e.key === "Escape") cancel();
                }}
                placeholder="Codigo..."
                className="rounded border border-input bg-background px-1.5 py-0.5 text-xs font-mono w-28 focus:outline-none focus:ring-1 focus:ring-ring"
            />
            <button
                type="button"
                onClick={save}
                disabled={saving}
                className="rounded p-0.5 text-emerald-600 hover:bg-emerald-50 disabled:opacity-50"
                title="Guardar"
            >
                <Check className="h-3 w-3" />
            </button>
            <button
                type="button"
                onClick={cancel}
                disabled={saving}
                className="rounded p-0.5 text-slate-500 hover:bg-slate-100 disabled:opacity-50"
                title="Cancelar"
            >
                <X className="h-3 w-3" />
            </button>
        </div>
    );
}

// ─── Página principal ─────────────────────────────────────────────────────────

/**
 * Página de ficha del proveedor (operador), rediseñada como encabezado + solapas.
 *
 * Estructura:
 *   ENCABEZADO (siempre visible): nombre, "Operador", CUIT, condición fiscal,
 *   datos de contacto, y chips de saldo EN VIVO por moneda.
 *
 *   SOLAPAS (mismo patrón visual que la ficha de reserva):
 *     1. Cuenta corriente   — acciones pago/saldo + extracto
 *     2. Deuda por reserva  — grilla de deuda abierta por reserva/moneda (DataGrid)
 *     3. Servicios comprados — grilla operativa con estado y código de confirmación
 *     4. Reembolsos         — plata que el operador debe devolver por anulaciones
 *     5. Datos bancarios    — cuentas (CBU/alias) del operador
 *     6. Datos              — edición de identidad (razón social, CUIT, etc.) SIN modal
 */
export default function SupplierAccountPage() {
    const { publicId } = useParams();
    const navigate = useNavigate();

    // ─── Solapa activa ────────────────────────────────────────────────────────
    const [activeTab, setActiveTab] = useState("cuenta-corriente");

    // ─── Overview del proveedor ───────────────────────────────────────────────
    const [overview, setOverview] = useState(null);
    const [loadingOverview, setLoadingOverview] = useState(true);
    const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

    // ─── Grilla de servicios comprados ────────────────────────────────────────
    const [servicesPage, setServicesPage] = useState(emptyPage);
    const [servicesPaging, setServicesPaging] = useState({ page: 1, pageSize: 25 });
    const [servicesLoading, setServicesLoading] = useState(true);
    const [serviceSearch, setServiceSearch] = useState("");
    const [serviceType, setServiceType] = useState("all");
    const debouncedServiceSearch = useDebounce(serviceSearch, 300);

    // ─── Control de fichas en línea (Cuenta corriente) ───────────────────────
    // extractoRefreshKey: incrementar fuerza al extracto a recargar sin que el usuario
    // refresque la página manualmente.
    const [extractoRefreshKey, setExtractoRefreshKey] = useState(0);
    const [deudaPorReservaRefreshKey, setDeudaPorReservaRefreshKey] = useState(0);
    const [showPagoInline, setShowPagoInline] = useState(false);
    const [paymentToEdit, setPaymentToEdit] = useState(null);
    // Fix bloqueante (review 2026-07-21, security-data-risk-reviewer): PagarProveedorInline
    // avisa acá cada vez que tiene un guardado en curso (POST/PUT en vuelo), para poder
    // deshabilitar el botón "Registrar pago / Cerrar" de arriba mientras tanto — sin esto,
    // el cajero podía cerrar la ficha desde ESE botón a mitad de un guardado.
    const [pagoGuardando, setPagoGuardando] = useState(false);
    // monedaUsandoSaldo: qué moneda tiene la ficha "Usar saldo a favor" abierta.
    // null = ninguna abierta. Solo una moneda puede estar abierta a la vez.
    const [monedaUsandoSaldo, setMonedaUsandoSaldo] = useState(null);
    // showReembolsoInline: si la ficha "Registrar reembolso recibido" está abierta (§4, 2026-07-01).
    const [showReembolsoInline, setShowReembolsoInline] = useState(false);
    // reembolsosTabRefreshKey: forzamos el remount de OperatorRefundsPendingSection al registrar
    // un reembolso desde Cuenta corriente, para que la solapa "Reembolsos" quede consistente
    // sin que el usuario tenga que refrescar la página a mano.
    const [reembolsosTabRefreshKey, setReembolsosTabRefreshKey] = useState(0);

    // ─── Datos de soporte para el extracto ───────────────────────────────────
    // allPayments: lista de hasta 200 pagos del proveedor. Se usa para cruzar
    // con sourcePublicId de cada línea del extracto y ofrecer el botón "Editar".
    const [allPayments, setAllPayments] = useState([]);
    // supplierCreditOverview: saldo a favor y aplicaciones activas.
    const [supplierCreditOverview, setSupplierCreditOverview] = useState(null);

    // ─── Los "dos números" del encabezado (recuadros Le debo / Me tiene que devolver / Saldo a favor) ───
    // statementCurrencies: bloques por moneda de GET /account/statement, con los campos limpios
    // iTheyOwe/theyOweMe/prepayment que arma el backend. Es la MISMA fuente que usa el extracto
    // de abajo para el circuito de cancelación, así que arriba y abajo siempre dan el mismo número.
    const [statementCurrencies, setStatementCurrencies] = useState([]);
    const [loadingStatementHeader, setLoadingStatementHeader] = useState(true);

    // ─── Badge de reembolsos pendientes (numerito en la solapa) ──────────────
    // Cargamos el conteo de reembolsos al montar para poder mostrar el badge.
    // OperatorRefundsPendingSection también carga sus propios datos internamente
    // cuando el usuario entra a la solapa — este call paralelo es intencional.
    const puedeVerReembolsos = hasPermission("tesoreria.supplier_payments");
    const { items: pendingRefundsItems, reload: reloadPendingRefundsBadge } = useOperatorRefundsPending(publicId, puedeVerReembolsos);
    // Solo mostramos el badge si el usuario tiene permiso (de lo contrario el
    // endpoint habría devuelto 403 o vacío, y no tiene sentido mostrarlo).
    const cantidadReembolsosPendientes = hasPermission("tesoreria.supplier_payments")
        ? pendingRefundsItems.length
        : 0;

    // ─── Funciones de carga ───────────────────────────────────────────────────

    const loadOverview = useCallback(async () => {
        setLoadingOverview(true);
        try {
            const response = await api.get(`/suppliers/${publicId}/account`);
            setOverview(response);
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error loading supplier account:", error);
            setOverview(null);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
        } finally {
            setLoadingOverview(false);
        }
    }, [publicId]);

    // Carga hasta 200 pagos del proveedor para hacer cross-reference en el extracto.
    // Si hay más de 200 pagos los más viejos no tendrán botón Editar (sí Eliminar).
    const loadAllPayments = useCallback(async () => {
        if (!publicId) return;
        if (!hasPermission("tesoreria.supplier_payments")) return;
        try {
            const response = await api.get(
                `/suppliers/${publicId}/account/payments?page=1&pageSize=200&sortBy=paidAt&sortDir=desc`
            );
            setAllPayments(response?.items || []);
        } catch (error) {
            // No bloqueante: si falla, el extracto sigue funcionando sin botones de edición.
            console.warn("[SupplierAccountPage] No se pudo cargar la lista de pagos para el extracto:", error?.message);
            setAllPayments([]);
        }
    }, [publicId]);

    // Carga el saldo a favor y aplicaciones activas del proveedor.
    const loadSupplierCredit = useCallback(async () => {
        if (!hasPermission("tesoreria.supplier_payments")) {
            setSupplierCreditOverview(null);
            return;
        }
        try {
            const creditData = await api.get(`/suppliers/${publicId}/credit`);
            setSupplierCreditOverview(creditData);
        } catch (error) {
            // No bloqueante: los carteles de saldo a favor siguen visibles
            // pero sin lista de aplicaciones activas ni botón para usar el saldo.
            console.warn("[SupplierAccountPage] No se pudo cargar el overview de crédito del proveedor:", error?.message);
            setSupplierCreditOverview(null);
        }
    }, [publicId]);

    // Carga los bloques por moneda del extracto (mismo endpoint que usa SupplierExtractoSection)
    // para poder pintar los tres recuadros del encabezado con los campos limpios del backend.
    const loadStatementHeader = useCallback(async () => {
        setLoadingStatementHeader(true);
        try {
            const response = await api.get(`/suppliers/${publicId}/account/statement`);
            setStatementCurrencies(response?.currencies || []);
        } catch (error) {
            // No bloqueante: si falla, los recuadros de arriba simplemente no aparecen;
            // el resto de la pantalla (extracto, deuda, servicios) sigue funcionando igual.
            console.warn("[SupplierAccountPage] No se pudo cargar el estado de cuenta para los recuadros del encabezado:", error?.message);
            setStatementCurrencies([]);
        } finally {
            setLoadingStatementHeader(false);
        }
    }, [publicId]);

    const loadServices = useCallback(async () => {
        setServicesLoading(true);
        try {
            const params = new URLSearchParams({
                page: String(servicesPaging.page),
                pageSize: String(servicesPaging.pageSize),
                sortBy: "date",
                sortDir: "desc",
            });
            if (debouncedServiceSearch.trim()) {
                params.set("search", debouncedServiceSearch.trim());
            }
            if (serviceType !== "all") {
                params.set("type", serviceType);
            }
            const response = await api.get(`/suppliers/${publicId}/account/services?${params.toString()}`);
            setServicesPage({ ...emptyPage, ...(response || {}) });
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error loading supplier services:", error);
            setServicesPage(emptyPage);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
        } finally {
            setServicesLoading(false);
        }
    }, [debouncedServiceSearch, publicId, serviceType, servicesPaging.page, servicesPaging.pageSize]);

    // ─── Handlers ─────────────────────────────────────────────────────────────

    // FIX bloqueante (review 2026-07-21, dos revisores convergentes): antes esta MISMA
    // función refrescaba Y cerraba la ficha a la vez, y PagarProveedorInline la llamaba
    // recién cuando el cajero cerraba el cartel de éxito — si cerraba la ficha desde OTRO
    // lado (el botón de arriba) mientras el cartel estaba abierto, nunca se refrescaba nada.
    // Ahora el refresco y el cierre son DOS funciones separadas:
    //   - handlePagoRegistrado: SOLO refresca. La llama PagarProveedorInline apenas el
    //     guardado resuelve OK del lado del servidor (prop onGuardado), sin esperar a que
    //     el cajero cierre nada — así el dato nunca queda desactualizado.
    //   - handleCerrarFichaPago: SOLO cierra la ficha (sin refrescar por su cuenta). La usa
    //     tanto "Cancelar"/la X (cancelar sin haber guardado nada) como "Cerrar"/"Ver cuenta"
    //     del cartel de éxito (ahí el refresco ya ocurrió antes, vía handlePagoRegistrado).
    const handlePagoRegistrado = useCallback(async () => {
        setExtractoRefreshKey((k) => k + 1);
        setDeudaPorReservaRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadAllPayments(), loadSupplierCredit()]);
    }, [loadOverview, loadAllPayments, loadSupplierCredit]);

    const handleCerrarFichaPago = useCallback(() => {
        setShowPagoInline(false);
        setPaymentToEdit(null);
        // Por las dudas: si por algún motivo saving no se apagó del lado del hijo antes de
        // cerrar, no dejamos el botón de arriba bloqueado para siempre.
        setPagoGuardando(false);
    }, []);

    // Botón "Registrar pago" / "Cerrar" de arriba de la ficha (alterna mostrarla u ocultarla).
    // Al cerrar desde ACÁ (en vez de los botones propios de la ficha), refrescamos igual por
    // las dudas de que haya un pago recién guardado que el cajero no llegó a confirmar con
    // "Cerrar"/"Ver cuenta" — cerrar sin haber guardado nada solo dispara pedidos de más,
    // que no rompen nada.
    const handleToggleFichaPago = useCallback(() => {
        if (pagoGuardando) return; // defensa en profundidad: el botón ya queda disabled=true
        if (showPagoInline) {
            handlePagoRegistrado();
            handleCerrarFichaPago();
        } else {
            setShowPagoInline(true);
        }
    }, [pagoGuardando, showPagoInline, handlePagoRegistrado, handleCerrarFichaPago]);

    // Se llama al completar una aplicación de saldo a favor.
    const handleSaldoAplicado = useCallback(async () => {
        setMonedaUsandoSaldo(null);
        setExtractoRefreshKey((k) => k + 1);
        setDeudaPorReservaRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Se llama al revertir una aplicación de saldo a favor.
    const handleRevertirAplicacionTerminada = useCallback(async () => {
        setExtractoRefreshKey((k) => k + 1);
        setDeudaPorReservaRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), loadSupplierCredit()]);
    }, [loadOverview, loadSupplierCredit]);

    // Se llama al registrar (y confirmar) un reembolso recibido del operador (§4, 2026-07-01).
    // Tiene que dejar consistentes TRES vistas a la vez: el extracto/recuadros de arriba
    // (vía extractoRefreshKey, misma fuente que ya dispara loadStatementHeader), el badge
    // numérico de la solapa "Reembolsos", y el contenido de esa misma solapa (remount por key).
    const handleReembolsoRegistrado = useCallback(async () => {
        setShowReembolsoInline(false);
        setExtractoRefreshKey((k) => k + 1);
        setReembolsosTabRefreshKey((k) => k + 1);
        await Promise.all([loadOverview(), reloadPendingRefundsBadge()]);
    }, [loadOverview, reloadPendingRefundsBadge]);

    // Abre la ficha de pago en modo edición con el pago seleccionado.
    const handleEditarPago = useCallback((payment) => {
        setPaymentToEdit(payment);
        setShowPagoInline(true);
        // Scroll al principio para que la ficha de pago quede visible
        window.scrollTo({ top: 0, behavior: "smooth" });
    }, []);

    // Confirma y elimina un pago del proveedor.
    const handleEliminarPago = useCallback(async (payment) => {
        const paymentId = getPublicId(payment) || payment?.publicId;
        if (!paymentId) return;

        const result = await showConfirm({
            title: "Eliminar pago",
            text: "¿Seguro que querés eliminar este pago? El saldo del proveedor se va a restaurar.",
            confirmText: "Sí, eliminar",
            confirmColor: "rose",
        });

        if (!result?.isConfirmed) return;

        try {
            await api.delete(`/suppliers/${publicId}/payments/${paymentId}`);
            setExtractoRefreshKey((k) => k + 1);
            setDeudaPorReservaRefreshKey((k) => k + 1);
            await Promise.all([loadOverview(), loadAllPayments()]);
            showSuccess("Pago eliminado.");
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo eliminar el pago."), "Error al eliminar");
        }
    }, [publicId, loadOverview, loadAllPayments]);

    // ─── Efectos ──────────────────────────────────────────────────────────────

    // Resetear estado al cambiar de proveedor (navegación directa entre cuentas)
    useEffect(() => {
        setServicesPaging({ page: 1, pageSize: 25 });
        setServiceSearch("");
        setServiceType("all");
        setShowPagoInline(false);
        setPaymentToEdit(null);
        setAllPayments([]);
        // Al cambiar de proveedor el overview viejo no sirve: se anula para que
        // la guarda del esqueleto vuelva a mostrar la carga inicial (y no se vea
        // un instante la cuenta del proveedor anterior).
        setOverview(null);
        setExtractoRefreshKey(0);
        setMonedaUsandoSaldo(null);
        setShowReembolsoInline(false);
        setSupplierCreditOverview(null);
        setStatementCurrencies([]);
        // Volvemos siempre a la primera solapa al cambiar de proveedor
        setActiveTab("cuenta-corriente");
    }, [publicId]);

    // useEffect con dependencias [loadOverview], etc.: cada función loadXxx ya
    // incluye publicId en su useCallback, así que cambiar el proveedor redefine
    // la función y dispara el efecto automáticamente.
    useEffect(() => { loadOverview(); }, [loadOverview]);
    useEffect(() => { loadSupplierCredit(); }, [loadSupplierCredit]);
    useEffect(() => { loadServices(); }, [loadServices]);
    useEffect(() => { loadAllPayments(); }, [loadAllPayments]);

    // Los recuadros del encabezado se recargan junto con el extracto (misma fuente de datos):
    // extractoRefreshKey sube cada vez que se registra/edita/elimina un pago o se usa saldo a
    // favor, y los recuadros de arriba tienen que reflejar ese mismo momento.
    useEffect(() => { loadStatementHeader(); }, [loadStatementHeader, extractoRefreshKey]);

    // Al cambiar filtros de búsqueda o tipo, volvemos a la página 1 de servicios.
    useEffect(() => {
        setServicesPaging((current) => ({ ...current, page: 1 }));
    }, [debouncedServiceSearch, serviceType, servicesPaging.pageSize]);

    // ─── Guardas de estado ────────────────────────────────────────────────────

    // El esqueleto de página entera es SOLO para la carga inicial (o cambio de
    // proveedor, que resetea overview a null). En un refresco con datos ya en
    // pantalla (ej. tras registrar un pago) NO se puede desmontar la página:
    // se llevaría puesta la ficha de pago con su cartel de éxito (bug cazado
    // por el E2E real del rediseño 2026-07-21).
    if (loadingOverview && !overview) {
        return <AccountPageSkeleton />;
    }

    if (!overview && !databaseUnavailable) {
        return (
            <div className="p-6">
                <p className="text-muted-foreground">No se encontró el proveedor.</p>
            </div>
        );
    }

    if (databaseUnavailable) {
        return <DatabaseUnavailableState />;
    }

    // ─── Datos derivados ──────────────────────────────────────────────────────

    const supplier = overview?.supplier;
    const services = servicesPage.items || [];

    // balancesByCurrency: array de { currency, confirmedPurchases, totalPaid, balance }.
    // Reemplaza el resumen escalar anterior que sumaba ARS+USD incorrectamente.
    const balancesByCurrency = overview?.balancesByCurrency || [];

    const puedeVerMontos = hasPermission("cobranzas.see_cost");
    const puedeEditarReservas = hasPermission("reservas.edit");
    const puedeEditarEliminarPago = hasPermission("tesoreria.supplier_payments");
    const puedeVerFacturasProveedor = hasPermission("cobranzas.see_cost");

    // Aplicaciones de saldo a favor vigentes (para el extracto).
    const activeApplications = supplierCreditOverview?.activeApplications ?? [];

    // Saldo disponible para la moneda cuya ficha de "Usar saldo" está abierta.
    const getSaldoDisponible = (moneda) => {
        const creditCurrencyLine = (supplierCreditOverview?.currencies ?? []).find(
            (c) => c.currency === moneda
        );
        if (creditCurrencyLine != null) {
            return Number(creditCurrencyLine.availableBalance ?? 0);
        }
        // Fallback: usamos el balance negativo del overview general (deuda < 0 = a favor)
        const balanceLine = balancesByCurrency.find((b) => b.currency === moneda);
        const deuda = balanceLine?.balance ?? 0;
        return deuda < 0 ? Math.abs(deuda) : 0;
    };

    // Monedas con saldo a favor: son las que muestran el botón "Usar saldo" en la solapa.
    const monedasAFavor = balancesByCurrency.filter((b) => (b.balance ?? 0) < 0);

    // Etiqueta de la solapa "Reembolsos" con el badge numérico si hay pendientes.
    const labelReembolsos = cantidadReembolsosPendientes > 0
        ? `Reembolsos (${cantidadReembolsosPendientes})`
        : "Reembolsos";

    // Condición fiscal en español para el subtítulo del encabezado.
    // Si el valor del backend no está en nuestro mapeo, lo omitimos:
    // la alternativa anterior (?? supplier?.taxCondition) exponía el enum interno
    // al usuario (ej: "IVA_RESP_INSCRIPTO").
    const taxConditionLabel = TAX_CONDITION_LABELS[supplier?.taxCondition] ?? null;

    // Label de la moneda por defecto para la vista de solo lectura (solapa Datos).
    // Busca en CURRENCY_OPTIONS para mostrar "Pesos ($)" en lugar del código "ARS".
    const defaultCurrencyLabel = CURRENCY_OPTIONS.find(
        (option) => option.value === supplier?.defaultCurrency
    )?.label ?? "—";

    // ─── Definición de solapas (patrón igual que ReservaDetailPage) ──────────
    // La solapa "Deuda por reserva" va segunda, pegada a "Cuenta corriente" porque
    // son hermanas conceptuales (extracto y deuda abierta por expediente).
    const solapas = [
        { id: "cuenta-corriente",    label: "Cuenta corriente",    icon: CreditCard  },
        { id: "deuda-por-reserva",   label: "Deuda por reserva",   icon: Layers      },
        { id: "servicios-comprados", label: "Servicios comprados", icon: Building2   },
        ...(puedeVerFacturasProveedor ? [{ id: "facturas-operador", label: "Facturas operador", icon: FileText }] : []),
        ...(puedeVerReembolsos ? [{ id: "reembolsos", label: labelReembolsos, icon: RotateCcw }] : []),
        { id: "datos-bancarios",     label: "Datos bancarios",      icon: Landmark    },
        { id: "datos",               label: "Datos",                icon: Settings    },
    ];

    return (
        <div className="p-6 space-y-6 max-w-7xl mx-auto">

            {/* ── Encabezado: identidad + chips de saldo ────────────────────────
                Siempre visible, arriba de las solapas.
                Chips enmascarados sin permiso cobranzas.see_cost (nunca mostrar verde sin permiso).
            ─────────────────────────────────────────────────────────────────── */}
            <div className="flex items-start gap-4">
                <button
                    onClick={() => navigate("/suppliers")}
                    className="mt-1 inline-flex h-10 w-10 items-center justify-center rounded-lg border border-input bg-background/50 hover:bg-accent flex-shrink-0"
                    aria-label="Volver al listado de operadores"
                >
                    <ArrowLeft className="h-5 w-5" />
                </button>

                <div className="min-w-0 flex-1">
                    {/* Nombre del proveedor */}
                    <h1 className="text-2xl font-bold truncate">{supplier?.name}</h1>

                    {/* Subtítulo: tipo + CUIT + condición fiscal */}
                    <p className="text-muted-foreground text-sm mt-0.5">
                        Operador
                        {supplier?.taxId && ` · CUIT ${supplier.taxId}`}
                        {taxConditionLabel && ` · ${taxConditionLabel}`}
                    </p>

                    {/* Datos de contacto opcionales */}
                    {(supplier?.phone || supplier?.email) && (
                        <div className="flex flex-wrap gap-3 mt-1.5 text-sm text-muted-foreground">
                            {supplier?.phone && (
                                <span className="flex items-center gap-1">
                                    <Phone className="h-4 w-4" /> {supplier.phone}
                                </span>
                            )}
                            {supplier?.email && (
                                <span className="flex items-center gap-1">
                                    <Mail className="h-4 w-4" /> {supplier.email}
                                </span>
                            )}
                        </div>
                    )}

                    {/* Los "dos números" + saldo a favor, por moneda (Fase D, 2026-07-01) */}
                    <SupplierBalanceThreeBoxesHeader
                        currencies={statementCurrencies}
                        loading={loadingStatementHeader}
                    />
                </div>
            </div>

            {/* Aviso pasivo (2026-07-16): un operador dado de alta rápido con el toggle
                "Datos fiscales pendientes" (ver NuevoOperadorInline) queda para siempre sin
                condición fiscal si nadie vuelve a completarla — y eso traba la facturación y
                las anulaciones de ese operador más adelante. Es solo informativo: NO bloquea
                ninguna acción de esta pantalla. Mismo estilo de franja de una línea que usa
                ReservaLockBanner en la ficha de la reserva. */}
            {!supplier?.taxCondition && (
                <div
                    data-testid="supplier-missing-tax-condition-banner"
                    className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200"
                >
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />
                    <span>
                        <span className="font-bold">Faltan los datos fiscales de este operador.</span>
                        {' '}Completá su condición fiscal para poder facturar y hacer anulaciones sin trabas.
                    </span>
                    <button
                        type="button"
                        onClick={() => setActiveTab("datos")}
                        data-testid="supplier-missing-tax-condition-cta"
                        className="ml-auto flex-shrink-0 rounded-lg border border-amber-300 bg-white px-3 py-1 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-100 dark:border-amber-700 dark:bg-slate-800 dark:text-amber-200 dark:hover:bg-amber-900/30"
                    >
                        Completar datos
                    </button>
                </div>
            )}

            {/* ── Solapas (mismo patrón visual que la ficha de la reserva) ─────── */}
            <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">

                {/* Barra de navegación entre solapas */}
                <div className="border-b border-slate-100 bg-slate-50/30 px-4 dark:border-slate-800 dark:bg-slate-800/20 sm:px-6">
                    <nav className="scrollbar-hide flex gap-8 overflow-x-auto" role="tablist">
                        {solapas.map((solapa) => (
                            <button
                                key={solapa.id}
                                role="tab"
                                aria-selected={activeTab === solapa.id}
                                aria-controls={`panel-${solapa.id}`}
                                onClick={() => setActiveTab(solapa.id)}
                                data-testid={`supplier-tab-${solapa.id}`}
                                className={`relative flex items-center gap-2 whitespace-nowrap py-4 text-sm font-semibold transition-all ${
                                    activeTab === solapa.id
                                        ? "text-indigo-600 dark:text-indigo-400"
                                        : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
                                }`}
                            >
                                <solapa.icon className={`h-4 w-4 ${activeTab === solapa.id ? "animate-bounce" : ""}`} />
                                {solapa.label}
                                {/* Línea azul inferior del tab activo */}
                                {activeTab === solapa.id && (
                                    <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-t-full bg-indigo-600 dark:bg-indigo-400" />
                                )}
                            </button>
                        ))}
                    </nav>
                </div>

                {/* Contenido de la solapa activa */}
                <div className="p-4 sm:p-6 lg:p-8">

                    {/* ── SOLAPA 1: Cuenta corriente ────────────────────────────────────
                        Botones de acción + ficha de pago en línea + Extracto.
                        La deuda abierta por reserva se mudó a la solapa "Deuda por reserva"
                        para no saturar esta solapa (cada una tiene una función clara).
                        Los botones abren fichas en línea debajo (sin ventanas flotantes).
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "cuenta-corriente" && (
                        <div className="space-y-6" id="panel-cuenta-corriente" role="tabpanel">

                            {/* Botones de acción: cada uno se gatea con SU propio permiso.
                                "Registrar pago" / "Usar saldo a favor" -> tesoreria.supplier_payments.
                                "Registrar reembolso recibido" -> necesita AMBOS: caja.edit (registra el
                                movimiento de caja) Y tesoreria.supplier_payments (lista los reembolsos
                                pendientes que hay que imputar). Sin los dos, la ficha no puede completarse,
                                así que el botón no aparece (evita mostrar un botón que lleva a un error de carga). */}
                            {hasPermission("tesoreria.supplier_payments") && (
                                <div className="flex flex-wrap items-center gap-3">

                                    {hasPermission("tesoreria.supplier_payments") && (
                                        <>
                                            {/* "Registrar pago": alterna la ficha de pago en línea.
                                                Fix bloqueante (review 2026-07-21): deshabilitado mientras
                                                PagarProveedorInline tiene un guardado en curso (pagoGuardando),
                                                para que el cajero no pueda cerrar la ficha a mitad de un POST/PUT. */}
                                            <button
                                                type="button"
                                                onClick={handleToggleFichaPago}
                                                disabled={pagoGuardando}
                                                data-testid="btn-registrar-pago"
                                                className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                                    showPagoInline
                                                        ? "bg-slate-500 hover:bg-slate-600"
                                                        : "bg-emerald-600 hover:bg-emerald-700 shadow-emerald-500/20"
                                                }`}
                                            >
                                                <Plus className="h-4 w-4" />
                                                {showPagoInline ? "Cerrar" : "Registrar pago"}
                                            </button>

                                            {/* "Usar saldo a favor": un botón por cada moneda con saldo verde.
                                                Si no hay saldo a favor en ninguna moneda, no aparece ningún botón.
                                                Si hay dos monedas a favor, ambos botones muestran su símbolo. */}
                                            {monedasAFavor.map((balance) => {
                                                const simbolo = balance.currency === "USD" ? "US$" : "$";
                                                const estaAbierto = monedaUsandoSaldo === balance.currency;
                                                return (
                                                    <button
                                                        key={balance.currency}
                                                        type="button"
                                                        onClick={() =>
                                                            setMonedaUsandoSaldo((prev) =>
                                                                prev === balance.currency ? null : balance.currency
                                                            )
                                                        }
                                                        data-testid={`btn-usar-saldo-${balance.currency}`}
                                                        className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium shadow-sm transition-colors ${
                                                            estaAbierto
                                                                ? "bg-emerald-200 text-emerald-800 hover:bg-emerald-300 dark:bg-emerald-900/60 dark:text-emerald-200"
                                                                : "bg-emerald-600 text-white hover:bg-emerald-700 shadow-emerald-500/20"
                                                        }`}
                                                    >
                                                        <TrendingUp className="h-4 w-4" />
                                                        {estaAbierto
                                                            ? `Cerrar saldo ${simbolo}`
                                                            : monedasAFavor.length > 1
                                                                ? `Usar saldo en ${simbolo}`
                                                                : "Usar saldo a favor"}
                                                    </button>
                                                );
                                            })}
                                        </>
                                    )}

                                    {/* "Registrar reembolso recibido": alterna la ficha en línea.
                                        Requiere AMBOS permisos (registrar en caja + ver los pendientes a imputar). */}
                                    {hasPermission("caja.edit") && hasPermission("tesoreria.supplier_payments") && (
                                        <button
                                            type="button"
                                            onClick={() => setShowReembolsoInline((prev) => !prev)}
                                            data-testid="btn-registrar-reembolso"
                                            className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium shadow-sm transition-colors ${
                                                showReembolsoInline
                                                    ? "bg-slate-500 hover:bg-slate-600 text-white"
                                                    : "bg-indigo-600 hover:bg-indigo-700 text-white shadow-indigo-500/20"
                                            }`}
                                        >
                                            <RotateCcw className="h-4 w-4" />
                                            {showReembolsoInline ? "Cerrar" : "Registrar reembolso recibido"}
                                        </button>
                                    )}
                                </div>
                            )}

                            {/* Ficha "Usar saldo a favor" en línea (debajo de los botones) */}
                            {monedaUsandoSaldo && (
                                <UsarSaldoOperadorInline
                                    supplierId={getPublicId(supplier)}
                                    moneda={monedaUsandoSaldo}
                                    saldoDisponible={getSaldoDisponible(monedaUsandoSaldo)}
                                    onAplicado={handleSaldoAplicado}
                                    onCancelar={() => setMonedaUsandoSaldo(null)}
                                />
                            )}

                            {/* Ficha "Registrar reembolso recibido" en línea (debajo de los botones) */}
                            {showReembolsoInline && (
                                <RegistrarReembolsoRecibidoInline
                                    supplierId={getPublicId(supplier)}
                                    onRegistrado={handleReembolsoRegistrado}
                                    onCancelar={() => setShowReembolsoInline(false)}
                                />
                            )}

                            {/* Ficha de pago en línea (nuevo pago o edición de uno existente) */}
                            {showPagoInline && (
                                <PagarProveedorInline
                                    supplierId={getPublicId(supplier)}
                                    balancesByCurrency={balancesByCurrency}
                                    openInvoicedCharges={overview?.openInvoicedCharges || []}
                                    paymentToEdit={paymentToEdit}
                                    onGuardado={handlePagoRegistrado}
                                    onCancelar={handleCerrarFichaPago}
                                    onSavingChange={setPagoGuardando}
                                />
                            )}

                            {/* Extracto de cuenta: libro mayor cronológico por moneda */}
                            <SupplierExtractoSection
                                supplierPublicId={publicId}
                                refreshKey={extractoRefreshKey}
                                allPayments={allPayments}
                                canEditarEliminar={puedeEditarEliminarPago}
                                onEditarPago={handleEditarPago}
                                onEliminarPago={handleEliminarPago}
                                activeApplications={activeApplications}
                                canRevertir={puedeEditarEliminarPago}
                                onRevertirTerminado={handleRevertirAplicacionTerminada}
                            />

                        </div>
                    )}

                    {/* ── SOLAPA 2: Deuda por reserva ───────────────────────────────────
                        Grilla de deuda al operador, abierta por reserva y por moneda.
                        Un bloque por moneda (ARS primero, USD después), con DataGrid interno.
                        La sección se movió acá desde "Cuenta corriente" para separar
                        el extracto cronológico del resumen de deuda por reserva.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "deuda-por-reserva" && (
                        <div id="panel-deuda-por-reserva" role="tabpanel">
                            <SupplierDebtByReservaSection publicId={publicId} refreshKey={deudaPorReservaRefreshKey} />
                        </div>
                    )}

                    {/* ── SOLAPA 3: Servicios comprados ────────────────────────────────
                        Grilla operativa con búsqueda, filtro, paginación,
                        y editores inline de estado y código de confirmación.
                        Contenido idéntico al que estaba antes en la página apilada.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "servicios-comprados" && (
                        <div id="panel-servicios-comprados" role="tabpanel">
                            <div className="overflow-hidden rounded-xl border bg-card shadow-sm">
                                <div className="border-b p-4 space-y-4">
                                    <div className="flex items-center justify-between gap-3">
                                        <h2 className="flex items-center gap-2 font-semibold">
                                            <Building2 className="h-5 w-5" />
                                            Servicios comprados
                                        </h2>
                                        <span className="text-sm text-muted-foreground">
                                            {servicesPage.totalCount || 0} resultados
                                        </span>
                                    </div>

                                    <ListToolbar
                                        className="border-slate-200/80 shadow-none dark:border-slate-800"
                                        searchSlot={
                                            <div className="relative flex-1">
                                                <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                                <input
                                                    type="text"
                                                    placeholder="Buscar descripción, reserva o archivo..."
                                                    value={serviceSearch}
                                                    onChange={(event) => setServiceSearch(event.target.value)}
                                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                                                />
                                            </div>
                                        }
                                        filterSlot={
                                            <div className="relative lg:w-56">
                                                <Filter className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                                                <select
                                                    value={serviceType}
                                                    onChange={(event) => setServiceType(event.target.value)}
                                                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                                                >
                                                    <option value="all">Todos los tipos</option>
                                                    <option value="Aereo">Aereo</option>
                                                    <option value="Hotel">Hotel</option>
                                                    <option value="Traslado">Traslado</option>
                                                    <option value="Paquete">Paquete</option>
                                                    <option value="Otro">Otros</option>
                                                </select>
                                            </div>
                                        }
                                    />
                                </div>

                                {/* Grilla desktop */}
                                <DataGrid density="compact" minWidth="1000px">
                                    <DataGridHeader>
                                        <DataGridHeaderRow>
                                            <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                                            <DataGridHeaderCell>Descripcion</DataGridHeaderCell>
                                            <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                                            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                                            <DataGridHeaderCell>Vencimiento</DataGridHeaderCell>
                                            <DataGridHeaderCell>Estado</DataGridHeaderCell>
                                            <DataGridHeaderCell>Codigo</DataGridHeaderCell>
                                            <DataGridHeaderCell align="right">Costo</DataGridHeaderCell>
                                            <DataGridHeaderCell align="right">Venta</DataGridHeaderCell>
                                        </DataGridHeaderRow>
                                    </DataGridHeader>
                                    <DataGridBody>
                                        {servicesLoading ? (
                                            <DataGridEmptyState colSpan={9} title="Cargando servicios..." />
                                        ) : services.length === 0 ? (
                                            <DataGridEmptyState colSpan={9} title="No hay servicios para este filtro." />
                                        ) : (
                                            services.map((service) => (
                                                <DataGridRow key={getPublicId(service)}>
                                                    <DataGridCell>
                                                        <span className="rounded bg-primary/10 px-2 py-1 text-xs font-medium text-primary">
                                                            {service.type}
                                                        </span>
                                                    </DataGridCell>
                                                    <DataGridCell>
                                                        <div className="font-medium">{service.description || "-"}</div>
                                                        {service.fileName
                                                            ? <div className="text-xs text-muted-foreground">{service.fileName}</div>
                                                            : null}
                                                    </DataGridCell>
                                                    <DataGridCell>
                                                        {service.reservaPublicId ? (
                                                            <Link
                                                                to={`/reservas/${service.reservaPublicId}`}
                                                                className="font-medium text-primary hover:underline"
                                                            >
                                                                {service.numeroReserva || "Ver reserva"}
                                                            </Link>
                                                        ) : (
                                                            service.numeroReserva || "-"
                                                        )}
                                                    </DataGridCell>
                                                    <DataGridCell>{formatDate(service.date)}</DataGridCell>
                                                    <DataGridCell>
                                                        {(() => {
                                                            const due = supplierDueState(service.suggestedDueDate);
                                                            if (!due) return <span className="text-xs text-slate-400">Sin plazo</span>;
                                                            const tone = due.tone === "overdue" || due.tone === "today"
                                                                ? "text-rose-700 bg-rose-50 dark:text-rose-300 dark:bg-rose-950/30"
                                                                : due.tone === "soon"
                                                                    ? "text-amber-700 bg-amber-50 dark:text-amber-300 dark:bg-amber-950/30"
                                                                    : "text-slate-600 bg-slate-50 dark:text-slate-300 dark:bg-slate-800";
                                                            return <span className={`rounded px-2 py-1 text-xs font-semibold ${tone}`}>{due.label}</span>;
                                                        })()}
                                                    </DataGridCell>
                                                    <DataGridCell>
                                                        <ServiceStatusEditor
                                                            service={service}
                                                            canEdit={puedeEditarReservas}
                                                            onUpdated={() => { loadServices(); loadOverview(); }}
                                                        />
                                                    </DataGridCell>
                                                    <DataGridCell>
                                                        <ServiceConfirmationEditor
                                                            service={service}
                                                            canEdit={puedeEditarReservas}
                                                            onUpdated={() => { loadServices(); loadOverview(); }}
                                                        />
                                                    </DataGridCell>
                                                    {/* Costo: enmascarado sin permiso cobranzas.see_cost */}
                                                    <DataGridCell align="right" className="font-mono">
                                                        {puedeVerMontos
                                                            ? formatCurrency(service.netCost, service.currency)
                                                            : <span className="text-muted-foreground">—</span>
                                                        }
                                                    </DataGridCell>
                                                    <DataGridCell align="right" className="font-mono">
                                                        {formatCurrency(service.salePrice, service.currency)}
                                                    </DataGridCell>
                                                </DataGridRow>
                                            ))
                                        )}
                                    </DataGridBody>
                                </DataGrid>

                                {/* Vista mobile */}
                                {servicesLoading ? (
                                    <div className="p-4 text-center text-sm text-muted-foreground md:hidden">
                                        Cargando servicios...
                                    </div>
                                ) : services.length === 0 ? (
                                    <ListEmptyState
                                        title="No hay servicios para este filtro."
                                        className="md:hidden rounded-none border-t border-dashed border-slate-200 dark:border-slate-800"
                                    />
                                ) : (
                                    <MobileRecordList className="p-4 md:hidden">
                                        {services.map((service) => (
                                            <MobileRecordCard
                                                key={getPublicId(service)}
                                                title={service.description || "Sin descripcion"}
                                                subtitle={service.type}
                                                meta={
                                                    <>
                                                        <div className="text-xs text-slate-500 dark:text-slate-400">
                                                            Fecha {formatDate(service.date)}
                                                        </div>
                                                        {service.suggestedDueDate && (
                                                            <div className="text-xs font-semibold text-amber-700 dark:text-amber-300">
                                                                {supplierDueState(service.suggestedDueDate)?.label}
                                                            </div>
                                                        )}
                                                        <div className="text-xs text-slate-500 dark:text-slate-400">
                                                            {service.reservaPublicId ? (
                                                                <Link
                                                                    to={`/reservas/${service.reservaPublicId}`}
                                                                    className="text-primary hover:underline"
                                                                >
                                                                    {service.numeroReserva || "Ver reserva"}
                                                                </Link>
                                                            ) : (
                                                                service.numeroReserva || "Sin reserva"
                                                            )}
                                                        </div>
                                                        <div className="flex flex-wrap items-center gap-2 mt-1">
                                                            <ServiceStatusEditor
                                                                service={service}
                                                                canEdit={puedeEditarReservas}
                                                                onUpdated={() => { loadServices(); loadOverview(); }}
                                                            />
                                                        </div>
                                                        <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                                                            Codigo:{" "}
                                                            <ServiceConfirmationEditor
                                                                service={service}
                                                                canEdit={puedeEditarReservas}
                                                                onUpdated={() => { loadServices(); loadOverview(); }}
                                                            />
                                                        </div>
                                                    </>
                                                }
                                                footer={
                                                    <span className="text-xs text-slate-500">
                                                        Costo{" "}
                                                        {puedeVerMontos
                                                            ? formatCurrency(service.netCost, service.currency)
                                                            : <span className="text-muted-foreground">—</span>
                                                        }
                                                    </span>
                                                }
                                                footerActions={
                                                    <span className="text-xs font-semibold text-slate-700 dark:text-slate-200">
                                                        Venta {formatCurrency(service.salePrice, service.currency)}
                                                    </span>
                                                }
                                            />
                                        ))}
                                    </MobileRecordList>
                                )}

                                {/* Paginación */}
                                <div className="border-t p-4">
                                    <PaginationFooter
                                        page={servicesPage.page || servicesPaging.page}
                                        pageSize={servicesPage.pageSize || servicesPaging.pageSize}
                                        totalCount={servicesPage.totalCount || 0}
                                        totalPages={servicesPage.totalPages || 0}
                                        hasPreviousPage={Boolean(servicesPage.hasPreviousPage)}
                                        hasNextPage={Boolean(servicesPage.hasNextPage)}
                                        onPageChange={(page) =>
                                            setServicesPaging((current) => ({ ...current, page }))
                                        }
                                        onPageSizeChange={(pageSize) =>
                                            setServicesPaging({ page: 1, pageSize })
                                        }
                                    />
                                </div>
                            </div>
                        </div>
                    )}

                    {/* ── SOLAPA 4: Reembolsos ──────────────────────────────────────────
                        Plata que el operador nos debe devolver por anulaciones.
                        OperatorRefundsPendingSection se autogate con tesoreria.supplier_payments;
                        si el usuario no tiene ese permiso, no se renderiza nada.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "facturas-operador" && puedeVerFacturasProveedor && (
                        <div id="panel-facturas-operador" role="tabpanel">
                            <SupplierInvoicesSection
                                supplierPublicId={publicId}
                                overview={overview}
                                canEdit={hasPermission("tesoreria.supplier_payments")}
                                canApply={hasPermission("tesoreria.supplier_payments")}
                                invoicingMode={supplier?.invoicingMode}
                            />
                        </div>
                    )}

                    {activeTab === "reembolsos" && (
                        <div id="panel-reembolsos" role="tabpanel">
                            {/* key=reembolsosTabRefreshKey: fuerza el remount (y por lo tanto el
                                refetch) de esta sección cuando se registra un reembolso desde
                                "Cuenta corriente", para que la solapa no quede con datos viejos. */}
                            <OperatorRefundsPendingSection
                                key={reembolsosTabRefreshKey}
                                supplierPublicId={publicId}
                                showSupplierColumn={false}
                            />
                        </div>
                    )}

                    {/* ── SOLAPA 5: Datos bancarios ─────────────────────────────────────
                        Lista de cuentas bancarias del operador (CBU/alias).
                        Edición gateada por proveedores.edit.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "datos-bancarios" && (
                        <div id="panel-datos-bancarios" role="tabpanel">
                            <ListaCuentasBancarias
                                ownerType="Supplier"
                                ownerId={publicId}
                                title="Datos bancarios del operador"
                                canEdit={hasPermission("proveedores.edit")}
                            />
                        </div>
                    )}

                    {/* ── SOLAPA 6: Datos ───────────────────────────────────────────────
                        Edición de identidad del proveedor en línea (sin ventana flotante).
                        Reemplaza el modal "Editar proveedor" anterior.
                        Solo lectura para quien no tiene proveedores.edit.
                    ─────────────────────────────────────────────────────────────── */}
                    {activeTab === "datos" && (
                        <div id="panel-datos" role="tabpanel" className="max-w-2xl">
                            <h2 className="text-lg font-semibold mb-6">Datos del operador</h2>

                            {hasPermission("proveedores.edit") ? (
                                <SupplierInlineEditForm
                                    supplier={supplier}
                                    onGuardado={loadOverview}
                                />
                            ) : (
                                // Vista de solo lectura para quien no tiene permiso de editar
                                <div className="space-y-4 text-sm">
                                    <div className="grid sm:grid-cols-2 gap-4">
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Razón social</p>
                                            <p className="font-medium">{supplier?.name || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">CUIT</p>
                                            <p className="font-medium">{supplier?.taxId || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Condición fiscal</p>
                                            <p className="font-medium">{taxConditionLabel || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Moneda por defecto</p>
                                            <p className="font-medium">{defaultCurrencyLabel}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Contacto</p>
                                            <p className="font-medium">{supplier?.contactName || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Teléfono</p>
                                            <p className="font-medium">{supplier?.phone || "—"}</p>
                                        </div>
                                        <div>
                                            <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Email</p>
                                            <p className="font-medium">{supplier?.email || "—"}</p>
                                        </div>
                                        {supplier?.address && (
                                            <div className="sm:col-span-2">
                                                <p className="text-muted-foreground text-xs uppercase tracking-wider mb-1">Dirección</p>
                                                <p className="font-medium">{supplier.address}</p>
                                            </div>
                                        )}
                                    </div>
                                    <p className="text-xs text-muted-foreground mt-2">
                                        No tenés permiso para editar los datos del operador.
                                    </p>
                                </div>
                            )}
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}
