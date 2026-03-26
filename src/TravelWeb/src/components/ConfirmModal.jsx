import { AlertTriangle, Info, Trash2, HelpCircle } from "lucide-react";

export default function ConfirmModal({ 
    isOpen, 
    onClose, 
    onConfirm, 
    title = "Confirmar acción", 
    message = "¿Estás seguro de que deseas realizar esta acción?",
    confirmText = "Confirmar",
    cancelText = "Cancelar",
    type = "warning", // 'warning', 'danger', 'info', 'question'
    isLoading = false
}) {
    if (!isOpen) return null;

    const icons = {
        warning: <AlertTriangle className="h-6 w-6 text-amber-600" />,
        danger: <Trash2 className="h-6 w-6 text-red-600" />,
        info: <Info className="h-6 w-6 text-blue-600" />,
        question: <HelpCircle className="h-6 w-6 text-indigo-600" />
    };

    const colors = {
        warning: "bg-amber-500 text-white hover:bg-amber-600 active:bg-amber-700 shadow-amber-200",
        danger: "bg-red-600 text-white hover:bg-red-700 active:bg-red-800 shadow-red-200",
        info: "bg-blue-600 text-white hover:bg-blue-700 active:bg-blue-800 shadow-blue-200",
        question: "bg-indigo-600 text-white hover:bg-indigo-700 active:bg-indigo-800 shadow-indigo-200"
    };

    const iconBg = {
        warning: "bg-amber-100",
        danger: "bg-red-100",
        info: "bg-blue-100",
        question: "bg-indigo-100"
    };

    // Close on backdrop click
    const handleBackdropClick = (e) => {
        if (e.target === e.currentTarget) onClose();
    };

    return (
        <div 
            className="fixed inset-0 z-[100] flex items-center justify-center p-4 sm:p-6 bg-slate-900/60 backdrop-blur-sm animate-in fade-in duration-200"
            onClick={handleBackdropClick}
        >
            <div 
                className="w-full max-w-sm bg-white dark:bg-slate-900 rounded-[2rem] shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden animate-in zoom-in-95 duration-200"
                role="dialog"
                aria-modal="true"
            >
                <div className="p-8 text-center">
                    <div className={`mx-auto mb-5 flex h-16 w-16 items-center justify-center rounded-2xl ${iconBg[type]}`}>
                        {icons[type]}
                    </div>
                    <h3 className="mb-2 text-xl font-bold text-slate-900 dark:text-white">
                        {title}
                    </h3>
                    <p className="text-sm text-slate-500 dark:text-slate-400 leading-relaxed">
                        {message}
                    </p>
                </div>
                
                <div className="flex flex-col sm:flex-row gap-3 p-8 pt-0">
                    <button
                        onClick={onClose}
                        disabled={isLoading}
                        className="flex-1 rounded-2xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 px-4 py-3 text-sm font-semibold text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                    >
                        {cancelText}
                    </button>
                    <button
                        onClick={onConfirm}
                        disabled={isLoading}
                        className={`flex-1 rounded-2xl px-4 py-3 text-sm font-bold shadow-xl transition-all active:scale-95 disabled:opacity-50 flex items-center justify-center gap-2 ${colors[type]}`}
                    >
                        {isLoading ? (
                            <div className="h-4 w-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                        ) : null}
                        {isLoading ? "Procesando..." : confirmText}
                    </button>
                </div>
            </div>
        </div>
    );
}
