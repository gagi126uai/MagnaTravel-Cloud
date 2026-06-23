/**
 * Modal para registrar o editar un pago al proveedor/operador.
 *
 * ADR-036 punto 4c (2026-06-23):
 * Se agrega un selector OPCIONAL para imputar el pago a un SERVICIO concreto
 * de una reserva. Flujo: elegir reserva (lista con deuda) → elegir un servicio
 * de ESE proveedor en esa reserva → los campos serviceRecordKind + servicePublicId
 * van en el request. Si el usuario no elige servicio, el pago queda a nivel reserva
 * (igual que antes).
 *
 * El selector solo aparece cuando el pago tiene una reservaId seleccionada.
 * Carga los servicios del proveedor usando el endpoint existente de la cuenta
 * del proveedor, filtrando client-side por la reserva elegida.
 *
 * Estrategia de API para listar servicios de una reserva-proveedor:
 *   GET /suppliers/{id}/account/services?search={numeroReserva}&pageSize=100
 *   Luego filtramos client-side por reservaPublicId === reservaElegida.reservaPublicId.
 *   No hay endpoint específico por reserva, pero la página es chica (servicios de
 *   una reserva para un proveedor = generalmente 1-5 items).
 */

import { useState, useEffect } from "react";
import { X, CreditCard, Banknote, Landmark, CheckCircle, AlertCircle, Layers, ChevronDown } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { formatCurrency } from "../lib/utils";
import { getPublicId, getRelatedPublicId } from "../lib/publicIds";

// Icon helper (fuera del componente para evitar re-renders con crash)
const CheckIcon = ({ className }) => (
    <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={className}><path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z" /><polyline points="14 2 14 8 20 8" /><path d="M16 13H8" /><path d="M16 17H8" /><path d="M10 9H8" /></svg>
);

/**
 * Sección opcional que permite imputar un pago a un servicio concreto de una reserva.
 *
 * Aparece solo cuando el usuario ya eligió una reserva en el selector de reservaId.
 * Carga los servicios del proveedor filtrando por la reserva elegida.
 *
 * Props:
 *   supplierId        — publicId del proveedor (para el endpoint account/services)
 *   reservaSeleccionada — objeto { reservaPublicId, numeroReserva } de la lista debt-by-reserva
 *   servicioSeleccionado — { servicePublicId, serviceRecordKind, descripcion } | null
 *   onServicioChange  — callback({ servicePublicId, serviceRecordKind, descripcion } | null)
 */
