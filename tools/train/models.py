# Модели возраста: small (genderage.onnx в PyTorch) и large (MobileNetV3 из timm), потери и экспорт в ONNX.
# Вход везде: float32 [N,3,S,S], RGB, значения 0…255; нормализация внутри модели.
import numpy as np
import onnx
import onnxruntime as ort
import timm
import torch
from onnx2torch import convert
from torch import nn
from torch.nn import functional as F

SMALL_INPUT = 96
LARGE_INPUT = 224
LARGE_BACKBONE = "mobilenetv3_large_100.ra_in1k"

CLASSES = 101
SIGMA = 2.0


class LargeAgeNet(nn.Module):
    """Основа timm; forward даёт ожидаемый возраст по 101 классу (0…100 лет), logits — сами классы."""

    def __init__(self, pretrained: bool):
        super().__init__()
        self.backbone = timm.create_model(LARGE_BACKBONE, pretrained=pretrained, num_classes=CLASSES)
        cfg = self.backbone.pretrained_cfg
        self.register_buffer("mean", torch.tensor(cfg["mean"]).view(1, 3, 1, 1) * 255)
        self.register_buffer("std", torch.tensor(cfg["std"]).view(1, 3, 1, 1) * 255)
        self.register_buffer("ages", torch.arange(CLASSES, dtype=torch.float32))

    def logits(self, data: torch.Tensor) -> torch.Tensor:
        return self.backbone((data - self.mean) / self.std)

    def forward(self, data: torch.Tensor) -> torch.Tensor:
        return (self.logits(data).softmax(dim=1) * self.ages).sum(dim=1)


def build_small(onnx_path: str) -> torch.nn.Module:
    """Переводит genderage.onnx в PyTorch; выход [N,3]: пол, пол, возраст/100."""
    model = onnx.load(onnx_path)
    net = convert(model)
    net.eval()
    sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
    x = np.random.default_rng(0).uniform(0, 255, (2, 3, SMALL_INPUT, SMALL_INPUT)).astype(np.float32)
    ref = sess.run(None, {sess.get_inputs()[0].name: x})[0]
    with torch.no_grad():
        got = net(torch.from_numpy(x)).numpy()
    diff = float(np.abs(ref - got).max())
    if diff > 1e-4:
        raise RuntimeError(f"Перевод в PyTorch расходится с ONNX Runtime: {diff}")
    net.train()
    return net


def build_large(pretrained: bool) -> torch.nn.Module:
    return LargeAgeNet(pretrained)


def predict_age(model, kind: str, data: torch.Tensor) -> torch.Tensor:
    """Возраст в годах, [N]."""
    if kind == "small":
        return model(data)[:, 2] * 100
    return model(data)


def loss_fn(model, kind: str, data: torch.Tensor, age: torch.Tensor) -> torch.Tensor:
    age = age.clamp(0, 100)
    if kind == "small":
        return (predict_age(model, kind, data) - age).abs().mean()
    logits = model.logits(data)
    expected = (logits.softmax(dim=1) * model.ages).sum(dim=1)
    target = torch.exp(-((model.ages[None, :] - age[:, None]) ** 2) / (2 * SIGMA ** 2))
    target = target / target.sum(dim=1, keepdim=True)
    kl = F.kl_div(logits.log_softmax(dim=1), target, reduction="batchmean")
    return (expected - age).abs().mean() + kl


def export_onnx(model, kind: str, path: str) -> float:
    """Экспортирует модель и возвращает наибольшее расхождение с PyTorch в годах (порог 0,01)."""
    size = SMALL_INPUT if kind == "small" else LARGE_INPUT
    out_name = "fc1" if kind == "small" else "age"
    was_training = model.training
    model.eval()
    try:
        x = torch.from_numpy(np.random.default_rng(0).uniform(0, 255, (2, 3, size, size)).astype(np.float32))
        torch.onnx.export(model, (x,), path, input_names=["data"], output_names=[out_name],
                          dynamic_axes={"data": {0: "N"}, out_name: {0: "N"}}, opset_version=17, dynamo=False)
        sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        diff = 0.0
        for n in (1, 2):
            xn = x[:n].numpy()
            with torch.no_grad():
                ref = predict_age(model, kind, x[:n]).numpy()
            out = sess.run(None, {"data": xn})[0]
            got = out[:, 2] * 100 if kind == "small" else out
            diff = max(diff, float(np.abs(ref - got).max()))
    finally:
        model.train(was_training)
    if diff > 0.01:
        raise RuntimeError(f"Экспорт расходится с PyTorch: {diff} года")
    return diff
