export function getPublicId(entity) {
  if (!entity) {
    return null;
  }

  if (typeof entity === "string") {
    return entity;
  }

  return entity.publicId ?? entity.PublicId ?? entity.id ?? entity.Id ?? null;
}

export function getRelatedPublicId(entity, publicKey, legacyKey) {
  if (!entity) {
    return null;
  }

  return entity[publicKey] ?? null;
}

export function hasPublicId(value) {
  return value !== null && value !== undefined && value !== "";
}