function SelectorServicioImputacion({ supplierId, reservaSeleccionada, servicioSeleccionado, onServicioChange }) {
    const [servicios, setServicios] = useState([]);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);

    // Cargamos los servicios del proveedor para la reserva elegida.
    // Usamos el endpoint existente con búsqueda por número de reserva,
    // luego filtramos client-side por reservaPublicId.
    useEffect(() => {
        if (!supplierId || !reservaSeleccionada?.reservaPublicId) {
            setServicios([]);
            return;
        }

        let cancelled = false;
        setLoading(true);
        setError(null);

        // Buscamos por número de reserva para acotar el resultado.
        // Si el número de reserva no existe (dato viejo), cargamos sin filtro de búsqueda
        // y filtramos solo por reservaPublicId (que es confiable).
        const params = new URLSearchParams({ pageSize: "100", sortBy: "date", sortDir: "asc" });
        if (reservaSeleccionada.numeroReserva) {
            params.set("search", reservaSeleccionada.numeroReserva);
        }

        api.get(`/suppliers/${supplierId}/account/services?${params.toString()}`)
            .then((response) => {
                if (cancelled) return;
                const items = response?.items || [];
                // Filtro client-side: solo los servicios de ESTA reserva
                const deEstaReserva = items.filter(
                    (s) => String(s.reservaPublicId || "").toLowerCase() ===
                           String(reservaSeleccionada.reservaPublicId || "").toLowerCase()
                );
                setServicios(deEstaReserva);
            })
            .catch((err) => {
                if (cancelled) return;
                console.warn("[SelectorServicioImputacion] No se pudieron cargar los servicios:", err?.message);
                setError("No se pudieron cargar los servicios de esta reserva.");
            })
            .finally(() => {
                if (!cancelled) setLoading(false);
            });

        return () => { cancelled = true; };
    }, [supplierId, reservaSeleccionada?.reservaPublicId, reservaSeleccionada?.numeroReserva]);

    if (loading) {
        return (
            <div className="text-xs text-muted-foreground italic">
                Cargando servicios de la reserva...
            </div>
        );
    }

    if (error) {
        return (
            <div className="text-xs text-amber-600 dark:text-amber-400">
                {error}
            </div>
        );
    }

    if (servicios.length === 0) {
        return (
            <div className="text-xs text-muted-foreground italic">
                Este proveedor no tiene servicios registrados en esta reserva.
            </div>
        );
    }

    // Mapeamos el Type del backend (Hotel/Vuelo/Traslado...) al recordKind del front
    // para poder mandar ServiceRecordKind en el payload.
    // El backend en el campo Type ya usa los mismos valores que ServiceRecordKind
    // pero capitalizados en español (según la implementación de la cuenta del proveedor).
    function tipoARecordKind(tipo) {
        const mapa = {
            "Hotel": "hotel",
            "Vuelo": "flight",
            "Aereo": "flight",
            "Traslado": "transfer",
            "Paquete": "package",
            "Asistencia": "assistance",
        };
        return mapa[tipo] || "generic";
    }

    const handleChange = (e) => {
        const value = e.target.value;
        if (!value) {
            // El usuario eligió "Sin imputar a servicio"
            onServicioChange(null);
            return;
        }

        // value es el publicId del servicio (string uuid)
        const servicioElegido = servicios.find((s) => String(s.publicId) === value);
        if (!servicioElegido) return;

        onServicioChange({
            servicePublicId: String(servicioElegido.publicId),
            serviceRecordKind: tipoARecordKind(servicioElegido.type),
            descripcion: servicioElegido.description || servicioElegido.type,
        });
    };

    const valorActual = servicioSeleccionado?.servicePublicId || "";

    return (
        <div className="space-y-1">
            <label className="text-xs font-medium text-slate-600 dark:text-slate-400">
                Servicio de la reserva (opcional)
            </label>
            <select
                value={valorActual}
                onChange={handleChange}
                className="w-full rounded-lg border border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 text-sm px-3 py-2 focus:ring-2 focus:ring-emerald-500 outline-none"
            >
                <option value="">Sin imputar a un servicio específico</option>
                {servicios.map((s) => (
                    <option key={String(s.publicId)} value={String(s.publicId)}>
                        {s.type}{s.description ? ` — ${s.description}` : ""}
                        {s.date ? ` (${new Date(s.date).toLocaleDateString("es-AR")})` : ""}
                    </option>
                ))}
            </select>
            {servicioSeleccionado && (
                <p className="text-[10px] text-emerald-600 dark:text-emerald-400">
                    El pago se imputará al servicio: {servicioSeleccionado.descripcion}
                </p>
            )}
        </div>
    );
}

