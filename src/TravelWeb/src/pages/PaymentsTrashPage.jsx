import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { formatCurrency, formatDate } from "../lib/utils";
import {
    Trash2,
    RotateCcw,
    CreditCard,
    FolderOpen,
    User,
    AlertCircle,
    Search,
    Loader2
} from "lucide-react";
import { Button } from "../components/ui/button";
import Swal from "sweetalert2";

export default function PaymentsTrashPage() {
    const [payments, setPayments] = useState([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        loadTrash();
    }, []);

    const loadTrash = async () => {
        setLoading(true);
        try {
            const data = await api.get("/payments/trash");
            setPayments(data);
        } catch (error) {
            showError("Error cargando papelera de pagos.");
        } finally {
            setLoading(false);
        }
    };

    const handleRestore = async (payment) => {
        const result = await Swal.fire({
            title: "¿Restaurar este pago?",
            html: `<p>El pago de <b>${formatCurrency(payment.amount)}</b> volverá a estar activo.</p>
                   <p class="text-sm text-gray-500 mt-1">Expediente: ${payment.fileNumber || "N/A"}</p>`,
            icon: "question",
            showCancelButton: true,
            confirmButtonText: "Sí, restaurar",
            cancelButtonText: "Cancelar",
            confirmButtonColor: "#10b981"
        });

        if (result.isConfirmed) {
            try {
                await api.put(`/payments/${payment.id}/restore`);
                showSuccess("Pago restaurado exitosamente.");
                loadTrash();
            } catch (error) {
                showError("No se pudo restaurar el pago.");
            }
        }
    };

    if (loading) {
        return (
            <div className="flex items-center justify-center min-h-[400px]">
                <div className="flex flex-col items-center gap-4">
                    <Loader2 className="h-10 w-10 animate-spin text-indigo-600" />
                    <p className="text-sm text-muted-foreground animate-pulse">Cargando papelera...</p>
                </div>
            </div>
        );
    }

    return (
        <div className="space-y-6 animate-in fade-in duration-500">
            <div>
                <h2 className="text-2xl font-bold tracking-tight flex items-center gap-2">
                    <Trash2 className="h-6 w-6 text-rose-500" />
                    Papelera de Pagos
                </h2>
                <p className="text-sm text-muted-foreground mt-1">
                    Pagos eliminados que pueden ser restaurados. Los datos se conservan para auditoría.
                </p>
            </div>

            {payments.length > 0 ? (
                <div className="rounded-xl border border-slate-200 bg-white shadow-sm overflow-hidden dark:bg-slate-900 dark:border-slate-800">
                    <div className="overflow-x-auto">
                        <table className="w-full text-left text-sm min-w-[700px]">
                            <thead className="bg-slate-50 border-b border-slate-200 dark:bg-slate-950 dark:border-slate-800">
                                <tr>
                                    <th className="px-6 py-3 font-medium text-slate-500">Monto</th>
                                    <th className="px-6 py-3 font-medium text-slate-500">Método</th>
                                    <th className="px-6 py-3 font-medium text-slate-500">Expediente</th>
                                    <th className="px-6 py-3 font-medium text-slate-500">Cliente</th>
                                    <th className="px-6 py-3 font-medium text-slate-500">Fecha Pago</th>
                                    <th className="px-6 py-3 font-medium text-slate-500">Eliminado</th>
                                    <th className="px-6 py-3 font-medium text-slate-500 w-[100px]"></th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                {payments.map((p) => (
                                    <tr key={p.id} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors">
                                        <td className="px-6 py-4">
                                            <div className="flex items-center gap-2">
                                                <div className="h-8 w-8 rounded-full bg-rose-100 dark:bg-rose-900/30 flex items-center justify-center">
                                                    <CreditCard className="h-4 w-4 text-rose-600 dark:text-rose-400" />
                                                </div>
                                                <span className="font-mono font-bold text-rose-600 dark:text-rose-400 line-through">
                                                    {formatCurrency(p.amount)}
                                                </span>
                                            </div>
                                        </td>
                                        <td className="px-6 py-4 text-slate-600 dark:text-slate-400">{p.method}</td>
                                        <td className="px-6 py-4">
                                            {p.fileNumber ? (
                                                <div className="flex items-center gap-1.5">
                                                    <FolderOpen className="h-3.5 w-3.5 text-slate-400" />
                                                    <span className="font-mono text-xs">{p.fileNumber}</span>
                                                    {p.fileName && <span className="text-slate-400 text-xs">— {p.fileName}</span>}
                                                </div>
                                            ) : (
                                                <span className="text-slate-400">-</span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4">
                                            {p.customerName ? (
                                                <div className="flex items-center gap-1.5">
                                                    <User className="h-3.5 w-3.5 text-slate-400" />
                                                    <span className="text-sm">{p.customerName}</span>
                                                </div>
                                            ) : (
                                                <span className="text-slate-400">-</span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4 text-slate-600 dark:text-slate-400 text-sm">
                                            {formatDate(p.paidAt)}
                                        </td>
                                        <td className="px-6 py-4">
                                            <span className="text-xs text-rose-500 font-medium">
                                                {formatDate(p.deletedAt)}
                                            </span>
                                        </td>
                                        <td className="px-6 py-4 text-right">
                                            <Button
                                                size="sm"
                                                variant="outline"
                                                onClick={() => handleRestore(p)}
                                                className="gap-1.5 text-emerald-600 border-emerald-200 hover:bg-emerald-50 hover:text-emerald-700 dark:text-emerald-400 dark:border-emerald-800 dark:hover:bg-emerald-900/20"
                                            >
                                                <RotateCcw className="h-3.5 w-3.5" />
                                                Restaurar
                                            </Button>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                </div>
            ) : (
                <div className="rounded-xl border border-dashed border-slate-300 dark:border-slate-700 p-12 text-center">
                    <div className="mx-auto h-14 w-14 rounded-full bg-emerald-50 dark:bg-emerald-900/20 flex items-center justify-center mb-4">
                        <Trash2 className="h-7 w-7 text-emerald-400" />
                    </div>
                    <h3 className="text-lg font-semibold text-slate-700 dark:text-slate-300">Papelera vacía</h3>
                    <p className="text-sm text-muted-foreground mt-1">No hay pagos eliminados. ¡Todo limpio!</p>
                </div>
            )}

            {/* Info banner */}
            <div className="flex items-start gap-3 p-4 rounded-lg bg-amber-50 dark:bg-amber-900/10 border border-amber-200 dark:border-amber-800">
                <AlertCircle className="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5" />
                <div className="text-sm text-amber-800 dark:text-amber-300">
                    <p className="font-medium">Sobre la papelera de pagos</p>
                    <p className="mt-1 opacity-80">
                        Los pagos eliminados se conservan como medida de seguridad. Al restaurar un pago,
                        este volverá a impactar en el saldo del expediente correspondiente.
                    </p>
                </div>
            </div>
        </div>
    );
}
