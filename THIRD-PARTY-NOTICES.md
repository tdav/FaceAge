# Сторонние файлы

| Файл | Источник | Лицензия | В репозитории |
|---|---|---|---|
| `src/FaceAge/Resources/Raw/yunet_320.onnx` | [opencv/opencv_zoo](https://github.com/opencv/opencv_zoo), `face_detection_yunet_2023mar.onnx`; в описании графа размер входа изменён с 640×640 на 320×320 (веса и узлы не изменены) | MIT | да |
| `src/FaceAge/Resources/Raw/genderage.onnx` | [InsightFace](https://github.com/deepinsight/insightface), пакет `buffalo_l` | только некоммерческое исследовательское использование | нет — `fetch-assets.ps1` |
| `tests/FaceAge.Tests/Photos/*.jpg` | Vecteezy, см. `tests/FaceAge.Tests/Photos/SOURCE.md` | бесплатная лицензия Vecteezy | нет — `fetch-assets.ps1` |
| `src/FaceAge/Resources/Fonts/Inter-*.ttf` | [Inter](https://github.com/rsms/inter) | SIL Open Font License 1.1, текст — `Inter-OFL.txt` | да |

Поправка возраста (`AgeEstimator.Calibration`) подобрана на наборе [UTKFace](https://susanqq.github.io/UTKFace/) (только некоммерческое исследовательское использование). Сам набор в репозиторий не входит, в коде — только 12 узлов поправки.
