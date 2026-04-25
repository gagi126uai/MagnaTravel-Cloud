export function isDatabaseUnavailableError(error) {
  return error?.code === "database_unavailable" || error?.status === 503;
}

function tryParseJsonString(value) {
  const trimmed = value.trim();
  if (!trimmed || (!trimmed.startsWith("{") && !trimmed.startsWith("["))) {
    return null;
  }

  try {
    return JSON.parse(trimmed);
  } catch {
    return null;
  }
}

function normalizeValidationErrors(errors) {
  if (!errors || typeof errors !== "object") {
    return "";
  }

  return Object.values(errors)
    .flat()
    .filter(Boolean)
    .join("\n");
}

export function normalizeMessage(value, fallback = "Error desconocido") {
  if (value === null || value === undefined || value === "") {
    return fallback;
  }

  if (typeof value === "string") {
    const parsed = tryParseJsonString(value);
    if (parsed !== null) {
      return normalizeMessage(parsed, fallback);
    }

    return value;
  }

  if (value instanceof Error) {
    return getApiErrorMessage(value, fallback);
  }

  if (Array.isArray(value)) {
    const message = value.map((item) => normalizeMessage(item, "")).filter(Boolean).join(", ");
    return message || fallback;
  }

  if (typeof value === "object") {
    const validationMessage = normalizeValidationErrors(value.errors);
    if (validationMessage) {
      return validationMessage;
    }

    return (
      normalizeMessage(value.message, "") ||
      normalizeMessage(value.error, "") ||
      normalizeMessage(value.detail, "") ||
      normalizeMessage(value.title, "") ||
      fallback
    );
  }

  return String(value);
}

export function getApiErrorMessage(error, fallback = "Error desconocido") {
  if (!error) {
    return fallback;
  }

  if (error.payload !== undefined && error.payload !== null) {
    const payloadMessage = normalizeMessage(error.payload, "");
    if (payloadMessage) {
      return payloadMessage;
    }
  }

  return normalizeMessage(error.message || error, fallback);
}
