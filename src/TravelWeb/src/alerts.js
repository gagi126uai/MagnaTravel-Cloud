import { toast } from "sonner";
import Swal from "sweetalert2";

export function showSuccess(message, title = "Listo") {
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

export function showInfo(message, title = "Informacion") {
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

const confirmPalettes = {
  indigo: {
    badgeTone: "bg-indigo-50 text-indigo-600",
    buttonTone: "bg-indigo-600 hover:bg-indigo-700 focus-visible:ring-indigo-500/30",
  },
  red: {
    badgeTone: "bg-rose-50 text-rose-600",
    buttonTone: "bg-rose-600 hover:bg-rose-700 focus-visible:ring-rose-500/30",
  },
  emerald: {
    badgeTone: "bg-emerald-50 text-emerald-600",
    buttonTone: "bg-emerald-600 hover:bg-emerald-700 focus-visible:ring-emerald-500/30",
  },
  amber: {
    badgeTone: "bg-amber-50 text-amber-600",
    buttonTone: "bg-amber-500 hover:bg-amber-600 focus-visible:ring-amber-500/30",
  },
};

function escapeHtml(value = "") {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function formatConfirmCopy(value = "") {
  return escapeHtml(value).replace(/\n/g, "<br />");
}

function buildConfirmHtml({ eyebrow, text, details, badgeTone }) {
  const eyebrowLabel = eyebrow || "Confirmacion";
  const textBlock = text
    ? `<p class="text-sm leading-6 text-slate-600">${formatConfirmCopy(text)}</p>`
    : "";
  const detailsBlock = details
    ? `<div class="rounded-xl border border-slate-200 bg-slate-50 px-3.5 py-3 text-xs leading-5 text-slate-500">${formatConfirmCopy(details)}</div>`
    : "";

  return `
    <div class="space-y-4 text-left">
      <div class="inline-flex items-center rounded-full px-2.5 py-1 text-[10px] font-bold uppercase tracking-[0.18em] ${badgeTone}">
        ${escapeHtml(eyebrowLabel)}
      </div>
      ${textBlock}
      ${detailsBlock}
    </div>
  `;
}

export async function showConfirm(input, text, confirmText = "Si, confirmar", confirmColor = "indigo") {
  const options =
    typeof input === "object" && input !== null
      ? input
      : {
          title: input,
          text,
          confirmText,
          confirmColor,
        };

  const palette = confirmPalettes[options.confirmColor] || confirmPalettes.indigo;

  const result = await Swal.fire({
    title: options.title || "Confirmar accion",
    html: buildConfirmHtml({
      eyebrow: options.eyebrow,
      text: options.text,
      details: options.details,
      badgeTone: palette.badgeTone,
    }),
    showCancelButton: true,
    showCloseButton: true,
    focusCancel: true,
    confirmButtonText: options.confirmText || confirmText,
    cancelButtonText: options.cancelText || "Cancelar",
    customClass: {
      popup: "w-full max-w-[26rem] rounded-2xl border border-slate-200 bg-white p-0 shadow-[0_24px_64px_-28px_rgba(15,23,42,0.45)]",
      title: "px-6 pt-6 text-left text-xl font-bold tracking-tight text-slate-950",
      htmlContainer: "mx-0 mt-0 px-6 pb-0 text-left",
      actions: "mt-0 flex flex-col-reverse gap-2 px-6 pb-6 pt-4 sm:flex-row sm:justify-end",
      confirmButton: `inline-flex min-h-10 items-center justify-center rounded-xl px-4 py-2.5 text-sm font-semibold text-white transition focus-visible:outline-none focus-visible:ring-4 ${palette.buttonTone}`,
      cancelButton: "inline-flex min-h-10 items-center justify-center rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50",
      closeButton: "text-slate-300 transition hover:!text-slate-500 focus:!shadow-none focus:!outline-none",
    },
    buttonsStyling: false,
  });

  return result.isConfirmed;
}

export function showToastSuccess(message) {
  toast.success(message);
}

export function showToastError(message) {
  toast.error(message);
}
