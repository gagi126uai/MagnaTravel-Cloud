export function isDatabaseUnavailableError(error) {
  return error?.code === "database_unavailable" || error?.status === 503;
}
