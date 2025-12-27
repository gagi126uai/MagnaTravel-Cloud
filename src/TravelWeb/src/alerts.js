import Swal from "sweetalert2";

const baseOptions = {
  background: "#0f172a",
  color: "#e2e8f0",
  confirmButtonColor: "#6366f1",
  backdrop: "rgba(15, 23, 42, 0.7)",
};

export function showSuccess(message) {
  return Swal.fire({
    ...baseOptions,
    icon: "success",
    title: "Listo",
    text: message,
  });
}

export function showError(message) {
  return Swal.fire({
    ...baseOptions,
    icon: "error",
    title: "Error",
    text: message,
  });
}

export function showInfo(message) {
  return Swal.fire({
    ...baseOptions,
    icon: "info",
    title: "Aviso",
    text: message,
  });
}
