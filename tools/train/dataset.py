# Чтение папки экспорта лиц и набор данных для обучения: вид телефона, искажения, деление по хешу, баланс возрастов.
import io
import json
import math
import zlib
from collections import Counter
from dataclasses import dataclass
from datetime import date
from pathlib import Path

import numpy as np
import torch
from PIL import Image, ImageEnhance, ImageFilter
from torch.utils.data import Dataset, WeightedRandomSampler

TARGET_MIN = 13
TARGET_MAX = 50
VAL_PERCENT = 15
PAD = 48  # запас по краям кадра: за его границей продолжаются крайние пиксели (как в экспорте)


@dataclass(frozen=True)
class Sample:
    file: str  # как в metadata.jsonl
    path: Path
    age: float  # (shot − birth) в днях / 365.2425
    sex: str
    pred: float


def read_export(data_dir: str) -> tuple[list[Sample], dict]:
    """Читает export.json и metadata.jsonl; оборванная последняя строка отбрасывается, строки без файла пропускаются."""
    root = Path(data_dir)
    export = json.loads((root / "export.json").read_text(encoding="utf-8"))
    lines = (root / "metadata.jsonl").read_text(encoding="utf-8").splitlines()
    samples: list[Sample] = []
    missing = 0
    for i, line in enumerate(lines):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            age = (date.fromisoformat(row["shot"]) - date.fromisoformat(row["birth"])).days / 365.2425
            file = row["file"]
            sex = row["sex"]
            pred = float(row["pred"])
        except (ValueError, KeyError, TypeError):
            if i == len(lines) - 1:
                continue  # оборванная последняя строка
            raise
        path = root / "images" / file
        if not path.is_file():
            missing += 1
            continue
        samples.append(Sample(file, path, age, sex, pred))
    print(f"Прочитано примеров: {len(samples)}; строк без файла пропущено: {missing}")
    return samples, export


def is_val(file: str) -> bool:
    return zlib.crc32(file.encode()) % 100 < VAL_PERCENT


def in_target(age: float) -> bool:
    return TARGET_MIN <= int(age) <= TARGET_MAX


# Искажения. prob — вероятность применения; диапазоны — равномерные. «train» — случайные, «phone» — фиксированные
# (генератор засеян crc32 имени файла, вероятность 1).
TRAIN = dict(rot=12, scale=(0.92, 1.08), shift=0.05, mirror=0.5,
             bright=0.3, contrast=0.3, color=0.2,
             gamma=(0.3, 0.7, 1.4), blur=(0.3, 0.1, 1.0), low=(0.4, 0.5, 1.0), jpeg=(0.3, 30, 90),
             noise=(0.3, 8.0), bar=0.05)
PHONE = dict(rot=8, scale=(1.0, 1.0), shift=0.0, mirror=0.0,
             bright=0.25, contrast=0.0, color=0.0,
             gamma=None, blur=(1.0, 0.0, 0.6), low=(1.0, 0.6, 1.0), jpeg=(1.0, 40, 80),
             noise=None, bar=0.0)


