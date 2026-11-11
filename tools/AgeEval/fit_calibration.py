"""Подбор поправки возраста: кусочно-линейная монотонная функция «сырая оценка → возраст».

Использование: python -I fit_calibration.py raw.csv [мин. возраст] [макс. возраст]
raw.csv пишет AgeEval третьим аргументом. Поправка подбирается на половине годных кадров
и проверяется на другой половине; в конце печатаются узлы для AgeEstimator, подобранные на всех.
"""
import csv, sys, zlib
import numpy as np

path = sys.argv[1]
lo = int(sys.argv[2]) if len(sys.argv) > 2 else 1
hi = int(sys.argv[3]) if len(sys.argv) > 3 else 40
KNOTS = 12

rows = [r for r in csv.DictReader(open(path, encoding='utf-8'))
        if r['quality'] == 'Good' and lo <= int(r['age']) <= hi]
age = np.array([int(r['age']) for r in rows], float)
raw = np.array([float(r['raw']) for r in rows])
# Разбиение на половины по хешу имени файла: воспроизводимо и не зависит от порядка строк.
half = np.array([zlib.crc32(r['file'].encode()) % 2 for r in rows])

def fit(raw, age):
    # Узлы — на квантилях сырой оценки: в каждом отрезке поровну фото. Значение узла — медиана
    # настоящего возраста в отрезке (медиана минимизирует среднюю абсолютную ошибку).
    edges = np.quantile(raw, np.linspace(0, 1, KNOTS + 1))
    xs, ys = [], []
    for a, b in zip(edges[:-1], edges[1:]):
        m = (raw >= a) & (raw <= b)
        xs.append(np.median(raw[m])); ys.append(np.median(age[m]))
    # Монотонность: старше на вид — не моложе по поправке.
    return np.array(xs), np.maximum.accumulate(np.array(ys))

def apply(xs, ys, r):
    # За краями — сдвиг крайнего узла, а не обрезка: возраст вне набора не прилипает к границе.
    return np.where(r < xs[0], r + ys[0] - xs[0], np.where(r > xs[-1], r + ys[-1] - xs[-1], np.interp(r, xs, ys)))

def report(name, pred, age):
    err = pred - age
    groups = '  '.join(f'{a}–{b}: {np.abs(err[(age >= a) & (age <= b)]).mean():.2f}'
                       for a, b in ((13, 19), (20, 29), (30, 40)) if ((age >= a) & (age <= b)).any())
    in_range = (age >= 13) & (age <= 40)
    print(f'{name:<22} MAE {np.abs(err).mean():5.2f}  смещ. {err.mean():+5.2f}  ≤3 года {np.mean(np.abs(err) <= 3) * 100:3.0f}%'
          f'  | 13–40: MAE {np.abs(err[in_range]).mean():.2f}  [{groups}]')

print(f'Годных кадров {len(age)}, возраст {lo}–{hi}')
for train, test in ((0, 1), (1, 0)):
    xs, ys = fit(raw[half == train], age[half == train])
    t = half == test
    print(f'-- подбор на половине {train}, проверка на половине {test} ({t.sum()} фото)')
    report('без поправки', raw[t], age[t])
    report('с поправкой', apply(xs, ys, raw[t]), age[t])

xs, ys = fit(raw, age)
print('\nУзлы для AgeEstimator.Calibration (подобраны на всех годных кадрах):')
print('[' + ', '.join(f'({x:.1f}f, {y:.1f}f)' for x, y in zip(xs, ys)) + ']')
