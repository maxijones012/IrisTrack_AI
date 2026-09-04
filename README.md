# IrisTrack AI

[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)
![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET-8-512BD4)

**IrisTrack AI** es un asistente inteligente de análisis visual de video para Windows. Se acopla a una ventana de reproducción existente —por ejemplo VLC— y utiliza detección de objetos para resaltar elementos de interés en tiempo real, sin reemplazar el reproductor.

La idea es simple: seguís mirando el video con tu reproductor habitual y IrisTrack AI trabaja como una capa de asistencia visual, local y no invasiva.

## Funciones principales

- Selección y acople a una ventana activa.
- Overlay transparente sobre el video.
- Detección de personas, bicicletas, motos, autos, colectivos, camiones y otras clases compatibles con el modelo.
- Filtro por objetivo: todos, persona, bicicleta, moto, vehículos, etc.
- Atajo `F8` para activar/desactivar la detección.
- Captura manual y capturas automáticas por detección.
- Guardado de capturas asociado al video analizado cuando la ruta puede resolverse automáticamente.
- Modo de cruce de línea, con dirección de cruce.
- Zonas ignoradas para excluir sectores que generan ruido visual, por ejemplo vehículos estacionados.
- Zona de interés para limitar las detecciones a un sector concreto de la imagen.
- Menú lateral invisible por proximidad para operar sin abrir la interfaz principal.
- Filtros orientados a reducir procesamiento innecesario y priorizar rendimiento.
- Procesamiento local: el video no necesita subirse a un servidor para ser analizado.

## Ejemplos de uso

IrisTrack AI puede utilizarse para tareas como:

- buscar únicamente bicicletas dentro de una filmación;
- detectar el paso de una moto, vehículo o persona;
- registrar automáticamente cuando un objetivo cruza una línea definida;
- ignorar sectores con objetos estacionarios que no forman parte del análisis;
- guardar capturas automáticas de los objetivos encontrados;
- asistir una revisión de CCTV sin cambiar de reproductor.

## Privacidad

El análisis se realiza localmente en el equipo. IrisTrack AI está diseñado para trabajar sobre la imagen de una ventana seleccionada y no requiere enviar los videos a un servidor remoto para la detección.

## Tecnología

El proyecto está desarrollado para Windows con **C# / WPF / .NET 8** y utiliza inferencia ONNX para el análisis de objetos.

IrisTrack AI utiliza componentes/modelos de **Ultralytics YOLO** para detección de objetos.

Ultralytics ofrece distintos esquemas de licenciamiento, incluyendo AGPL-3.0 y licencias comerciales/Enterprise. Este proyecto se publica como software libre bajo **GNU AGPL v3.0**. Si se modifica el modelo de distribución, se integra el proyecto en software propietario cerrado o se plantea un esquema comercial incompatible con AGPL, corresponde revisar previamente los términos vigentes de Ultralytics.

## Importante: herramienta de asistencia

IrisTrack AI es una **herramienta de asistencia visual** y no una fuente de verdad automática.

Los modelos de detección pueden producir:

- falsos positivos;
- falsos negativos;
- clasificaciones incorrectas;
- omisiones por calidad de imagen, distancia, iluminación, compresión, velocidad del video u oclusiones.

Los resultados deben ser revisados por una persona. Una detección de IrisTrack AI no debe considerarse, por sí sola, una identificación, pericia o conclusión definitiva.

## Requisitos

- Windows 10/11 de 64 bits.
- .NET 8 para compilar desde código fuente.
- CPU compatible; cuando el entorno lo permite, el proyecto puede aprovechar aceleración disponible mediante ONNX Runtime.

## Compilación

El repositorio incluirá el código fuente y scripts de compilación de las versiones públicas de IrisTrack AI.

En las versiones actuales del proyecto se utiliza un flujo de publicación `win-x64` para generar `IrisTrackAI.exe`.

## Licencia

Copyright (C) 2026 SoftwareParaTodos / colaboradores de IrisTrack AI.

IrisTrack AI se distribuye bajo la **GNU Affero General Public License v3.0 (AGPL-3.0)**. Consultá el archivo [LICENSE](LICENSE) para ver el texto completo.

Podés usar, estudiar, modificar y redistribuir el software de acuerdo con los términos de esa licencia. Las versiones derivadas y distribuidas deben respetar las obligaciones de AGPL-3.0.

## Donaciones

**IrisTrack AI es gratuito y de código abierto.**

Si la herramienta te resulta útil y querés colaborar con su desarrollo, podés realizar una donación voluntaria mediante los medios que publique el proyecto.

Las donaciones:

- no son obligatorias;
- no desbloquean funciones;
- no convierten la aplicación en una versión Premium;
- ayudan a sostener desarrollo, pruebas y mejoras futuras.

## Estado del proyecto

IrisTrack AI está en desarrollo activo. La interfaz, los modelos, las opciones de rendimiento y las funciones pueden cambiar entre versiones.

---

**IrisTrack AI — Detectá. Seguí. Analizá.**
