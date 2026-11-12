# Обучение модели возраста по папке экспорта лиц: python train.py --data <папка> --model small|large --out <папка>
import argparse
import json
import math
import sys
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
import torch
from torch.utils.data import DataLoader

from dataset import FaceDataset, balanced_sampler, in_target, is_val, read_export
from models import LARGE_INPUT, SMALL_INPUT, build_large, build_small, export_onnx, loss_fn, predict_age

# Пороги сверки с полем pred (смещение, средняя абсолютная разница). Пробный прогон на UTKFace: правильный препроцессинг —
# -0.65 и 0.91 (разброс нормы между прогонами: смещение -0.60…-0.75); порядок каналов B,G,R — +7.31 и 14.10;
# кадр целиком вместо вида телефона — +1.74 и 4.26. Порог смещения 1.5 оставляет запас над нормой; вторую поломку ловит разница.
PRED_BIAS_LIMIT = 1.5
PRED_MAD_LIMIT = 2.0

# Копия AgeEstimator.Calibration (src/FaceAge/Vision/AgeEstimator.cs) на 2026-11-12.
CALIBRATION = [(8.1, 2.0), (24.0, 22.0), (26.7, 25.0), (29.0, 26.0), (31.3, 26.0), (34.0, 28.0),
               (37.3, 30.0), (41.3, 34.0), (47.2, 39.0), (55.3, 47.0), (65.3, 55.0), (75.4, 66.0)]

DEFAULT_LR = {"small": 0.0003, "large": 0.001}
DEFAULT_FREEZE_BN = {"small": True, "large": False}

WARN_TEXT = ("Сверка с оценками нынешней модели превышена. Это может означать ошибку подготовки кадров, а может — "
             "особенность ваших фото. Обучение продолжается. Пришлите эту строку и файл export.json.")

MODES = ("clean", "phone")
NAMES = {"original": "исходная", "calibrated": "исходная с поправкой", "trained": "обученная"}


def calibrate(age: float) -> float:
    """Правило из AgeEstimator.Analyze: между узлами линейно, за краями — сдвиг крайнего узла."""
    k = 1
    while k < len(CALIBRATION) - 1 and age > CALIBRATION[k][0]:
        k += 1
    if age < CALIBRATION[0][0]:
        return age + CALIBRATION[0][1] - CALIBRATION[0][0]
    if age > CALIBRATION[-1][0]:
        return age + CALIBRATION[-1][1] - CALIBRATION[-1][0]
    (r0, a0), (r1, a1) = CALIBRATION[k - 1], CALIBRATION[k]
    return a0 + (age - r0) * (a1 - a0) / (r1 - r0)


def make_loader(samples, export, size, mode, args, shuffle=False, sampler=None, device="cpu"):
    ds = FaceDataset(samples, export, size, mode)
    return DataLoader(ds, batch_size=args.batch, shuffle=shuffle and sampler is None, sampler=sampler,
                      num_workers=args.workers, persistent_workers=args.workers > 0,
                      pin_memory=device == "cuda", drop_last=sampler is not None and len(samples) > args.batch)


def evaluate(model, kind, loader, device) -> np.ndarray:
    """Возраст по паре «кадр + зеркало» (среднее), в порядке примеров загрузчика."""
    model.eval()
    out = []
    with torch.no_grad():
        for data, _ in loader:
            data = data.to(device)
            out.append(((predict_age(model, kind, data) + predict_age(model, kind, data.flip(-1))) / 2).float().cpu())
    return torch.cat(out).numpy()


def evaluate_onnx(sess, loader) -> np.ndarray:
    """То же для исходной genderage.onnx: возраст = fc1[2] × 100."""
    name = sess.get_inputs()[0].name
    out = []
    for data, _ in loader:
        x = data.numpy()
        out.append((sess.run(None, {name: x})[0][:, 2] + sess.run(None, {name: x[..., ::-1].copy()})[0][:, 2]) * 50)
    return np.concatenate(out)


