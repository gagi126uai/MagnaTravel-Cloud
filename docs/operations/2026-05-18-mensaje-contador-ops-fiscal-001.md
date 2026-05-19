# Mensaje para contador + arca-tax-expert — Signoff OPS-FISCAL-001

## Para que sirve este documento

Es el texto que Gaston (o quien gestione el deploy) le manda al contador y al
asesor fiscal de la agencia para obtener firma sobre el comportamiento del
nuevo modulo de cancelacion de reservas. Sin esta firma, el feature flag
`EnableNewCancellationFlow` NO se debe prender en produccion.

---

## Mensaje sugerido (copiar y pegar al contador)

> Hola [nombre del contador].
>
> Estamos por activar en MagnaTravel el modulo nuevo de cancelacion de
> reservas + reintegros del operador + creditos del cliente. Antes de
> prenderlo en produccion necesitamos que firmes una decision fiscal.
>
> **La decision en una linea:**
>
> Cuando un usuario cancela una reserva y emite una nota de credito (NC)
> fiscal, queremos que el flujo NO pida una aprobacion separada para la
> NC SI el usuario ya tuvo una aprobacion explicita para anular la
> reserva.
>
> **El contexto:**
>
> Hoy MagnaTravel ya emite NCs fiscales cuando se anulan facturas. El
> sistema actual exige una aprobacion ("approval") para cada NC. Con el
> modulo nuevo, anular una reserva pasa por una aprobacion **al inicio**
> del flujo (cuando el usuario decide cancelar). Esa aprobacion deja
> rastro auditable: quien aprobo, cuando, con que motivo.
>
> El flujo nuevo seria: vendedor pide cancelar reserva → un usuario con
> permiso de aprobacion (Admin o Colaborador segun politica) aprueba la
> cancelacion → el sistema emite automaticamente la NC fiscal a ARCA SIN
> pedir una segunda aprobacion para la NC.
>
> **Por que pedimos saltear la segunda aprobacion:**
>
> La aprobacion ya tomada cubre la decision fiscal: anular una reserva
> con factura emitida obliga a emitir NC, no hay decision "intermedia".
> Pedir aprobacion otra vez es ruido operativo sin valor fiscal extra.
>
> **Lo que necesitamos que firmes:**
>
> Que este atajo es valido fiscalmente bajo Resolucion General AFIP
> [completar referencia que el contador conozca] y bajo la politica
> interna de control que la agencia mantiene para anulaciones.
>
> **Salvaguardas que dejamos puestas:**
>
> - Si NO hubo aprobacion del BC (la primera aprobacion), la NC SI pasa
>   por aprobacion normal (no se saltea nada).
> - Toda NC emitida queda con cross-reference al ApprovalRequest original
>   en el campo `Invoice.AnnulmentApprovalRequestId` (campo nuevo).
> - El campo `AnnulmentReason` de la NC arranca con prefijo `[BC-XXXX]`
>   que indica el ID del BookingCancellation que origino la NC.
> - Audit log fiscal queda en BD con la trazabilidad completa.
> - Hay un "boton de emergencia" (ForceArcaConfirmation, solo Admin)
>   para casos donde el callback automatico de ARCA falla y la NC quedo
>   emitida pero el sistema no lo registro.
>
> **Documento tecnico de referencia:**
>
> ADR-004 en `docs/architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md`.
>
> **Que necesitamos de vos:**
>
> 1. Lectura del ADR-004 (o si preferis, una reunion de 30 min donde te
>    explicamos en vivo).
> 2. Firma (mail o documento PDF) confirmando que la decision es valida
>    fiscalmente, o sugiriendo cambios si no estas de acuerdo.
> 3. Si encuentras un riesgo fiscal especifico, decinos cual asi lo
>    cubrimos antes de prender el flag.
>
> **Plan B si no estas de acuerdo:**
>
> Pasamos a "doble aprobacion" (mantener la NC pidiendo su propia
> aprobacion siempre). Es operativamente mas lento pero fiscalmente mas
> conservador. El sistema soporta ambos modos via configuracion.
>
> Quedamos a la espera. Gracias.
>
> [firma]

---

## Lista de chequeo para Gaston antes de mandar

- [ ] Adjuntar el archivo `docs/architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md` al mail.
- [ ] CC al asesor fiscal (arca-tax-expert).
- [ ] Indicar plazo de respuesta (ej. 7 dias habiles).
- [ ] Mantener trazabilidad: guardar la respuesta firmada en `docs/operations/signoffs/`.

## Cuando llegue la firma

1. Crear archivo `docs/operations/signoffs/2026-XX-XX-ops-fiscal-001-firma.md`
   con copia del mensaje firmado + fecha + nombre + matricula del contador.
2. Actualizar ADR-004 cambiando Status de `ACCEPTED (bloqueado por signoff)`
   a `ACCEPTED (signoff completado 2026-XX-XX)`.
3. Commitear con mensaje `docs(cancellation): signoff OPS-FISCAL-001 completado`.
4. Ahora si: prender `EnableNewCancellationFlow=true` en la migracion de
   prod (siguiendo el procedimiento de `docs/operations/2026-05-18-precondicion-responsibleuserid.md`).

## Si el contador rechaza o pide cambios

1. NO prender el feature flag.
2. Documentar los cambios pedidos en un nuevo ADR (ADR-XXX-fix-fiscal-feedback).
3. Aplicar Plan B (doble aprobacion) o Plan C que negocies con el contador.
4. Volver a pedir signoff con el cambio aplicado.
