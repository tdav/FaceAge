# Скачивает файлы, которые нельзя распространять в публичном репозитории:
#  - модель возраста InsightFace genderage.onnx (только некоммерческое использование);
#  - тестовые фото Vecteezy (бесплатная лицензия запрещает выкладывать сами файлы).
# Каждый файл сверяется по SHA-256: подменённый или изменённый файл не будет использован.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$assets = @(
    @{
        Path = 'src\FaceAge\Resources\Raw\genderage.onnx'
        Url = 'https://huggingface.co/public-data/insightface/resolve/main/models/buffalo_l/genderage.onnx'
        Sha256 = '4FDE69B1C810857B88C64A335084F1C3FE8F01246C9A191B48C7BB756D6652FB'
    },
    @{
        Path = 'tests\FaceAge.Tests\Photos\passport-man-middle-aged.jpg'
        Url = 'https://static.vecteezy.com/system/resources/thumbnails/052/606/659/small/passport-portrait-of-middle-aged-man-looking-at-camera-over-white-studio-background-isolated-photo.jpg'
        Sha256 = '383F54487BAF240881389B02CC780965705AA92750EB9497797A2424DFC5A47F'
    },
    @{
        Path = 'tests\FaceAge.Tests\Photos\passport-man-young.jpg'
        Url = 'https://static.vecteezy.com/system/resources/thumbnails/057/704/953/small/a-young-man-in-a-white-shirt-and-blue-tie-free-photo.jpg'
        Sha256 = '7F25FD9024CA1788F1B33EFF743400A47171AA681A3135B80383BC79B93AC5DD'
    }
)

foreach ($asset in $assets) {
    $target = Join-Path $root $asset.Path
    if ((Test-Path $target) -and (Get-FileHash $target).Hash -eq $asset.Sha256) {
        Write-Host "Уже есть: $($asset.Path)"
        continue
    }

    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    $temp = "$target.download"
    Write-Host "Скачивание: $($asset.Path)"
    Invoke-WebRequest $asset.Url -OutFile $temp -UserAgent 'Mozilla/5.0' -TimeoutSec 120

    $hash = (Get-FileHash $temp).Hash
    if ($hash -ne $asset.Sha256) {
        Remove-Item $temp
        throw "SHA-256 не совпал для $($asset.Path): ожидался $($asset.Sha256), получен $hash"
    }

    Move-Item $temp $target -Force
}

Write-Host 'Готово.'
