import { useState, useEffect } from "react";
import { X, DollarSign, CreditCard, Banknote, Landmark, CheckCircle2, AlertCircle } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { formatCurrency } from "../lib/utils";

export default function SupplierPaymentModal({ isOpen, onClose, onSuccess, supplierId, supplierName, currentBalance, editingPayment = null }) {
    if (!isOpen) return null;

    const [bgOpacity, setBgOpacity] = useState("opacity-0");
    const [scale, setScale] = useState("scale-95 opacity-0");

    // Form State
    const [formData, setFormData] = useState({
        amount: "",
        method: "Transfer",
        reference: "",
        notes: "",
        travelFileId: null
    });

    const [loading, setLoading] = useState(false);
    const [error, setError] = useState(null);

    // Initial Load
    useEffect(() => {
        if (isOpen) {
            setTimeout(() => {
                setBgOpacity("opacity-100");
                setScale("scale-100 opacity-100");
            }, 10);

            if (editingPayment) {
                setFormData({
                    amount: editingPayment.amount,
                    method: editingPayment.method,
                    reference: editingPayment.reference || "",
                    notes: editingPayment.notes || "",
                    travelFileId: editingPayment.travelFileId
                });
            } else {
                setFormData({ amount: "", method: "Transfer", reference: "", notes: "", travelFileId: null });
            }
            setError(null);
        }
    }, [isOpen, editingPayment]);

    const handleClose = () => {
        setBgOpacity("opacity-0");
        setScale("scale-95 opacity-0");
        setTimeout(onClose, 200);
    };

    // Calculate Real-Time Balance Preview
    const amountVal = parseFloat(formData.amount) || 0;

    // If editing, we first "restore" the original payment amount to the debt to see the true debt
    const originalPaymentAmount = editingPayment ? editingPayment.amount : 0;
    const effectiveDebt = currentBalance + originalPaymentAmount;

    const remainingBalance = effectiveDebt - amountVal;
    const isOverpaying = remainingBalance < 0;

    const handleSubmit = async (e) => {
        e.preventDefault();

        if (amountVal <= 0) {
            setError("El monto debe ser mayor a 0");
            return;
        }

        if (isOverpaying) {
            setError("El pago excede la deuda actual. No se permiten saldos negativos.");
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
                travelFileId: formData.travelFileId
            };

            if (editingPayment) {
                await api.put(`/suppliers/${supplierId}/payments/${editingPayment.id}`, payload);
                showSuccess("Pago actualizado correctamente");
            } else {
                await api.post(`/suppliers/${supplierId}/payments`, payload);
                showSuccess("Pago registrado correctamente");
            }
            onSuccess();
            handleClose();
        } catch (err) {
            console.error(err);
            setError(err.response?.data?.message || err.response?.data || "Error al procesar el pago");
        } finally {
            setLoading(false);
        }
    };

    const paymentMethods = [
        { id: 'Transfer', label: 'Transferencia', icon: Landmark, color: 'text-blue-600 bg-blue-50 border-blue-200' },
        { id: 'Cash', label: 'Efectivo', icon: Banknote, color: 'text-green-600 bg-green-50 border-green-200' },
        { id: 'Check', label: 'Cheque', icon: FileText, color: 'text-amber-600 bg-amber-50 border-amber-200' },
        { id: 'Card', label: 'Tarjeta', icon: CreditCard, color: 'text-purple-600 bg-purple-50 border-purple-200' },
    ];

    // Icon component helper
    const FileText = ({ className }) => (
        <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className={className}><path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z" /><polyline points="14 2 14 8 20 8" /><path d="M16 13H8" /><path d="M16 17H8" /><path d="M10 9H8" /></svg>
    );

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 text-slate-800 dark:text-slate-100">
            {/* Backdrop */}
            <div
                className={`fixed inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 ${bgOpacity}`}
                onClick={handleClose}
            />

            {/* Modal Content */}
            <div className={`relative w-full max-w-lg bg-white dark:bg-slate-900 rounded-2xl shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden transition-all duration-300 transform ${scale}`}>

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

                    {/* Balance Preview Card */}
                    <div className={`p-4 rounded-xl border transition-all duration-300 ${isOverpaying ? 'bg-red-50 border-red-200 dark:bg-red-900/10 dark:border-red-900/30' : 'bg-slate-50 border-slate-200 dark:bg-slate-800/50 dark:border-slate-700'}`}>
                        <div className="flex justify-between items-center mb-2">
                            <span className="text-sm text-slate-500 dark:text-slate-400">Deuda Actual</span>
                            <span className="font-mono font-medium">{formatCurrency(effectiveDebt)}</span>
                        </div>
                        <div className="flex justify-between items-center mb-2">
                            <span className="text-sm text-slate-500 dark:text-slate-400">Pago a Realizar</span>
                            <span className={`font-mono font-medium ${isOverpaying ? 'text-red-600' : 'text-emerald-600'}`}>
                                - {formatCurrency(amountVal)}
                            </span>
                        </div>
                        <div className="h-px bg-slate-200 dark:bg-slate-700 my-2" />
                        <div className="flex justify-between items-center">
                            <span className={`text-sm font-medium ${isOverpaying ? 'text-red-600' : 'text-slate-700 dark:text-slate-300'}`}>
                                Saldo Restante
                            </span>
                            <span className={`font-mono font-bold text-lg ${isOverpaying ? 'text-red-600' : 'text-slate-900 dark:text-white'}`}>
                                {formatCurrency(remainingBalance)}
                            </span>
                        </div>
                        {isOverpaying && (
                            <div className="mt-3 flex items-start gap-2 text-red-600 text-xs bg-white dark:bg-red-950/30 p-2 rounded border border-red-100 dark:border-red-900/30">
                                <AlertCircle className="h-4 w-4 shrink-0 mt-0.5" />
                                <p>El monto ingresado supera la deuda total con este proveedor. Por favor corrige el importe.</p>
                            </div>
                        )}
                    </div>

                    {/* Amount Input */}
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
                                className={`w-full pl-8 pr-4 py-3 text-lg font-mono rounded-lg border focus:ring-2 focus:border-transparent transition-all outline-none ${isOverpaying ? 'border-red-300 focus:ring-red-200 bg-red-50/10' : 'border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 focus:ring-emerald-500'}`}
                            />
                        </div>
                    </div>

                    {/* Method Selection */}
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
                                        className={`relative flex items-center gap-3 p-3 rounded-lg border transition-all ${isSelected ? `border-emerald-500 bg-emerald-50 dark:bg-emerald-900/20 ring-1 ring-emerald-500` : 'border-slate-200 dark:border-slate-700 hover:border-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800'}`}
                                    >
                                        <div className={`p-1.5 rounded-full ${isSelected ? 'bg-emerald-100 text-emerald-600' : 'bg-slate-100 text-slate-500'}`}>
                                            <Icon className="h-4 w-4" />
                                        </div>
                                        <span className={`text-sm font-medium ${isSelected ? 'text-emerald-900 dark:text-emerald-100' : 'text-slate-600 dark:text-slate-400'}`}>
                                            {m.label}
                                        </span>
                                        {isSelected && <CheckCircle2 className="absolute top-2 right-2 h-4 w-4 text-emerald-500" />}
                                    </button>
                                )
                            })}
                        </div>
                    </div>

                    {/* Reference & Notes */}
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

                    {/* Error Message */}
                    {error && (
                        <div className="p-3 bg-red-50 text-red-600 text-sm rounded-lg flex items-center gap-2 animate-pulse">
                            <AlertCircle className="h-4 w-4" />
                            {error}
                        </div>
                    )}

                    {/* Footer Actions */}
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
                                    ? 'bg-slate-400 cursor-not-allowed shadow-none'
                                    : 'bg-emerald-600 hover:bg-emerald-700'
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
