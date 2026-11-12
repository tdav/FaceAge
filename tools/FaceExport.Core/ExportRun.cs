using System.Data;
using System.Data.Common;
using System.Globalization;

namespace FaceAge.Export;

/// <summary>Состояние выгрузки после очередной строки или пары. Line — готовая строка журнала, когда есть что сказать.</summary>
public readonly record struct ExportProgress(
    int Age, string Sex, int PairIndex, int PairCount,
    int Returned, int Saved, int Skipped, int TotalSaved, int TotalSkipped,
    string? Line);

/// <summary>Расшифровка фото в пиксели R,G,B,A построчно (шаг строки = width * 4); false — файл не открылся.</summary>
public delegate bool PhotoDecoder(byte[] photo, out byte[] rgba, out int width, out int height);

/// <summary>
/// Цикл выгрузки «возраст × пол → запрос → строки» поверх общего ADO.NET. Ни один путь не возвращает управление молча:
/// пара помечается завершённой только после чтения всех строк; при отмене и сбое исключение уходит наверх.
/// </summary>
public sealed class ExportRun
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly FaceCropper cropper;
    private readonly ExportFolder folder;
    private readonly PhotoDecoder decode;
    private readonly Func<byte[], byte[]> encodeJpeg;

    /// <param name="encodeJpeg">RGB 256×256×3 → JPEG с качеством 95.</param>
    public ExportRun(FaceCropper cropper, ExportFolder folder, PhotoDecoder decode, Func<byte[], byte[]> encodeJpeg)
    {
        this.cropper = cropper;
        this.folder = folder;
        this.decode = decode;
        this.encodeJpeg = encodeJpeg;
    }

    /// <summary>
    /// Соединение приходит открытым. Запрос возвращает поля по позиции: 0 — фото, 1 — дата рождения, 2 — дата съёмки;
    /// параметры называются age и sex (без двоеточия — так требует Oracle).
    /// </summary>
    public void Run(
        DbConnection connection, string sql, int ageFrom, int ageTo, IReadOnlyList<string> sexes,
        Action<DbCommand>? configure, IProgress<ExportProgress> progress, CancellationToken token)
    {
        var pairs = new List<(int Age, string Sex)>();
        for (var age = ageFrom; age <= ageTo; age++)
        {
            foreach (var sex in sexes)
            {
                pairs.Add((age, sex));
            }
        }

        if (pairs.Count == 0)
        {
            throw new ArgumentException("Нет пар для выгрузки: проверьте диапазон возрастов и значения пола");
        }

        var totalSaved = 0;
        var totalSkipped = 0;
        var ran = 0;

        for (var index = 0; index < pairs.Count; index++)
        {
            var (pairAge, pairSex) = pairs[index];
            token.ThrowIfCancellationRequested();

            if (this.folder.Completed.Contains((pairAge, pairSex)))
            {
                progress.Report(new ExportProgress(pairAge, pairSex, index + 1, pairs.Count, 0, 0, 0, totalSaved, totalSkipped, $"возраст {pairAge}, {pairSex}: уже выгружено"));
                continue;
            }

            ran++;
            var returned = 0;
            var saved = 0;
            var skipped = 0;
            var reasons = new Dictionary<string, int>();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            var ageParameter = command.CreateParameter();
            ageParameter.ParameterName = "age";
            ageParameter.DbType = DbType.Int32;
            ageParameter.Value = pairAge;
            command.Parameters.Add(ageParameter);
            var sexParameter = command.CreateParameter();
            sexParameter.ParameterName = "sex";
            sexParameter.DbType = DbType.String;
            sexParameter.Value = pairSex;
            command.Parameters.Add(sexParameter);
            configure?.Invoke(command);

            // Без этого «Стоп» ждал бы конца запроса.
            using var registration = token.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch (Exception)
                {
                    // Драйвер или закрытое соединение могут бросить из Cancel; это не должно всплыть в потоке, отменившем токен.
                }
            });

            try
            {
                token.ThrowIfCancellationRequested();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    returned++;

                    string? reason = null;
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                    {
                        reason = "даты или фото пустые";
                    }
                    else
                    {
                        if (reader.GetValue(0) is not byte[] photo)
                        {
                            throw new InvalidOperationException($"Поле 1 должно быть фото (BLOB), а пришло {reader.GetValue(0).GetType().Name}");
                        }

                        DateTime birth;
                        DateTime shot;
                        try
                        {
                            birth = reader.GetDateTime(1);
                        }
                        catch (Exception e) when (e is InvalidCastException or FormatException)
                        {
                            throw new InvalidOperationException($"Поле 2 должно быть датой рождения (DATE), а пришло {reader.GetValue(1).GetType().Name}", e);
                        }

                        try
                        {
                            shot = reader.GetDateTime(2);
                        }
                        catch (Exception e) when (e is InvalidCastException or FormatException)
                        {
                            throw new InvalidOperationException($"Поле 3 должно быть датой съёмки (DATE), а пришло {reader.GetValue(2).GetType().Name}", e);
                        }

                        if (shot < birth)
                        {
                            reason = "даты или фото пустые";
                        }
                        else if (!this.decode(photo, out var rgba, out var width, out var height))
                        {
                            reason = "не открылось";
                        }
                        else
                        {
                            var crop = this.cropper.Cut(rgba, width, height, width * 4);
                            if (!crop.Found)
                            {
                                reason = "лицо не найдено";
                            }
                            else
                            {
                                this.folder.Add(this.encodeJpeg(crop.Rgb), DateOnly.FromDateTime(birth), DateOnly.FromDateTime(shot), pairSex, crop.Pred);
                                saved++;
                            }
                        }
                    }

                    if (reason is not null)
                    {
                        skipped++;
                        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    }

                    progress.Report(new ExportProgress(pairAge, pairSex, index + 1, pairs.Count, returned, saved, skipped, totalSaved + saved, totalSkipped + skipped, null));
                }
            }
            catch (Exception e) when (token.IsCancellationRequested && e is not OperationCanceledException)
            {
                // Отмена во время запроса драйвер может показать собственным исключением — наружу уходит отмена.
                throw new OperationCanceledException(e.Message, e, token);
            }

            // Драйвер мог тихо закончить выборку по Cancel(): неполную пару завершённой не помечаем.
            token.ThrowIfCancellationRequested();

            totalSaved += saved;
            totalSkipped += skipped;
            this.folder.CompletePair(pairAge, pairSex, returned, saved, skipped);

            string line;
            if (returned == 0)
            {
                line = $"возраст {pairAge}, {pairSex}: в базе нет фото";
            }
            else
            {
                line = $"возраст {pairAge}, {pairSex}: сохранено {saved.ToString("N0", Ru)}, пропущено {skipped.ToString("N0", Ru)}";
                if (skipped > 0)
                {
                    line += " (" + string.Join(", ", reasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value.ToString("N0", Ru)}")) + ")";
                }

                if (skipped * 10 > returned)
                {
                    line += " — ВНИМАНИЕ: пропущена большая доля фото";
                }
            }

            progress.Report(new ExportProgress(pairAge, pairSex, index + 1, pairs.Count, returned, saved, skipped, totalSaved, totalSkipped, line));
        }

        if (ran == 0)
        {
            var last = pairs[^1];
            progress.Report(new ExportProgress(last.Age, last.Sex, pairs.Count, pairs.Count, 0, 0, 0, 0, 0, "Все пары уже выгружены"));
        }
    }
}
