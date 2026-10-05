param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,
    [Parameter(Mandatory = $true)]
    [string]$LogoPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
[xml]$applicationMarkup = Get-Content -LiteralPath (Join-Path $SourceDirectory 'App.xaml') -Raw
$geometryNode = $applicationMarkup.SelectSingleNode("//*[local-name()='Geometry']")
if ($null -eq $geometryNode) { throw 'The application has no BrandMark geometry.' }
$geometry = [Windows.Media.Geometry]::Parse($geometryNode.InnerText)
$background = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#F2AF78'))
$ink = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#302116'))
$images = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16,32,48,64,128,256)
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.DrawRoundedRectangle($background, $null, [Windows.Rect]::new(0,0,$size,$size), $size*0.24, $size*0.24)
    $drawing.PushTransform([Windows.Media.TranslateTransform]::new($size*0.16,$size*0.13))
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size*0.68/32,$size*0.68/32))
    $pen = [Windows.Media.Pen]::new($ink,1.8)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    $drawing.DrawGeometry($null,$pen,$geometry)
    $drawing.Pop()
    $drawing.Pop()
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
