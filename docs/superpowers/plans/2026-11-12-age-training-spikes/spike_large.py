# Спайк: основа timm для модели large — лицензия весов, размер, экспорт в ONNX, скорость на CPU.
# Запуск: python -I spike_large.py <out.onnx>
import sys
import time

import numpy as np
import onnx
import onnxruntime as ort
import timm
import torch
from torch import nn

out = sys.argv[1]
print("timm", timm.__version__, "torch", torch.__version__)
for tag in timm.list_pretrained("mobilenetv3_large_100*") + timm.list_pretrained("mobilenetv4_conv_small*")[:3] + timm.list_pretrained("efficientnet_lite0*"):
    cfg = timm.get_pretrained_cfg(tag)
    print("cfg", tag, "license", getattr(cfg, "license", None), "input", cfg.input_size, "hub", cfg.hf_hub_id, "mean", cfg.mean, "std", cfg.std)


class AgeNet(nn.Module):
    """Вход RGB 0…255, нормализация внутри; выход — ожидаемый возраст по 101 классу (0…100 лет)."""

    def __init__(self, name: str):
        super().__init__()
        self.backbone = timm.create_model(name, pretrained=False, num_classes=101)
        cfg = self.backbone.pretrained_cfg
        self.register_buffer("mean", torch.tensor(cfg["mean"]).view(1, 3, 1, 1) * 255)
        self.register_buffer("std", torch.tensor(cfg["std"]).view(1, 3, 1, 1) * 255)
        self.register_buffer("ages", torch.arange(101, dtype=torch.float32))

    def forward(self, data: torch.Tensor) -> torch.Tensor:
        logits = self.backbone((data - self.mean) / self.std)
        return (logits.softmax(dim=1) * self.ages).sum(dim=1)


x = np.random.default_rng(0).uniform(0, 255, (2, 3, 224, 224)).astype(np.float32)
for name in ("mobilenetv3_large_100", "efficientnet_lite0"):
    net = AgeNet(name).eval()
    print(name, "params", sum(p.numel() for p in net.parameters()))
    with torch.no_grad():
        ref = net(torch.from_numpy(x)).numpy()
    for dynamo in (False, True):
        path = out.replace(".onnx", f"_{name}_dynamo{int(dynamo)}.onnx")
        try:
            kwargs = dict(input_names=["data"], output_names=["age"], dynamo=dynamo,
                          dynamic_axes={"data": {0: "N"}, "age": {0: "N"}})
            if not dynamo:
                kwargs["opset_version"] = 17
            torch.onnx.export(net, (torch.from_numpy(x),), path, **kwargs)
            s = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
            got = s.run(None, {"data": x})[0]
            t = time.time()
            for _ in range(20):
                s.run(None, {"data": x})
            em = onnx.load(path)
            print(f"  export dynamo={dynamo} ok maxdiff", float(np.abs(ref - got).max()), "out", [(o.name, o.shape) for o in s.get_outputs()],
                  "batch1", s.run(None, {"data": x[:1]})[0].shape, f"cpu ms/pair {(time.time() - t) / 20 * 1000:.1f}",
                  "ir", em.ir_version, "opset", [(o.domain, o.version) for o in em.opset_import])
        except Exception as e:  # noqa: BLE001
            print(f"  export dynamo={dynamo} FAILED: {type(e).__name__}: {str(e)[:300]}")