def stats(pred, age, sex) -> dict:
    """Метрики по примерам; все возрасты берутся как есть (отбор по in_target делает вызывающий код)."""
    err = pred - age
    years = np.array([int(a) for a in age])
    sex = np.array(sex)
    return {
        "n": int(len(age)),
        "mae": float(np.abs(err).mean()),
        "bias": float(err.mean()),
        "within": {str(t): float((np.abs(err) <= t).mean()) for t in (1, 2, 3, 5)},
        "by_year": {str(y): {"n": int((years == y).sum()), "mae": float(np.abs(err[years == y]).mean()),
                             "bias": float(err[years == y].mean())} for y in sorted(set(years))},
        "by_sex": {s: {"n": int((sex == s).sum()), "mae": float(np.abs(err[sex == s]).mean())} for s in sorted(set(sex))},
    }


def main() -> None:
    here = Path(__file__).resolve().parent
    p = argparse.ArgumentParser()
    p.add_argument("--data", required=True)
    p.add_argument("--model", required=True, choices=["small", "large"])
    p.add_argument("--out", required=True)
    p.add_argument("--genderage", default=str(here / "../../src/FaceAge/Resources/Raw/genderage.onnx"))
    p.add_argument("--epochs", type=int, default=30)
    p.add_argument("--batch", type=int, default=128)
    p.add_argument("--lr", type=float, default=None, help="по умолчанию: small 0.0003, large 0.001")
    p.add_argument("--freeze-bn", action=argparse.BooleanOptionalAction, default=None,
                   help="BatchNorm в режиме eval при обучении (скользящие средние не меняются); по умолчанию: small да, large нет")
    p.add_argument("--workers", type=int, default=4)
    p.add_argument("--limit", type=int, default=0)
    p.add_argument("--no-pretrained", action="store_true")
    p.add_argument("--resume", action="store_true")
    args = p.parse_args()
    if args.lr is None:
        args.lr = DEFAULT_LR[args.model]
    if args.freeze_bn is None:
        args.freeze_bn = DEFAULT_FREEZE_BN[args.model]

    samples, export = read_export(args.data)
    if args.limit:
        pick = np.random.default_rng(0).choice(len(samples), min(args.limit, len(samples)), replace=False)
        samples = [samples[i] for i in sorted(pick)]
    train = [s for s in samples if not is_val(s.file)]
    val = [s for s in samples if is_val(s.file)]
    val_t = [s for s in val if in_target(s.age)]
    if not val_t:
        print("В отложенной части нет фото 13–50 лет: считать точность не на чем. Нужен экспорт с этими возрастами.")
        sys.exit(2)
    print(f"Обучение: {len(train)}, отложенных: {len(val)}, из них 13–50 лет: {len(val_t)}")

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    kind = args.model
    size = SMALL_INPUT if kind == "small" else LARGE_INPUT
    torch.manual_seed(0)
    model = (build_small(args.genderage) if kind == "small" else build_large(not args.no_pretrained)).to(device)

    opt = torch.optim.AdamW(model.parameters(), lr=args.lr)
    train_loader = make_loader(train, export, size, "train", args, sampler=balanced_sampler(train), device=device)
    steps = args.epochs * len(train_loader)
    warm = len(train_loader)  # разогрев — одна эпоха
    sched = torch.optim.lr_scheduler.LambdaLR(
        opt, lambda i: (i + 1) / warm if i < warm else 0.5 * (1 + math.cos(math.pi * (i - warm) / max(1, steps - warm))))
    scaler = torch.amp.GradScaler(enabled=device == "cuda")
    val_loaders = {m: make_loader(val_t, export, size, m, args, device=device) for m in MODES}

    history, best, start = [], float("inf"), 0
    if args.resume:
        ck = torch.load(out / "last.pt", map_location=device)
        model.load_state_dict(ck["model"])
        opt.load_state_dict(ck["opt"])
        sched.load_state_dict(ck["sched"])
        history, best, start = ck["history"], ck["best"], ck["epoch"]
        print(f"Продолжение с эпохи {start + 1}")

    # Базовые линии: исходная genderage.onnx на отложенных примерах, вход 96, усреднение «кадр + зеркало».
    opts = ort.SessionOptions()
    opts.log_severity_level = 3  # в модели зашит размер пакета 1: без этого на каждый вызов печатается предупреждение
    sess = ort.InferenceSession(args.genderage, opts, providers=["CPUExecutionProvider"])
    base = {m: evaluate_onnx(sess, make_loader(val, export, SMALL_INPUT, m, args, device="cpu")) for m in MODES}
    val_age = np.array([s.age for s in val])
    val_sex = [s.sex for s in val]
    val_pred = np.array([s.pred for s in val])
    diff = base["clean"] - val_pred
    pred_check = {"bias": float(diff.mean()), "mad": float(np.abs(diff).mean()),
                  "bias_limit": PRED_BIAS_LIMIT, "mad_limit": PRED_MAD_LIMIT}
    pred_check["ok"] = abs(pred_check["bias"]) <= PRED_BIAS_LIMIT and pred_check["mad"] <= PRED_MAD_LIMIT
    pred_text = (f"Сверка pred: смещение {pred_check['bias']:+.2f} (порог {PRED_BIAS_LIMIT}), "
                 f"средняя абсолютная разница {pred_check['mad']:.2f} (порог {PRED_MAD_LIMIT}): "
                 + ("в норме" if pred_check["ok"] else "ПРЕВЫШЕН ПОРОГ. " + WARN_TEXT))
    print(pred_text)
    if not pred_check["ok"]:
        print("!" * 70 + "\nВНИМАНИЕ: " + WARN_TEXT + "\n" + "!" * 70)

    epoch_seconds = []
    for epoch in range(start, args.epochs):
        t0 = time.time()
        model.train()
        if args.freeze_bn:
            for module in model.modules():
                if isinstance(module, torch.nn.modules.batchnorm._BatchNorm):
                    module.eval()
        total, count = 0.0, 0
        for data, age in train_loader:
            data, age = data.to(device), age.to(device)
            with torch.autocast(device_type=device, enabled=device == "cuda"):
                loss = loss_fn(model, kind, data, age)
            opt.zero_grad()
            scaler.scale(loss).backward()
            scaler.step(opt)
            scaler.update()
            sched.step()
            total += loss.item() * len(age)
            count += len(age)
        mae = {}
        for m in MODES:
            pr = evaluate(model, kind, val_loaders[m], device)
            mae[m] = float(np.abs(pr - np.array([s.age for s in val_t])).mean())
        seconds = time.time() - t0
        epoch_seconds.append(seconds)
        history.append({"epoch": epoch + 1, "loss": total / count, "mae_clean": mae["clean"],
                        "mae_phone": mae["phone"], "seconds": seconds})
        print(f"Эпоха {epoch + 1}/{args.epochs}: потеря {total / count:.3f}, MAE clean {mae['clean']:.3f}, "
              f"MAE phone {mae['phone']:.3f}, {seconds:.0f} с", flush=True)
        if mae["phone"] < best:
            best = mae["phone"]
            torch.save(model.state_dict(), out / "best.pt")
        torch.save({"model": model.state_dict(), "opt": opt.state_dict(), "sched": sched.state_dict(),
                    "history": history, "best": best, "epoch": epoch + 1}, out / "last.pt")

    # Результат: лучшая эпоха -> ONNX -> отчёт.
    model.load_state_dict(torch.load(out / "best.pt", map_location=device))
    onnx_diff = export_onnx(model, kind, str(out / "model.onnx"))
    trained = {m: evaluate(model, kind, make_loader(val, export, size, m, args, device=device), device) for m in MODES}
    raw = {"original": base, "calibrated": {m: np.array([calibrate(float(a)) for a in base[m]]) for m in MODES},
           "trained": trained}
    mask = np.array([in_target(a) for a in val_age])
    result = {}
    outside = {}
    for name, per_mode in raw.items():
        result[name] = {}
        for m in MODES:
            result[name][m] = stats(per_mode[m][mask], val_age[mask], np.array(val_sex)[mask])
            years = np.array([int(a) for a in val_age])
            outside[f"{name}/{m}"] = {str(y): float(np.abs(per_mode[m][~mask & (years == y)] - val_age[~mask & (years == y)]).mean())
                                      for y in sorted(set(years[~mask]))}
    report = {"args": vars(args), "device": device, "counts": {"train": len(train), "val": len(val), "val_target": int(mask.sum())},
              "pred_check": pred_check, "onnx_diff_years": onnx_diff, "epoch_seconds": epoch_seconds,
              "history": history, "models": result, "outside_target_mae": outside}
    (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "report.md").write_text(render(report, pred_text), encoding="utf-8")
    print(f"Готово: {out}")


