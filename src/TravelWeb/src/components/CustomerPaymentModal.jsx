import { useState, useEffect } from "react";
import { formatCurrency } from "../lib/utils";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { X, CreditCard, Calendar, FileText, DollarSign, AlignLeft, CheckCircle2 } from "lucide-react";
import { getPublicId, getRelatedPublicId } from "../lib/publicIds";

export default function CustomerPaymentModal({ isOpen, onClose, customerId, paymentToEdit, onSave, availableReservas = [] }) {
    const [formData, setFormData] = useState({
        amount: "",
        method: "Transfer",
        paidAt: new Date().toISOString().split("T")[0],
        notes: "",
        reservaPublicId: ""
    });
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (isOpen) {
            if (paymentToEdit) {
                setFormData({
                    amount: paymentToEdit.amount,
                    method: paymentToEdit.method,
                    paidAt: paymentToEdit.paidAt ? new Date(paymentToEdit.paidAt).toISOString().split("T")[0] : new Date().toISOString().split("T")[0],
                    notes: paymentToEdit.notes || "",
                    reservaPublicId: getRelatedPublicId(paymentToEdit, "reservaPublicId", "reservaId") || ""
                });
            } else {
                setFormData({
                    amount: "",
                    method: "Transfer",
                    paidAt: new Date().toISOString().split("T")[0],
                    notes: "",
                    reservaPublicId: availableReservas.length > 0 ? getPublicId(availableReservas[0]) || "" : ""
                });
            }
        }
    }, [isOpen, paymentToEdit, availableReservas]);

    if (!isOpen) return null;

    const handleSubmit = async (e) => {
        e.preventDefault();
        if (!formData.reservaPublicId) {
            showError("Debe seleccionar una reserva");
            return;
        }

        setLoading(true);
        try {
            const payload = {
                amount: parseFloat(formData.amount),
                method: formData.method,
                paidAt: new Date(formData.paidAt).toISOString(),
                notes: formData.notes
            };

            if (paymentToEdit) {
                // Edit existing payment
                await api.put(`/reservas/${formData.reservaPublicId}/payments/${getPublicId(paymentToEdit)}`, payload);
            } else {
                // Create new payment
                await api.post(`/reservas/${formData.reservaPublicId}/payments`, payload);
            }

            onSave();
            onClose();
            showSuccess(paymentToEdit ? "El pago se actualizo correctamente." : "La cobranza se registro correctamente.");
        } catch (error) {
            console.error(error);
            showError(error.response?.data || "No se pudo guardar el pago");
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-md rounded-xl border bg-card shadow-2xl overflow-hidden scale-100 animate-in zoom-in-95 duration-200">
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div>
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                            {paymentToEdit ? "Editar Pago" : "Nueva Cobranza"}
                        </h3>
                        <p className="text-sm text-muted-foreground">
                            {paymentToEdit ? "Modificar detalles del pago" : "Registrar ingreso de dinero"}
                        </p>
                    </div>
                    <button onClick={onClose} className="text-slate-400 hover:text-slate-500">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-4">
                    {/* File Selection */}
                    <div className="space-y-1.5">
                        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Reserva a Imputar</label>
                        <div className="relative">
                            <FileText className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                            <select
                                disabled={!!paymentToEdit} // Cannot change file when editing
                                value={formData.reservaPublicId}
                                onChange={(e) => setFormData({ ...formData, reservaPublicId: e.target.value })}
                                className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500 disabled:opacity-50"
                            >
                                <option value="">Seleccionar Reserva...</option>
                                {availableReservas.map(reserva => (
                                    <option key={getPublicId(reserva)} value={getPublicId(reserva)}>
                                        {reserva.numeroReserva} - {reserva.name} (Saldo: {formatCurrency(reserva.balance)})
                                    </option>
                                ))}
                            </select>
                        </div>
                        {paymentToEdit && <p className="text-xs text-muted-foreground">No se puede cambiar la reserva de un pago ya registrado.</p>}
                    </div>

                    <div className="grid grid-cols-2 gap-4">
                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Monto</label>
                            <div className="relative">
                                <DollarSign className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                <input
                                    type="number"
                                    required
                                    min="0.01"
                                    step="0.01"
                                    value={formData.amount}
                                    onChange={(e) => setFormData({ ...formData, amount: e.target.value })}
                                    className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                                    placeholder="0.00"
                                />
                            </div>
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Fecha</label>
                            <div className="relative">
                                <Calendar className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                <input
                                    type="date"
                                    required
                                    value={formData.paidAt}
                                    onChange={(e) => setFormData({ ...formData, paidAt: e.target.value })}
                                    className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                                />
                            </div>
                        </div>
                    </div>

                    <div className="space-y-1.5">
                        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Método de Pago</label>
                        <div className="relative">
                            <CreditCard className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                            <select
                                value={formData.method}
                                onChange={(e) => setFormData({ ...formData, method: e.target.value })}
                                className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                            >
                                <option value="Transfer">Transferencia</option>
                                <option value="Cash">Efectivo</option>
                                <option value="Card">Tarjeta</option>
                                <option value="Cheque">Cheque</option>
                                <option value="Deposit">Depósito</option>
                            </select>
                        </div>
                    </div>

                    <div className="space-y-1.5">
                        <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Notas (Opcional)</label>
                        <div className="relative">
                            <AlignLeft className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                            <textarea
                                rows={2}
                                value={formData.notes}
                                onChange={(e) => setFormData({ ...formData, notes: e.target.value })}
                                className="w-full rounded-md border border-input bg-background pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500 resize-none"
                                placeholder="Referencia, nro de comprobante..."
                            />
                        </div>
                    </div>

                    <div className="pt-2 flex gap-3">
                        <button
                            type="button"
                            onClick={onClose}
                            className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={loading}
                            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm transition-colors disabled:opacity-50 flex items-center justify-center gap-2"
                        >
                            {loading ? "Guardando..." : (
                                <>
                                    <CheckCircle2 className="h-4 w-4" />
                                    {paymentToEdit ? "Guardar Cambios" : "Registrar Pago"}
                                </>
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
