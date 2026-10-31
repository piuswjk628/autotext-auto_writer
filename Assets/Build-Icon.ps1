# Rasterize this site's rectangle-based SVG into a multi-resolution Windows icon.
Add-Type -AssemblyName System.Drawing
[xml]$svg = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'favicon.svg') -Raw
function RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $path.AddRectangle([System.Drawing.RectangleF]::new($x,$y,$w,$h)); return $path }
    $d = $r * 2
    $path.AddArc($x,$y,$d,$d,180,90)
    $path.AddArc(($x+$w-$d),$y,$d,$d,270,90)
    $path.AddArc(($x+$w-$d),($y+$h-$d),$d,$d,0,90)
    $path.AddArc($x,($y+$h-$d),$d,$d,90,90)
    $path.CloseFigure()
    return $path
}
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($size/32.0),($size/32.0))
    foreach ($rect in $svg.SelectNodes("//*[local-name()='rect']")) {
        $x = 0; $y = 0; $r = 0
        if ($rect.HasAttribute('x')) { $x = [single]$rect.GetAttribute('x') }
        if ($rect.HasAttribute('y')) { $y = [single]$rect.GetAttribute('y') }
        if ($rect.HasAttribute('rx')) { $r = [single]$rect.GetAttribute('rx') }
        $path = RoundedRect $x $y ([single]$rect.GetAttribute('width')) ([single]$rect.GetAttribute('height')) $r
        $fill = $rect.GetAttribute('fill')
        if (-not $fill) { $fill = $rect.ParentNode.GetAttribute('fill') }
        if (-not $fill) { $fill = '#000' }
        if ($fill -ne 'none') {
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($fill))
            $graphics.FillPath($brush,$path); $brush.Dispose()
        }
        if ($rect.HasAttribute('stroke')) {
            $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($rect.GetAttribute('stroke')),[single]$rect.GetAttribute('stroke-width'))
            $graphics.DrawPath($pen,$path); $pen.Dispose()
        }
        $path.Dispose()
    }
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size=$size; Bytes=$stream.ToArray() }
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.png'),$stream.ToArray()) }
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + $frames.Count * 16
foreach ($frame in $frames) {
    $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app.ico'),$output.ToArray())
$writer.Dispose(); $output.Dispose()