class FaceDataset(Dataset):
    """(samples, export, input_size, mode) -> (data float32 [3,S,S] RGB 0…255, age).

    mode: "train" — случайные искажения; "clean" — без искажений; "phone" — фиксированные искажения.
    """

    def __init__(self, samples: list[Sample], export: dict, input_size: int, mode: str):
        if mode not in ("train", "clean", "phone"):
            raise ValueError(mode)
        self.samples = samples
        self.input_size = input_size
        self.mode = mode
        self.size = export["size"]
        self.view = self.size * export["modelCropScale"] / export["cropScale"]  # сторона вида телефона, px (219,4)
        self.cfg = TRAIN if mode == "train" else PHONE

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, index: int):
        sample = self.samples[index]
        with Image.open(sample.path) as im:
            img = im.convert("RGB")
        w = self.input_size
        if self.mode == "clean":
            rng = None
            angle, scale, dx, dy, mirror = 0.0, 1.0, 0.0, 0.0, False
        else:
            c = self.cfg
            rng = np.random.default_rng(zlib.crc32(sample.file.encode())) if self.mode == "phone" else np.random.default_rng()
            angle = rng.uniform(-c["rot"], c["rot"])
            scale = rng.uniform(*c["scale"])
            dx, dy = rng.uniform(-c["shift"], c["shift"], 2) * self.view
            mirror = rng.random() < c["mirror"]

        # Один проход: прямая билинейная выборка БЕЗ сглаживания из кадра, продолженного крайними пикселями, сразу в S×S
        # (S = input_size), как в AgeEstimator.Analyze. Лишний проход или усреднение размывает лицо, а модель чувствительна
        # к резкости (размытие «омолаживает» на 2–3 года).
        padded = Image.fromarray(np.pad(np.asarray(img), ((PAD, PAD), (PAD, PAD), (0, 0)), mode="edge"))
        s = self.view * scale / w
        cos, sin = math.cos(math.radians(angle)), math.sin(math.radians(angle))
        half = -w / 2  # Pillow сам прибавляет 0,5 к координатам выходного пикселя
        centre = self.size / 2 + PAD
        out = padded.transform((w, w), Image.AFFINE,
                                (s * cos, -s * sin, centre + dx + s * (cos - sin) * half,
                                 s * sin, s * cos, centre + dy + s * (sin + cos) * half),
                                resample=Image.BILINEAR)
        if mirror:
            out = out.transpose(Image.FLIP_LEFT_RIGHT)
        if rng is not None:
            out = self.distort(out, rng)

        data = torch.from_numpy(np.asarray(out, dtype=np.float32).copy()).permute(2, 0, 1).contiguous()
        return data, torch.tensor(sample.age, dtype=torch.float32)

    def distort(self, img: Image.Image, rng: np.random.Generator) -> Image.Image:
        c = self.cfg
        w = img.width
        if rng.random() < c["low"][0]:
            small = max(8, round(rng.uniform(c["low"][1], c["low"][2]) * w))  # доля входа
            img = img.resize((small, small), Image.BILINEAR).resize((w, w), Image.BILINEAR)
        if rng.random() < c["blur"][0]:
            # сила размытия задана для входа 96; для другого входа масштабируется, чтобы относительно лица была та же
            img = img.filter(ImageFilter.GaussianBlur(rng.uniform(c["blur"][1], c["blur"][2]) * w / 96))
        if c["bright"]:
            img = ImageEnhance.Brightness(img).enhance(1 + rng.uniform(-c["bright"], c["bright"]))
        if c["contrast"]:
            img = ImageEnhance.Contrast(img).enhance(1 + rng.uniform(-c["contrast"], c["contrast"]))
        if c["color"]:
            img = ImageEnhance.Color(img).enhance(1 + rng.uniform(-c["color"], c["color"]))
        if c["gamma"] and rng.random() < c["gamma"][0]:
            g = rng.uniform(c["gamma"][1], c["gamma"][2])
            img = Image.fromarray((255 * (np.asarray(img) / 255.0) ** g).round().astype(np.uint8))
        if c["noise"] and rng.random() < c["noise"][0]:
            arr = np.asarray(img, dtype=np.float32) + rng.normal(0, rng.uniform(0, c["noise"][1]), (w, w, 3))
            img = Image.fromarray(arr.clip(0, 255).round().astype(np.uint8))
        if rng.random() < c["jpeg"][0]:
            buf = io.BytesIO()
            img.save(buf, "JPEG", quality=int(rng.integers(c["jpeg"][1], c["jpeg"][2] + 1)))
            buf.seek(0)
            img = Image.open(buf).convert("RGB")
        if rng.random() < c["bar"]:
            arr = np.array(img)
            n = max(1, round(w * rng.uniform(0.03, 0.12)))
            side = int(rng.integers(4))
            if side == 0:
                arr[:n] = 0
            elif side == 1:
                arr[-n:] = 0
            elif side == 2:
                arr[:, :n] = 0
            else:
                arr[:, -n:] = 0
            img = Image.fromarray(arr)
        return img


def balanced_sampler(samples: list[Sample]) -> WeightedRandomSampler:
    """Вес примера = 1 / число примеров с тем же int(age) и sex."""
    keys = [(int(s.age), s.sex) for s in samples]
    counts = Counter(keys)
    weights = [1.0 / counts[k] for k in keys]
    return WeightedRandomSampler(weights, num_samples=len(samples), replacement=True)
