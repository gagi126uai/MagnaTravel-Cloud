/**
 * Pegamento entre `useServiceLineInterpretation` (la llamada de red) y CADA *InlineForm
 * (Hotel/Aéreo/Traslado/Paquete/Asistencia). Un solo hook para los 5 tipos: la parte que
 * cambia de un tipo a otro (nombres de campo) vive en `SERVICE_LINE_FIELD_MAPS`
 * (serviceLineInterpretationLogic.js), así que este hook no necesita saber nada
 * específico de Hotel/Aéreo/etc.
 *
 * Qué hace, en orden, cada vez que llega una interpretación NUEVA y utilizable:
 *   1. Si el motor reconoció el producto directo (Momento 3, §3.3) y todavía no hay
 *      ninguno elegido → marca `productoResueltoPorLineaInteligente` y precarga nombre,
 *      ciudad y rateId. La CAJA de arriba (lo que escribió el vendedor) NO se toca — el
 *      *InlineForm la muestra tal cual y arma el renglón "Producto *" aparte con este dato
 *      (mockup firmado §3.3: la frase se conserva arriba, el producto resuelto va abajo).
 *   2. Arma el resto (operador, variante, precio, fechas) respetando V10=A: nunca pisa
 *      un campo que el vendedor ya tocó con la mano (ver `marcarTocado`).
 *   3. Si el producto NO se reconoció pero hay parecidos (Momento 4, §3.4), arma el
 *      override para el desplegable del buscador (`aiOverride`).
 *   4. Si hay una duda grande (§4) sobre un campo TODAVÍA sin tocar, la deja lista para
 *      mostrar (`duda`) — un campo que el vendedor ya tocó NUNCA la ofrece (V10=A).
 *
 * Lo que este hook NO hace: no sabe nada de JSX. Cada *InlineForm decide DÓNDE poner el
 * amarillo y DÓNDE mostrar la línea de la duda — acá solo se decide QUÉ va en cada lado.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useServiceLineInterpretation } from "./useServiceLineInterpretation";
import {
  SERVICE_LINE_FIELD_MAPS,
  construirPatchDeProducto,
  construirPatchDeResto,
  construirOverrideBuscador,
  puedeMostrarDuda,
  resolverRespuestaDuda,
} from "./serviceLineInterpretationLogic";

/**
 * @param {{
 *   reservaId: string,
 *   serviceType: "Hotel"|"Aereo"|"Traslado"|"Paquete"|"Asistencia",
 *   isEditing: boolean,
 *   canSeeCost: boolean,
 *   form: object,
 *   setForm: Function,
 *   setCamposSugeridos: Function,
 *   precioTocadoPorElUsuario: boolean,
 *   monedaTocadaPorElUsuario: boolean,
 *   idsDeCampoParaEnfocar: Record<string,string>,
 * }} params
 */
