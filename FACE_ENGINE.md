# IrisTrack AI — Motor de rostros

El modo **Rostros / identificación** está pensado para mantener el rendimiento de IrisTrack sin ejecutar dos detectores pesados sobre el mismo fotograma.

## Arquitectura

- Objetos, vehículos, motos, bicicletas y personas: **YOLO26n**.
- Rostros / identificación: **SCRFD 500M**.
- Comparación facial opcional: **ArcFace MobileNet**.
- En modo Rostros, IrisTrack **no ejecuta inferencia YOLO** sobre el fotograma.
- ArcFace permanece apagado si no existen imágenes en la carpeta de referencias.
- Cuando hay referencias, ArcFace se ejecuta sólo sobre un rostro nuevo suficientemente grande; no se vuelve a reconocer el mismo track en cada frame.

Los modelos se ejecutan directamente mediante ONNX Runtime desde .NET 8. No se inicia Python ni otro proceso auxiliar.

## Modelos

Los pesos se descargan bajo demanda y se verifican mediante SHA-256 antes de utilizarse. No están incluidos en este repositorio.

### SCRFD 500M KPS

- Proyecto de referencia: UniFace / InsightFace
- Archivo: `scrfd_500m_kps.onnx`
- Fuente: `https://github.com/yakhyo/uniface/releases/download/weights/scrfd_500m_kps.onnx`
- SHA-256: `5e4447f50245bbd7966bd6c0fa52938c61474a04ec7def48753668a9d8b4ea3a`

### ArcFace MobileNet

- Proyecto de referencia: UniFace / InsightFace
- Archivo: `w600k_mbf.onnx`
- Fuente: `https://github.com/yakhyo/uniface/releases/download/weights/w600k_mbf.onnx`
- SHA-256: `9cc6e4a75f0e2bf0b1aed94578f144d15175f357bdc05e815e5c4a02b319eb4f`

Revisar siempre las licencias y condiciones de los modelos originales además de la licencia del código de IrisTrack AI.

## Uso de referencias

1. Seleccionar **Rostros / identificación**.
2. Presionar **CARPETA ROSTROS**.
3. Copiar una imagen clara por persona. El nombre del archivo se utiliza como etiqueta, por ejemplo `Juan_Perez.jpg`.
4. Presionar **RECARGAR**.

La galería se guarda localmente en `%LocalAppData%\IrisTrackAI\RostrosConocidos`.

## Interpretación

Una similitud facial es una señal técnica de apoyo. IrisTrack muestra las coincidencias como **POSIBLE** y no como una identificación concluyente. La calidad del video, resolución, iluminación, ángulo, oclusiones y calidad de la imagen de referencia pueden modificar el resultado.

## Rendimiento

La prioridad de este diseño es evitar trabajo innecesario:

- YOLO y SCRFD son rutas excluyentes de inferencia.
- Los modelos pueden permanecer cargados en memoria para cambiar de modo rápidamente, pero sólo el motor seleccionado procesa cuadros.
- ArcFace no participa si no hay referencias.
- Con referencias, el embedding facial se calcula una vez por track cuando el rostro alcanza un tamaño útil.
