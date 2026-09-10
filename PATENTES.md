# IrisTrack AI · modo Patentes (prueba)

1. Abrí el video en tu reproductor y acoplá IrisTrack a esa ventana.
2. En **Objetivo**, elegí **Patentes · prueba**. También está en el menú del borde.
3. La primera vez se descargan aproximadamente 11 MB de modelos. Después el análisis es local.
4. Activá **Capturas automáticas** / F10 para guardar. **Abrir capturas** abre el destino.

| Objetivo | Modelos activos |
|---|---|
| General (personas, vehículos, etc.) | YOLO26n |
| Patentes | YOLOv9 Tiny 384 + CCT-XS v2. YOLO26n se descarga de memoria. |
| Vehículos + patentes | YOLO26n y, dentro de cada vehículo encontrado, YOLOv9 Tiny 384 + CCT-XS v2 |

Los modelos se preparan cuando empieza el análisis. Cambiar de modo espera a que termine
la inferencia anterior antes de descargar o cargar sesiones. Si falla una descarga, F8
permite apagar y volver a intentar. Se conserva el modo elegido y se informa el error.

## Lecturas y rendimiento

- **Amarillo:** patente encontrada, lectura pendiente o dudosa.
- **Celeste:** tres lecturas consecutivas iguales, OCR medio ≥80% y cada carácter ≥50%.
- La coincidencia repetida es un filtro operativo, no una garantía de exactitud.
- El lector recibe únicamente el recorte de la patente. El overlay no ejecuta IA.
- OCR como máximo cada 200 ms por seguimiento; después de estabilizarse, cada segundo.
- El límite de análisis/s y el reposo por movimiento siguen disponibles. Reducir análisis
  puede perder vehículos rápidos: conviene comparar con los videos que usás habitualmente.
- La barra inferior muestra análisis/s y milisegundos de IA; el estado indica CPU o DirectML.
- El filtro de clase del modo general no evita ejecutar YOLO26n. Sólo Patentes usa el
  detector específico sin YOLO26n. El modo combinado requiere más procesamiento.

## Archivos guardados

Se usa el destino habitual, junto al video cuando IrisTrack identifica su ruta, con una
subcarpeta **Patentes**. Cada aparición tiene un JSON y las imágenes elegidas (recorte y/o
fotograma). Si mejora la confianza de la lectura, o aumenta claramente el tamaño de la
patente manteniendo confianza similar, se actualiza ese registro y se reemplazan sus
imágenes. No se agrega una fila de historial por cada lectura.

Los registros contienen texto, confianza del detector y del OCR, hora de captura, ventana,
archivo de origen cuando se conoce y segundos transcurridos de análisis. **La posición real
del video permanece vacía**: capturar una ventana externa no permite deducirla. La hora de
captura es la del equipo; el tiempo de análisis también avanza mientras se pausa IrisTrack.
No se sustituyen letras por números ni se fuerza un formato argentino.

Una pérdida breve del seguimiento puede recuperar la misma aparición si coinciden el texto,
la proximidad y el tiempo. Una reaparición posterior puede generar otro registro. Cambiar
de fuente, objetivo o zonas reinicia el seguimiento. Los cruces de línea mantienen su propio
evento; en modo combinado se cuenta el vehículo para evitar contar también su patente.

## Verificación

En Windows con .NET 8:

```powershell
dotnet run --project tests/IrisTrackAI.Checks/IrisTrackAI.Checks.csproj -c Release -- --ui --models
```

Las comprobaciones incluyen decodificación, cajas, seguimiento sin asignaciones dobles,
consenso, actualización de imágenes, tiempos, carga de la interfaz, descarga y ejecución
de los modelos con la imagen pública del autor y descarga de modelos al cambiar de modo.
La imagen de demostración no reemplaza una evaluación con patentes argentinas, motos,
distintas distancias y video nocturno, ni mide el rendimiento de otra computadora.

## Modelos y fuentes

- [FastALPR](https://github.com/ankandrew/fast-alpr): arquitectura de detección y lectura.
- [Open Image Models](https://github.com/ankandrew/open-image-models): YOLOv9 Tiny 384,
  letterbox RGB con relleno 114, tensor FP32 NCHW y salida NMS de siete columnas.
- [Fast Plate OCR](https://github.com/ankandrew/fast-plate-ocr): CCT-XS v2 global, RGB uint8
  NHWC 128×64, diez posiciones independientes, alfabeto latino. El propio modelo normaliza.

La integración es C# con ONNX Runtime. No requiere instalar Python. El OCR está fijado al
SHA-256 `8031afb5fdc6b4d80462c9d542f1284ebd2cfddf5dbacd62609848d7e2855f44` y ambos modelos se
validan antes de usarlos. DirectML tiene respaldo CPU si el driver falla.