export default function SupplierPaymentModal({ isOpen, onClose, onSuccess, supplierId, supplierName = "", currentBalance = 0, editingPayment = null }) {
    if (!isOpen) return null;

    const [bgOpacity, setBgOpacity] = useState("opacity-0");
    const [scale, setScale] = useState("scale-95 opacity-0");

    // Estado del formulario
    const [formData, setFormData] = useState({
        amount: "",
        method: "Transfer",
        reference: "",
        notes: "",
        reservaId: null,
    });

    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);

    // ── ADR-036 4c: imputación a servicio ───────────────────────────────────────
    // reservaSeleccionada: objeto { reservaPublicId, numeroReserva } o null.
    // Se puebla cuando el usuario elige una reserva de la lista de deuda.
    const [reservaSeleccionada, setReservaSeleccionada] = useState(null);

    // servicioSeleccionado: { servicePublicId, serviceRecordKind, descripcion } o null.
    // Si es null, el pago va a nivel reserva (sin imputación por servicio).
    const [servicioSeleccionado, setServicioSeleccionado] = useState(null);

    // Lista de reservas con deuda del proveedor (del endpoint debt-by-reserva).
    // Se carga al abrir el modal para poblar el selector de reservas.
    const [reservasConDeuda, setReservasConDeuda] = useState([]);
    const [reservasLoading, setReservasLoading] = useState(false);

    // Animación de entrada
    useEffect(() => {
        if (isOpen) {
            const timer = setTimeout(() => {
                setBgOpacity("opacity-100");
                setScale("scale-100 opacity-100");
            }, 10);
            return () => clearTimeout(timer);
        }
    }, [isOpen]);

    // Inicialización del form y carga de reservas al abrir
    useEffect(() => {
        if (!isOpen) return;

        if (editingPayment) {
            setFormData({
                amount: editingPayment.amount,
                method: editingPayment.method,
                reference: editingPayment.reference || "",
                notes: editingPayment.notes || "",
                reservaId: getRelatedPublicId(editingPayment, "reservaPublicId", "reservaId"),
            });
            // Al editar, si había servicio imputado, lo reconstruimos desde el DTO de pago
            if (editingPayment.servicePublicId && editingPayment.serviceRecordKind) {
                setServicioSeleccionado({
                    servicePublicId: editingPayment.servicePublicId,
                    serviceRecordKind: editingPayment.serviceRecordKind,
                    descripcion: editingPayment.serviceRecordKind,
                });
            } else {
                setServicioSeleccionado(null);
            }
        } else {
            setFormData({ amount: "", method: "Transfer", reference: "", notes: "", reservaId: null });
            setServicioSeleccionado(null);
        }
        setReservaSeleccionada(null);
        setError(null);

        // Cargar lista de reservas con deuda del proveedor para el selector.
        // Solo si hay supplierId válido (puede fallar si el proveedor no tiene deuda → lista vacía).
        if (!supplierId) return;
        setReservasLoading(true);
        api.get(`/suppliers/${supplierId}/account/debt-by-reserva`)
            .then((response) => {
                setReservasConDeuda(response?.reservas || []);
            })
            .catch((err) => {
                console.warn("[SupplierPaymentModal] No se pudo cargar la lista de reservas:", err?.message);
                setReservasConDeuda([]);
            })
            .finally(() => {
                setReservasLoading(false);
            });
    }, [isOpen, editingPayment, supplierId]);

    // Cuando cambia la reserva elegida, limpiamos la imputación por servicio
    // (la nueva reserva puede no tener el mismo servicio)
    useEffect(() => {
        setServicioSeleccionado(null);
    }, [reservaSeleccionada]);

    const handleClose = () => {
        setBgOpacity("opacity-0");
        setScale("scale-95 opacity-0");
        setTimeout(onClose, 200);
    };

    // Preview de saldo en tiempo real
    const safeBalance = Number(currentBalance) || 0;
    const amountVal = parseFloat(formData.amount) || 0;
    const originalPaymentAmount = editingPayment ? (Number(editingPayment.amount) || 0) : 0;
    const effectiveDebt = safeBalance + originalPaymentAmount;
    const remainingBalance = effectiveDebt - amountVal;

    // No permitir pagar más de la deuda actual (si la deuda es positiva)
    const isOverpaying = remainingBalance < -0.01 && effectiveDebt > 0;

    /**
     * Maneja el cambio en el selector de reserva.
     * Busca el objeto de reserva en la lista y lo guarda en reservaSeleccionada.
     * También actualiza formData.reservaId (que se manda al backend directamente).
     */
    const handleReservaChange = (e) => {
        const publicId = e.target.value;
        if (!publicId) {
            setReservaSeleccionada(null);
            setFormData((prev) => ({ ...prev, reservaId: null }));
            return;
        }

        const encontrada = reservasConDeuda.find(
            (r) => String(r.reservaPublicId) === publicId
        );
        setReservaSeleccionada(encontrada || { reservaPublicId: publicId, numeroReserva: publicId });
        setFormData((prev) => ({ ...prev, reservaId: publicId }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();

        if (amountVal <= 0) {
            setError("El monto debe ser mayor a 0");
            return;
        }

        if (isOverpaying) {
            setError("El pago excede la deuda actual.");
            return;
        }

        setLoading(true);
        setError(null);

        try {
            const payload = {
                amount: amountVal,
                method: formData.method,
                reference: formData.reference,
                notes: formData.notes,
                reservaId: formData.reservaId || null,
                // ADR-036 4c: solo se mandan si el usuario eligió un servicio
                serviceRecordKind: servicioSeleccionado?.serviceRecordKind || null,
                servicePublicId: servicioSeleccionado?.servicePublicId || null,
            };

            if (editingPayment) {
                await api.put(`/suppliers/${supplierId}/payments/${getPublicId(editingPayment)}`, payload);
                showSuccess("Pago actualizado correctamente");
            } else {
                await api.post(`/suppliers/${supplierId}/payments`, payload);
                showSuccess("Pago registrado correctamente");
            }
            onSuccess();
            handleClose();
        } catch (err) {
            console.error(err);
            setError(err.message || "Error al procesar el pago");
        } finally {
            setLoading(false);
        }
    };

    const paymentMethods = [
        { id: "Transfer", label: "Transferencia", icon: Landmark, color: "text-blue-600 bg-blue-50 border-blue-200" },
        { id: "Cash", label: "Efectivo", icon: Banknote, color: "text-green-600 bg-green-50 border-green-200" },
        { id: "Check", label: "Cheque", icon: CheckIcon, color: "text-amber-600 bg-amber-50 border-amber-200" },
        { id: "Card", label: "Tarjeta", icon: CreditCard, color: "text-purple-600 bg-purple-50 border-purple-200" },
    ];

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 text-slate-800 dark:text-slate-100">
            {/* Backdrop */}
            <div className={`fixed inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 ${bgOpacity}`} />

            {/* Contenido del modal */}
            <div className={`relative w-full max-w-lg bg-white dark:bg-slate-900 rounded-2xl shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden transition-all duration-300 transform ${scale} max-h-[90vh] overflow-y-auto`}>

                {/* Header */}
                <div className="flex items-center justify-between px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50">
                    <div>
                        <h2 className="text-xl font-bold text-slate-900 dark:text-white">
                            {editingPayment ? "Editar Pago" : "Registrar Pago"}
                        </h2>
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                            Proveedor: <span className="font-medium text-slate-700 dark:text-slate-300">{supplierName}</span>
                        </p>
                    </div>
                    <button
                        onClick={handleClose}
                        className="p-2 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-6">

                    {/* Preview de saldo */}
                    <div className={`p-4 rounded-xl border transition-all duration-300 ${isOverpaying ? "bg-red-50 border-red-200 dark:bg-red-900/10 dark:border-red-900/30" : "bg-slate-50 border-slate-200 dark:bg-slate-800/50 dark:border-slate-700"}`}>
                        <div className="flex justify-between items-center mb-2">
                            <span className="text-sm text-slate-500 dark:text-slate-400">Deuda Actual</span>
                            <span className="font-mono font-medium">{formatCurrency(effectiveDebt)}</span>
                        </div>
                        <div className="flex justify-between items-center mb-2">
                            <span className="text-sm text-slate-500 dark:text-slate-400">Pago a Realizar</span>
                            <span className={`font-mono font-medium ${isOverpaying ? "text-red-600" : "text-emerald-600"}`}>
                                - {formatCurrency(amountVal)}
                            </span>
                        </div>
                        <div className="h-px bg-slate-200 dark:bg-slate-700 my-2" />
                        <div className="flex justify-between items-center">
                            <span className={`text-sm font-medium ${isOverpaying ? "text-red-600" : "text-slate-700 dark:text-slate-300"}`}>
                                Saldo Restante
                            </span>
                            <span className={`font-mono font-bold text-lg ${isOverpaying ? "text-red-600" : "text-slate-900 dark:text-white"}`}>
                                {formatCurrency(remainingBalance)}
                            </span>
                        </div>
                        {isOverpaying && (
                            <div className="mt-3 flex items-start gap-2 text-red-600 text-xs bg-white dark:bg-red-950/30 p-2 rounded border border-red-100 dark:border-red-900/30">
                                <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                                <p>El monto ingresado supera la deuda total con este proveedor. Por favor corregí el importe.</p>
                            </div>
                        )}
                    </div>

                    {/* Monto */}
                    <div className="space-y-2">
                        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">
                            Monto a Pagar
                        </label>
                        <div className="relative">
                            <span className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400 font-bold">$</span>
                            <input
                                type="number"
                                step="0.01"
                                autoFocus
                                value={formData.amount}
                                onChange={(e) => setFormData({ ...formData, amount: e.target.value })}
                                placeholder="0.00"
                                className={`w-full pl-8 pr-4 py-3 text-lg font-mono rounded-lg border focus:ring-2 focus:border-transparent transition-all outline-none ${isOverpaying ? "border-red-300 focus:ring-red-200 bg-red-50/10" : "border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 focus:ring-emerald-500"}`}
                            />
                        </div>
                    </div>

                    {/* Método de pago */}
                    <div className="space-y-2">
                        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">
                            Método de Pago
                        </label>
                        <div className="grid grid-cols-2 gap-2">
                            {paymentMethods.map((m) => {
                                const Icon = m.icon;
                                const isSelected = formData.method === m.id;
                                return (
                                    <button
                                        key={m.id}
                                        type="button"
                                        onClick={() => setFormData({ ...formData, method: m.id })}
                                        className={`relative flex items-center gap-3 p-3 rounded-lg border transition-all ${isSelected ? "border-emerald-500 bg-emerald-50 dark:bg-emerald-900/20 ring-1 ring-emerald-500" : "border-slate-200 dark:border-slate-700 hover:border-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800"}`}
                                    >
                                        <div className={`p-1.5 rounded-full ${isSelected ? "bg-emerald-100 text-emerald-600" : "bg-slate-100 text-slate-500"}`}>
                                            <Icon className="h-4 w-4" />
                                        </div>
                                        <span className={`text-sm font-medium ${isSelected ? "text-emerald-900 dark:text-emerald-100" : "text-slate-600 dark:text-slate-400"}`}>
                                            {m.label}
                                        </span>
                                        {isSelected && <CheckCircle className="absolute top-2 right-2 h-4 w-4 text-emerald-500" />}
                                    </button>
                                );
                            })}
                        </div>
                    </div>

                    {/* Referencia y Notas */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Referencia</label>
                            <input
                                type="text"
                                value={formData.reference}
                                onChange={(e) => setFormData({ ...formData, reference: e.target.value })}
                                placeholder="# Comprobante"
                                className="w-full px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 text-sm focus:ring-2 focus:ring-emerald-500 outline-none"
                            />
                        </div>
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Notas</label>
                            <input
                                type="text"
                                value={formData.notes}
                                onChange={(e) => setFormData({ ...formData, notes: e.target.value })}
                                placeholder="Opcional..."
                                className="w-full px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 text-sm focus:ring-2 focus:ring-emerald-500 outline-none"
                            />
                        </div>
                    </div>

                    {/* ── ADR-036 4c: Imputar a reserva y servicio (opcional) ─────────── */}
                    <div className="space-y-3 rounded-xl border border-slate-200 dark:border-slate-700 p-4 bg-slate-50/50 dark:bg-slate-800/30">
                        <div className="flex items-center gap-2">
                            <Layers className="h-4 w-4 text-slate-500" />
                            <span className="text-sm font-semibold text-slate-700 dark:text-slate-300">
                                Imputar a una reserva / servicio
                            </span>
                            <span className="text-xs text-slate-400">(opcional)</span>
                        </div>

                        {/* Selector de reserva */}
                        <div className="space-y-1">
                            <label className="text-xs font-medium text-slate-600 dark:text-slate-400">
                                Reserva
                            </label>
                            {reservasLoading ? (
                                <div className="text-xs text-muted-foreground italic">Cargando reservas...</div>
                            ) : (
                                <select
                                    value={formData.reservaId || ""}
                                    onChange={handleReservaChange}
                                    className="w-full rounded-lg border border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 text-sm px-3 py-2 focus:ring-2 focus:ring-emerald-500 outline-none"
                                >
                                    <option value="">Sin imputar a una reserva específica</option>
                                    {reservasConDeuda.map((r) => (
                                        <option key={String(r.reservaPublicId)} value={String(r.reservaPublicId)}>
                                            {r.numeroReserva || "Reserva"}{r.fileName ? ` — ${r.fileName}` : ""}
                                        </option>
                                    ))}
                                    {/* Si la deuda está a cero o no hay reservas: el proveedor puede tener
                                        deuda total pero sin reservas imputadas. Le damos mensaje informativo. */}
                                    {reservasConDeuda.length === 0 && !reservasLoading && (
                                        <option disabled>— No hay reservas con deuda para este proveedor —</option>
                                    )}
                                </select>
                            )}
                        </div>

                        {/* Selector de servicio: aparece cuando hay una reserva elegida */}
                        {reservaSeleccionada && (
                            <SelectorServicioImputacion
                                supplierId={supplierId}
                                reservaSeleccionada={reservaSeleccionada}
                                servicioSeleccionado={servicioSeleccionado}
                                onServicioChange={setServicioSeleccionado}
                            />
                        )}

                        {/* Resumen de la imputación elegida */}
                        {servicioSeleccionado && (
                            <div className="rounded-lg bg-emerald-50 dark:bg-emerald-950/20 border border-emerald-200 dark:border-emerald-800 px-3 py-2 text-xs text-emerald-700 dark:text-emerald-300">
                                Pago imputado al servicio: <strong>{servicioSeleccionado.descripcion}</strong>
                            </div>
                        )}
                    </div>

                    {/* Error */}
                    {error && (
                        <div className="p-3 bg-red-50 text-red-600 text-sm rounded-lg flex items-center gap-2 animate-pulse">
                            <AlertCircle className="h-4 w-4" />
                            {error}
                        </div>
                    )}

                    {/* Acciones */}
                    <div className="flex items-center justify-end gap-3 pt-2">
                        <button
                            type="button"
                            onClick={handleClose}
                            className="px-5 py-2.5 rounded-lg text-sm font-medium text-slate-600 dark:text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={loading || isOverpaying}
                            className={`px-6 py-2.5 rounded-lg text-sm font-bold text-white shadow-lg shadow-emerald-500/20 transition-all hover:scale-[1.02] active:scale-[0.98] ${(loading || isOverpaying)
                                ? "bg-slate-400 cursor-not-allowed shadow-none"
                                : "bg-emerald-600 hover:bg-emerald-700"
                                }`}
                        >
                            {loading ? "Procesando..." : (editingPayment ? "Guardar Cambios" : "Confirmar Pago")}
                        </button>
                    </div>

                </form>
            </div>
        </div>
    );
}