export function useServiceLineInterpretationForForm({
  reservaId,
  serviceType,
  isEditing,
  canSeeCost,
  form,
  setForm,
  setCamposSugeridos,
  precioTocadoPorElUsuario,
  monedaTocadaPorElUsuario,
  idsDeCampoParaEnfocar,
  // Opcional: Hotel/Aéreo/Traslado tienen su PROPIO flag "tocado" para el precio/moneda
  // (lo usa la sugerencia POR VARIANTE, una feature distinta y ya probada). Si el
  // vendedor contesta "No" a la duda de precio, ese campo se vacía acá — pero sin avisar
  // a la OTRA feature, su próxima respuesta (300ms después) podría volver a completarlo,
  // deshaciendo el "No" sin que el vendedor lo haya tocado a mano. Este callback es el
  // puente: se llama con el nombre de CADA campo que la duda vació.
  alVaciarCampoPorDuda,
  // Opcional (mismo motivo que el de arriba, bug bloqueante B2 del revisor funcional): el
  // costo que sale de la FRASE ("48 usd") es un dato que el vendedor dijo, tanto como si
  // lo hubiera tecleado — si no se avisa, la sugerencia POR VARIANTE (que corre en un
  // hook aparte, con sus PROPIOS flags) lo pisa 300ms después al resolver su propia
  // consulta. Se llama con "costo" o "moneda" cuando la interpretación aplicó alguno.
  alPrecargarPrecioDeLaFrase,
}) {
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  const searchText = mapa ? form[mapa.nameField] : "";
  // Fix menor (revisor funcional): con el producto YA resuelto (rateId o newCatalogProduct
  // presentes), no tiene sentido seguir interpretando la misma frase — el motor tiene un
  // tope de 40 pedidos/minuto y estaríamos re-interpretando la propia salida del sistema.
  // Escribir de nuevo en la caja limpia rateId/newCatalogProduct (ver cada *InlineForm,
  // handleSearchChange) y esto se destraba solo.
  const productoYaResueltoAhora = Boolean(form.rateId || form.newCatalogProduct);

  // La interpretación solo tiene sentido cargando un servicio NUEVO, con texto propio sin
  // resolver todavía: en edición el buscador ya está deshabilitado (no se puede cambiar
  // de producto), así que no hay ninguna frase nueva que interpretar.
  const { interpretation, isThinking } = useServiceLineInterpretation({
    reservaId,
    serviceType,
    text: searchText,
    enabled: !isEditing && !productoYaResueltoAhora,
  });

  // Campos que el vendedor tocó con la mano y que la interpretación NUNCA puede pisar
  // (V10=A). Separado de `precioTocadoPorElUsuario`/`monedaTocadaPorElUsuario` (que ya
  // existen para la sugerencia de precio POR VARIANTE, una feature distinta) para no
  // arriesgar esa lógica ya probada — acá se combinan ambos al armar el patch.
  const [camposTocadosPorElVendedor, setCamposTocadosPorElVendedor] = useState(() => new Set());
  const marcarTocado = useCallback((campo) => {
    setCamposTocadosPorElVendedor((prev) => {
      if (prev.has(campo)) return prev;
      const next = new Set(prev);
      next.add(campo);
      return next;
    });
  }, []);

  // Set COMBINADO (este feature + la sugerencia por variante) — es lo que se usa para
  // decidir la duda Y para el merge de Momento 4 (construirPatchDeSeleccionManual, que
  // corre en cada *InlineForm). Recalculado en cada render, es liviano (unas pocas claves).
  const camposTocadosCombinados = useMemo(() => {
    const combinado = new Set(camposTocadosPorElVendedor);
    if (precioTocadoPorElUsuario && mapa?.costField) combinado.add(mapa.costField);
    if (monedaTocadaPorElUsuario && mapa?.currencyField) combinado.add(mapa.currencyField);
    return combinado;
  }, [camposTocadosPorElVendedor, precioTocadoPorElUsuario, monedaTocadaPorElUsuario, mapa]);

  const [duda, setDuda] = useState(null);
  const [aiOverride, setAiOverride] = useState(null);
  // Momento 3 (§3.3): true cuando el motor reconoció el producto directo. Gobierna el
  // renglón "Producto *" que cada *InlineForm pinta DEBAJO de la caja (que sigue
  // mostrando la frase tal cual la escribió el vendedor — mockup firmado, no se toca).
  const [productoResueltoPorLineaInteligente, setProductoResueltoPorLineaInteligente] = useState(false);

  // `form` cambia en cada tecla — lo leemos por ref para no relanzar este efecto por eso.
  // Lo único que tiene que disparar la aplicación de una interpretación es que LLEGUE una
  // interpretación nueva, no que el form haya cambiado por cualquier otro motivo.
  const formRef = useRef(form);
  formRef.current = form;

  useEffect(() => {
    if (!interpretation || !mapa) {
      setAiOverride(null);
      return;
    }
    const formActual = formRef.current;
    const productoYaResuelto = Boolean(formActual.rateId || formActual.newCatalogProduct);

    // V10=A combinado: además de lo que el vendedor tocó en ESTE feature, respetamos el
    // precio/moneda ya protegidos por la sugerencia-por-variante (misma regla, mismo
    // campo real, dos features que conviven).
    const tocados = new Set(camposTocadosPorElVendedor);
    if (precioTocadoPorElUsuario && mapa.costField) tocados.add(mapa.costField);
    if (monedaTocadaPorElUsuario && mapa.currencyField) tocados.add(mapa.currencyField);

    const patchProducto = construirPatchDeProducto({ dto: interpretation, serviceType, productoYaResuelto });
    const { patch: patchResto, camposSugeridos: camposSugeridosResto } = construirPatchDeResto({
      dto: interpretation, serviceType, canSeeCost, camposTocados: tocados,
    });

    const patchCombinado = { ...(patchProducto || {}), ...patchResto };
    if (Object.keys(patchCombinado).length > 0) {
      setForm((prev) => ({ ...prev, ...patchCombinado }));
    }
    if (camposSugeridosResto.length > 0) {
      setCamposSugeridos((prev) => {
        const next = { ...prev };
        for (const campo of camposSugeridosResto) next[campo] = true;
        return next;
      });
    }
    // Nota: el renglón "Producto *" (ResolvedProductRow) ya NO necesita una entrada en
    // `camposSugeridos` — es de solo lectura y siempre amarillo mientras está visible
    // (fix bloqueante, segunda vuelta: dejarlo editable era una fuga de identidad
    // fantasma, ver el comentario grande en ResolvedProductRow.jsx). Su visibilidad la
    // gobierna únicamente `productoResueltoPorLineaInteligente`, de abajo.
    if (patchProducto) {
      setProductoResueltoPorLineaInteligente(true);
    }

    // Bug bloqueante B2 (revisor funcional): un costo que salió de LA FRASE tiene que
    // "contar como tocado" para la sugerencia POR VARIANTE (hook aparte, con sus propios
    // flags) — si no, 300ms después esa sugerencia puede pisar (o vaciar) el número que
    // el vendedor efectivamente dijo al escribir la frase.
    if (camposSugeridosResto.includes(mapa.costField)) alPrecargarPrecioDeLaFrase?.("costo");
    if (camposSugeridosResto.includes(mapa.currencyField)) alPrecargarPrecioDeLaFrase?.("moneda");

    setAiOverride(construirOverrideBuscador({ dto: interpretation, productoYaResuelto }));
    // Bug bloqueante B1 (revisor funcional): la duda NUNCA se ofrece sobre un campo que
    // el vendedor ya tocó — `tocados` es el MISMO set que protegió el patch de arriba.
    setDuda(
      puedeMostrarDuda({ doubt: interpretation.doubt, serviceType, canSeeCost, camposTocados: tocados })
        ? interpretation.doubt
        : null
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [interpretation]);

  // "Sí" cierra la duda sin tocar nada. "No" vacía el/los campo(s) que señala y les deja
  // el foco — y los marca como tocados para que una interpretación posterior (si el
  // vendedor sigue escribiendo) tampoco los vuelva a pisar.
  const onRespuestaDuda = useCallback((respuestaEsSi) => {
    if (!duda) return;
    if (!respuestaEsSi) {
      // Bug bloqueante B1, defensa en profundidad: se vuelve a filtrar contra los
      // tocados VIGENTES (pudieron cambiar entre que se armó la duda y que se contestó).
      const { camposAVaciar, campoParaEnfocar } = resolverRespuestaDuda({
        doubt: duda, respuestaEsSi: false, serviceType, camposTocados: camposTocadosCombinados,
      });
      if (camposAVaciar.length > 0) {
        setForm((prev) => {
          const next = { ...prev };
          for (const campo of camposAVaciar) next[campo] = "";
          return next;
        });
        // Fix menor (revisor funcional): un campo recién vaciado por "No" deja de ser
        // sugerencia — si quedara marcado `true`, el casillero se vería amarillo y VACÍO
        // a la vez, un estado que no significa nada para el vendedor.
        setCamposSugeridos((prev) => {
          const next = { ...prev };
          for (const campo of camposAVaciar) next[campo] = false;
          return next;
        });
        for (const campo of camposAVaciar) {
          marcarTocado(campo);
          alVaciarCampoPorDuda?.(campo);
        }
      }
      if (campoParaEnfocar) {
        const idCampo = idsDeCampoParaEnfocar?.[campoParaEnfocar];
        // El campo recién se vació en este mismo tick: esperamos al próximo frame para
        // que el input ya esté en el DOM con el valor nuevo antes de enfocarlo.
        requestAnimationFrame(() => {
          if (idCampo) document.getElementById(idCampo)?.focus();
        });
      }
    }
    setDuda(null);
  }, [duda, serviceType, setForm, setCamposSugeridos, marcarTocado, idsDeCampoParaEnfocar, alVaciarCampoPorDuda, camposTocadosCombinados]);

  // Al resolver el producto a mano (elegir del buscador, crear nuevo, o borrar el texto)
  // todo lo que la línea inteligente venía ofreciendo sobre LA IDENTIDAD del producto deja
  // de tener sentido — cada *InlineForm llama a esto desde sus propios
  // handleSelectExisting/handleCreateNew/handleSearchChange.
  const limpiarResolucionIA = useCallback(() => {
    setAiOverride(null);
    setProductoResueltoPorLineaInteligente(false);
  }, []);

  return {
    isThinking,
    duda,
    onRespuestaDuda,
    aiOverride,
    productoResueltoPorLineaInteligente,
    limpiarResolucionIA,
    marcarTocado,
    // Expuesto para que cada *InlineForm lo use en su propio handleSelectExisting
    // (construirPatchDeSeleccionManual, bug bloqueante B3: Momento 4 no puede pisar lo
    // que ya está sugerido por la línea inteligente ni lo tocado).
    camposTocados: camposTocadosCombinados,
  };
}
