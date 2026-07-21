# 2026-07-20 — Contrato pantalla-motor: las 9 tandas completas en un día

**Para quién es este doc**: para cualquiera que se sume al proyecto y quiera
entender qué se hizo hoy y por qué, sin haber estado.

## La idea en una frase

Antes, muchas pantallas ofrecían botones que el motor después rechazaba con
carteles que no decían qué hacer (o directamente se comían el motivo). Hoy se
terminó el plan completo de 9 tandas para que **la pantalla y el motor digan
lo mismo**: los botones que no se pueden usar aparecen apagados con el motivo
a la vista, y cuando algo igual se rechaza, el cartel dice la causa real y el
camino a seguir.

## Qué salió hoy (commits `b20cc9bd..60b6ee46`, pusheados a main)

| Tanda | Qué cambia para el usuario |
|---|---|
| 1 (ya venía del 19/07) | "Pagar al proveedor" muestra el mensaje real del motor (antes: genérico). |
| 2 | Anular el comprobante de un cobro desde la ficha abre el modal de pedir autorización ahí mismo (antes: cartel sin salida). |
| 3 | "Anular reserva" dice el motivo real por código (multi-operador, factura viva → camino de NC, etc.) y el freno de plata trae el botón "Emitir factura". |
| 4 | "Anular varios servicios" muestra la causa por fila + botón "Ver facturas"; todos los mensajes del motor dicen "anular" (no "cancelar"); la pantalla dejó de frenar de más reservas facturadas sin voucher. |
| 5 | "Emitir factura" se apaga por estado con motivo en criollo; desapareció el único enum en inglés que podía llegar a pantalla. |
| 6 | "Editar"/"Eliminar" de cada cobro se apagan mirando EL PAGO (recibo emitido, factura con CAE), con el motivo abajo, antes de llenar nada. |
| 7 | La papelera de anular un servicio avisa ANTES (voucher / plata al operador sin factura / sin cliente) con el motivo al lado; si igual explota, el cartel dice el motivo real y trae el botón correcto. |
| 8 | "Deshacer multa" con impuestos dice a dónde ir: botón "Ir a Cobranzas y Facturación". |
| 9 | Los fallos de "Ver PDF"/"Enviar" de la devolución muestran el detalle real y el siguiente paso en el cartel del panel (chau toast genérico). |

## Cómo se trabajó (y qué atraparon los controles)

Cada tanda pasó por: spec UX desde la guía firmada → implementación →
revisores (backend, frontend, fuga de datos; seguridad/plata donde tocaba) →
commit. Tres bloqueantes REALES aparecieron y se arreglaron antes de subir:

1. **Tanda 6**: la ficha carga los cobros por un endpoint distinto al que
   habíamos equipado con los candados — sin el fix, todos los cobros quedaban
   bloqueados sin motivo. Lo cazó el revisor frontend siguiendo el dato de
   punta a punta.
2. **Tanda 7 (B1)**: el pre-chequeo repartía la plata pagada al operador entre
   los servicios hermanos, y podía dejar la papelera prendida para un servicio
   que el motor iba a rechazar. Se corrigió para que cada servicio se evalúe
   igual que lo evalúa el motor (aislado, contra el pool completo), con un
   test de integración contra Postgres real del escenario exacto.
3. **Tanda 7 (E2E)**: la ficha carga los servicios por 5 endpoints por tipo
   que no traían el candado nuevo — la papelera se veía activa aunque el motor
   decía "bloqueado". **Lo cazó el paseo E2E con la app corriendo de verdad**,
   no las suites (2490 tests front + 3925 unit estaban verdes igual). Es la
   enésima confirmación de la regla: sin correr la app real, no hay "listo".

## Qué está VERIFICADO DE VERDAD y qué no

**Verificado E2E real (17/17, capturas en `scripts/e2e-local/shots-t2a9/`)**:
candado R1 en la papelera con motivo y camino a la vista · candados del cobro
en sus 3 estados (recibo emitido / anulado por auditoría / regresión de anular
comprobante) · facturar apagado en Presupuesto sin jerga · vocabulario
"anular" · 409 de anular reserva con código y mensaje criollo.

**NO verificado E2E** (cubierto por tests unit/integración): el modal de
autorización con un usuario sin permiso (T2), deshacer multa con tributos
(T8), fallo real de PDF (T9). La prueba a mano de Gaston sigue pendiente.

## Números al cierre

Unit backend 3925/3925 · integración de cancelaciones 253/253 **contra
Postgres real local** (Docker quedó configurado; los agentes ya pueden correr
Testcontainers) · front 2490/2490 · builds sin errores · E2E 17/17.

## Seguimientos anotados (no bloquean, quedan en la memoria del retomo)

Atajo "Pedí autorización" en el cartel de edición (necesita código estable en
el candado de reserva) · INV-UNDO-MULTIOP con texto viejo · paridad fina del
lote (botones por motivo) · catch ancho de PaymentsController (cobros) ·
tildes/"(CAE)" en dos textos heredados · etiquetas faltantes de tipos de
autorización.
