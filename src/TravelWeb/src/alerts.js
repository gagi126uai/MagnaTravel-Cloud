import { toast } from "sonner";
import Swal from "sweetalert2"; // Mantener solo para diálogos de confirmación complejos si es necesario, o migrar todo.

// Modern Toast Notifications using Sonner
// Documentation: https://sonner.emilkowal.ski/

export function showSuccess(message, title = "¡Listo!") {
  toast.success(title, {
    description: message,
    duration: 3000,
  });
}

export function showError(message, title = "Error") {
  toast.error(title, {
    description: message,
    duration: 4000,
  });
}

export function showInfo(message, title = "Información") {
  toast.info(title, {
    description: message,
    duration: 3000,
  });
}

export function showWarning(message, title = "Advertencia") {
  toast.warning(title, {
    description: message,
    duration: 3500,
  });
}

// Keep SweetAlert ONLY for strictly modal confirmations that require blocking interaction
// Sonner is for toasts, SweetAlert for Modals.
export async function showConfirm(title, text, confirmText = "Sí, confirmar", confirmColor = "indigo") {
  const result = await Swal.fire({
    title: title,
    text: text,
    icon: "question",
    showCancelButton: true,
    confirmButtonText: confirmText,
    cancelButtonText: "Cancelar",
    customClass: {
      popup: "rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900",
      title: "text-lg font-bold text-slate-900 dark:text-white",
      htmlContainer: "text-slate-600 dark:text-slate-300",
      confirmButton: `px-4 py-2 rounded-lg text-white font-medium ${confirmColor === 'red' ? 'bg-red-600 hover:bg-red-700' : 'bg-indigo-600 hover:bg-indigo-700'}`,
      cancelButton: "px-4 py-2 rounded-lg bg-slate-100 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700 text-slate-700 dark:text-slate-300 font-medium",
      actions: "gap-2"
    },
    buttonsStyling: false
  });
  return result.isConfirmed;
}

// Compatibility layer for legacy calls
export function showToastSuccess(message) {
  toast.success(message);
}

export function showToastError(message) {
  toast.error(message);
}
