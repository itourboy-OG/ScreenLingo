param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,
    [Parameter(Mandatory = $true)]
    [string]$LogoPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$brandPath = Join-Path $SourceDirectory 'assets\brand-icon.png'
if (-not (Test-Path -LiteralPath $brandPath -PathType Leaf)) { throw "The ScreenLingo brand icon is missing: $brandPath." }
$brand = [Windows.Media.Imaging.BitmapImage]::new([Uri]::new($brandPath))
$images = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,32,48,64,128,256)
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual,[Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing.DrawImage($brand,[Windows.Rect]::new(0,0,$size,$size))
    $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $images.Add($stream.ToArray())
    $stream.Dispose()
}
$iconPath = Join-Path $SourceDirectory 'app.ico'
$iconStream = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16*$sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$images[$index].Length)
        $writer.Write([UInt32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($imageBytes in $images) { $writer.Write($imageBytes) }
} finally { $writer.Dispose() }
[IO.File]::WriteAllBytes($LogoPath,$images[$images.Count-1])
[IO.File]::WriteAllBytes((Join-Path $SourceDirectory 'assets\logo.png'),$images[$images.Count-1])
