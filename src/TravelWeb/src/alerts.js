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
    iconTone: "border-indigo-100 bg-indigo-50 text-indigo-600",
    buttonTone: "bg-indigo-600 hover:bg-indigo-700 focus-visible:ring-indigo-500/30",
    eyebrowTone: "text-indigo-600",
    glyph: "?",
  },
  red: {
    iconTone: "border-rose-100 bg-rose-50 text-rose-600",
    buttonTone: "bg-rose-600 hover:bg-rose-700 focus-visible:ring-rose-500/30",
    eyebrowTone: "text-rose-600",
    glyph: "!",
  },
  emerald: {
    iconTone: "border-emerald-100 bg-emerald-50 text-emerald-600",
    buttonTone: "bg-emerald-600 hover:bg-emerald-700 focus-visible:ring-emerald-500/30",
    eyebrowTone: "text-emerald-600",
    glyph: "OK",
  },
  amber: {
    iconTone: "border-amber-100 bg-amber-50 text-amber-600",
    buttonTone: "bg-amber-500 hover:bg-amber-600 focus-visible:ring-amber-500/30",
    eyebrowTone: "text-amber-600",
    glyph: "!",
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

function buildConfirmHtml({ eyebrow, text, details, eyebrowTone }) {
  const textBlock = text
    ? `<p class="text-sm leading-6 text-slate-600">${formatConfirmCopy(text)}</p>`
    : "";
  const detailsBlock = details
    ? `<div class="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-left text-xs leading-5 text-slate-500">${formatConfirmCopy(details)}</div>`
    : "";

  return `
    <div class="space-y-4 text-left">
      <div class="text-[11px] font-black uppercase tracking-[0.24em] ${eyebrowTone}">
        ${escapeHtml(eyebrow || "Confirmacion")}
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
      eyebrowTone: palette.eyebrowTone,
    }),
    iconHtml: `
      <div class="flex h-14 w-14 items-center justify-center rounded-2xl border text-sm font-black uppercase tracking-[0.24em] ${palette.iconTone}">
        ${palette.glyph}
      </div>
    `,
    showCancelButton: true,
    showCloseButton: true,
    focusCancel: true,
    confirmButtonText: options.confirmText || confirmText,
    cancelButtonText: options.cancelText || "Cancelar",
    customClass: {
      popup: "w-full max-w-[30rem] rounded-[28px] border border-slate-200 bg-white p-0 shadow-[0_30px_90px_-32px_rgba(15,23,42,0.35)]",
      icon: "mt-7 mb-0",
      title: "px-7 pt-4 text-left text-[1.45rem] font-black tracking-tight text-slate-950",
      htmlContainer: "mx-0 mt-0 px-7 pb-1 text-left",
      actions: "mt-0 grid grid-cols-1 gap-3 px-7 pb-7 pt-2 sm:grid-cols-2",
      confirmButton: `order-1 inline-flex min-h-11 items-center justify-center rounded-2xl px-5 py-3 text-sm font-bold text-white shadow-sm transition focus-visible:outline-none focus-visible:ring-4 ${palette.buttonTone}`,
      cancelButton: "order-2 inline-flex min-h-11 items-center justify-center rounded-2xl border border-slate-200 bg-white px-5 py-3 text-sm font-semibold text-slate-700 transition hover:bg-slate-50",
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
