# Спайк: genderage.onnx -> PyTorch (onnx2torch) -> обучаемость -> обратно в ONNX.
# Запуск: python -I spike_small.py <genderage.onnx> <out.onnx>
import sys
import time
from collections import Counter

import numpy as np
import onnx
import onnxruntime as ort
import torch
from onnx2torch import convert

src, out = sys.argv[1], sys.argv[2]
m = onnx.load(src)
dims = lambda v: [d.dim_value or d.dim_param for d in v.type.tensor_type.shape.dim]
print("ir", m.ir_version, "opset", [(o.domain, o.version) for o in m.opset_import])
print("inputs", [(i.name, dims(i)) for i in m.graph.input if i.name not in {t.name for t in m.graph.initializer}])
print("outputs", [(o.name, dims(o)) for o in m.graph.output])
print("ops", dict(Counter(n.op_type for n in m.graph.node)))
print("initializers", len(m.graph.initializer), "params", sum(int(np.prod(i.dims)) for i in m.graph.initializer))

sess = ort.InferenceSession(src, providers=["CPUExecutionProvider"])
name = sess.get_inputs()[0].name
x = np.random.default_rng(0).uniform(0, 255, (2, 3, 96, 96)).astype(np.float32)
ref = sess.run(None, {name: x})[0]
print("ref", ref.round(4).tolist())

net = convert(m)
net.eval()
with torch.no_grad():
    got = net(torch.from_numpy(x)).numpy()
print("eval maxdiff", float(np.abs(ref - got).max()))
print("modules", dict(Counter(type(v).__name__ for v in net.modules())))
print("trainable", sum(p.numel() for p in net.parameters() if p.requires_grad),
      "buffers", sum(b.numel() for b in net.buffers()))

net.train()
xb = torch.from_numpy(np.random.default_rng(1).uniform(0, 255, (8, 3, 96, 96)).astype(np.float32))
y = net(xb)
loss = (y[:, 2] * 100 - 30).abs().mean()
loss.backward()
grads = [p for p in net.parameters() if p.grad is not None and float(p.grad.abs().sum()) > 0]
print("train forward", tuple(y.shape), "loss", round(float(loss), 3), "params with grad", len(grads), "of", len(list(net.parameters())))

# Один шаг оптимизатора реально меняет выход.
opt = torch.optim.AdamW(net.parameters(), lr=1e-3)
opt.step()
net.eval()
with torch.no_grad():
    after = net(torch.from_numpy(x)).numpy()
print("changed after step", float(np.abs(after - got).max()))

for dynamo in (False, True):
    try:
        t = time.time()
        kwargs = dict(input_names=["data"], output_names=["fc1"], dynamo=dynamo)
        if dynamo:
            kwargs["dynamic_shapes"] = {"input": {0: torch.export.Dim("N", min=1, max=64)}} if False else None
            kwargs.pop("dynamic_shapes")
            kwargs["dynamic_axes"] = {"data": {0: "N"}, "fc1": {0: "N"}}
        else:
            kwargs["dynamic_axes"] = {"data": {0: "N"}, "fc1": {0: "N"}}
            kwargs["opset_version"] = 17
        path = out.replace(".onnx", f"_dynamo{int(dynamo)}.onnx")
        torch.onnx.export(net, (torch.from_numpy(x),), path, **kwargs)
        s2 = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        got2 = s2.run(None, {"data": x})[0]
        b1 = s2.run(None, {"data": x[:1]})[0]
        em = onnx.load(path)
        print(f"export dynamo={dynamo} ok {time.time() - t:.1f}s maxdiff", float(np.abs(after - got2).max()),
              "in", [(i.name, i.shape) for i in s2.get_inputs()], "out", [(o.name, o.shape) for o in s2.get_outputs()],
              "batch1", b1.shape, "ir", em.ir_version, "opset", [(o.domain, o.version) for o in em.opset_import])
    except Exception as e:  # noqa: BLE001
        print(f"export dynamo={dynamo} FAILED: {type(e).__name__}: {str(e)[:300]}")