def render(r: dict, pred_text: str) -> str:
    """Текст report.md."""
    a = r["args"]
    L = [f"# Отчёт обучения: {a['model']}", "",
         f"Отложенные примеры, возрасты 13–50, усреднение «кадр + зеркало». Режимы: clean — без искажений, "
         f"phone — фиксированные искажения.", ""]
    L += ["## Сводка", "", "| Модель | Режим | MAE | Смещение | ≤1 | ≤2 | ≤3 | ≤5 |", "|---|---|---|---|---|---|---|---|"]
    for name, per_mode in r["models"].items():
        for m, s in per_mode.items():
            w = s["within"]
            L.append(f"| {NAMES[name]} | {m} | {s['mae']:.2f} | {s['bias']:+.2f} | {w['1']:.0%} | {w['2']:.0%} | {w['3']:.0%} | {w['5']:.0%} |")
    L += ["", "## MAE по полу", "", "| Модель | Режим | " + " | ".join(
        f"{k} (n={v['n']})" for k, v in next(iter(r['models'].values()))["clean"]["by_sex"].items()) + " |",
        "|---|---|" + "---|" * len(next(iter(r["models"].values()))["clean"]["by_sex"])]
    for name, per_mode in r["models"].items():
        for m, s in per_mode.items():
            L.append(f"| {NAMES[name]} | {m} | " + " | ".join(f"{v['mae']:.2f}" for v in s["by_sex"].values()) + " |")
    for m in MODES:
        L += ["", f"## По годам возраста, режим {m} (MAE / смещение)", "",
              "| Год | n | " + " | ".join(NAMES[n] for n in r["models"]) + " |", "|---|---|" + "---|" * len(r["models"])]
        years = r["models"]["trained"][m]["by_year"]
        for y, v in years.items():
            cells = [f"{r['models'][n][m]['by_year'][y]['mae']:.2f} / {r['models'][n][m]['by_year'][y]['bias']:+.2f}" for n in r["models"]]
            L.append(f"| {y} | {v['n']} | " + " | ".join(cells) + " |")
    L += ["", "## MAE по возрастам вне 13–50", "", "| Год | " + " | ".join(r["outside_target_mae"]) + " |",
          "|---|" + "---|" * len(r["outside_target_mae"])]
    years = sorted({int(y) for v in r["outside_target_mae"].values() for y in v})
    for y in years:
        L.append(f"| {y} | " + " | ".join(f"{v[str(y)]:.2f}" if str(y) in v else "" for v in r["outside_target_mae"].values()) + " |")
    L += ["", "## Сверка с полем pred", "", pred_text, "",
          f"## Экспорт в ONNX", "", f"Расхождение ONNX Runtime с PyTorch: {r['onnx_diff_years']:.2e} года.", "",
          "## Запуск", "", f"Параметры: `{json.dumps(a, ensure_ascii=False)}`", f"Устройство: {r['device']}",
          f"Примеров: обучение {r['counts']['train']}, отложенных {r['counts']['val']}, из них 13–50 лет {r['counts']['val_target']}.",
          f"Среднее время эпохи: {np.mean(r['epoch_seconds']):.0f} с." if r["epoch_seconds"] else ""]
    return "\n".join(L) + "\n"


if __name__ == "__main__":
    main()
