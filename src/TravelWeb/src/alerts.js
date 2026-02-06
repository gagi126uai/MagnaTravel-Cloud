import Swal from "sweetalert2";

// Common Toast Mixin (Top Right Notification)
const Toast = Swal.mixin({
  toast: true,
  position: "top-end",
  showConfirmButton: false,
  timer: 3000,
  timerProgressBar: true,
  didOpen: (toast) => {
    toast.onmouseenter = Swal.stopTimer;
    toast.onmouseleave = Swal.resumeTimer;
  },
  customClass: {
    popup: "colored-toast dark:bg-slate-800 dark:text-white"
  }
});

// Base styles using Tailwind classes via customClass
const baseOptions = {
  customClass: {
    popup: "rounded-2xl shadow-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-slate-800 dark:text-slate-100",
    title: "text-xl font-bold text-slate-900 dark:text-white",
    htmlContainer: "text-slate-600 dark:text-slate-300",
    confirmButton: "px-4 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white font-medium focus:ring-4 focus:ring-indigo-500/20",
    cancelButton: "px-4 py-2 rounded-lg bg-slate-200 hover:bg-slate-300 dark:bg-slate-700 dark:hover:bg-slate-600 text-slate-700 dark:text-slate-200 font-medium",
    denyButton: "px-4 py-2 rounded-lg bg-red-600 hover:bg-red-700 text-white",
  },
  buttonsStyling: false, // Important to use Tailwind classes
  backdrop: "rgba(15, 23, 42, 0.6) backdrop-blur-sm", // More professional backdrop
};

export function showSuccess(message, title = "¡Listo!") {
  return Swal.fire({
    ...baseOptions,
    icon: "success",
    title: title,
    text: message,
    timer: 2500,
    timerProgressBar: true
  });
}

export function showError(message, title = "Atención") {
  return Swal.fire({
    ...baseOptions,
    icon: "error",
    title: title,
    text: message,
    confirmButtonText: "Entendido"
  });
}

export function showInfo(message, title = "Información") {
  return Swal.fire({
    ...baseOptions,
    icon: "info",
    title: title,
    text: message,
  });
}

export function showWarning(message, title = "Advertencia") {
  return Swal.fire({
    ...baseOptions,
    icon: "warning",
    title: title,
    text: message,
  });
}

export async function showConfirm(title, text, confirmText = "Sí, confirmar", confirmColor = "indigo") {
  // Dynamically adjust confirm button color
  const confirmClass = confirmColor === 'red'
    ? "px-4 py-2 rounded-lg bg-red-600 hover:bg-red-700 text-white font-medium focus:ring-4 focus:ring-red-500/20"
    : "px-4 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white font-medium focus:ring-4 focus:ring-indigo-500/20";

  const result = await Swal.fire({
    ...baseOptions,
    title: title,
    text: text,
    icon: "question",
    showCancelButton: true,
    confirmButtonText: confirmText,
    cancelButtonText: "Cancelar",
    customClass: {
      ...baseOptions.customClass,
      confirmButton: confirmClass
    }
  });
  return result.isConfirmed;
}

export function showToastSuccess(message) {
  Toast.fire({
    icon: "success",
    title: message
  });
}

export function showToastError(message) {
  Toast.fire({
    icon: "error",
    title: message
  });
}
