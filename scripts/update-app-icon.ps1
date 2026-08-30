param(
    [string]$SourcePng = (Join-Path $PSScriptRoot '..\design\app-icon-a-transparent-master.png'),
    [string]$OutputPng = (Join-Path $PSScriptRoot '..\src\MinecraftServerManager\Assets\AppIcon.png'),
    [string]$OutputIco = (Join-Path $PSScriptRoot '..\src\MinecraftServerManager\Assets\AppIcon.ico'),
    [ValidateRange(0.75, 0.96)]
    [double]$CanvasFill = 0.92
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$SourcePng = [IO.Path]::GetFullPath($SourcePng)
$OutputPng = [IO.Path]::GetFullPath($OutputPng)
$OutputIco = [IO.Path]::GetFullPath($OutputIco)
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

function Get-AlphaBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    $minimumX = $Bitmap.Width
    $minimumY = $Bitmap.Height
    $maximumX = -1
    $maximumY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                continue
            }

            $minimumX = [Math]::Min($minimumX, $x)
            $minimumY = [Math]::Min($minimumY, $y)
            $maximumX = [Math]::Max($maximumX, $x)
            $maximumY = [Math]::Max($maximumY, $y)
        }
    }

    if ($maximumX -lt 0) {
        throw "The source icon contains no visible pixels: $SourcePng"
    }

    return [System.Drawing.Rectangle]::FromLTRB(
        $minimumX,
        $minimumY,
        $maximumX + 1,
        $maximumY + 1)
}

function New-TransparentBitmap {
    param(
        [int]$Width,
        [int]$Height
    )

    return [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}

function Set-HighQualityGraphics {
    param([System.Drawing.Graphics]$Graphics)

    $Graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $Graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
}

function New-ScaledBitmap {
    param(
        [System.Drawing.Image]$Source,
        [int]$Size
    )

    $scaled = New-TransparentBitmap -Width $Size -Height $Size
    $graphics = [System.Drawing.Graphics]::FromImage($scaled)
    try {
        Set-HighQualityGraphics -Graphics $graphics
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage(
            $Source,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0,
            0,
            $Source.Width,
            $Source.Height,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    return $scaled
}

function ConvertTo-PngBytes {
    param([System.Drawing.Image]$Image)

    $stream = [IO.MemoryStream]::new()
    try {
        $Image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return [byte[]]$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [System.Drawing.Image]$Source,
        [string]$Path
    )

    $frames = @()
    foreach ($size in $iconSizes) {
        $frameBitmap = New-ScaledBitmap -Source $Source -Size $size
        try {
            [byte[]]$frameBytes = ConvertTo-PngBytes -Image $frameBitmap
            $frames += ,$frameBytes
        }
        finally {
            $frameBitmap.Dispose()
        }
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$iconSizes.Count)

        $payloadOffset = 6 + (16 * $iconSizes.Count)
        for ($index = 0; $index -lt $iconSizes.Count; $index++) {
            $size = $iconSizes[$index]
            $dimensionByte = if ($size -eq 256) { [byte]0 } else { [byte]$size }
            $frame = $frames[$index]

            $writer.Write($dimensionByte)
            $writer.Write($dimensionByte)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Length)
            $writer.Write([UInt32]$payloadOffset)

            $payloadOffset += $frame.Length
        }

        foreach ($frame in $frames) {
            $writer.Write([byte[]]$frame)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $SourcePng -PathType Leaf)) {
    throw "Source icon not found: $SourcePng"
}

$outputDirectory = Split-Path -Parent $OutputPng
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$temporaryPng = Join-Path $outputDirectory ('.AppIcon-{0}.png' -f [Guid]::NewGuid().ToString('N'))
$temporaryIco = Join-Path $outputDirectory ('.AppIcon-{0}.ico' -f [Guid]::NewGuid().ToString('N'))
$sourceBitmap = [System.Drawing.Bitmap]::new($SourcePng)
$renderedBitmap = $null

try {
    $visibleBounds = Get-AlphaBounds -Bitmap $sourceBitmap

    # Retain a few transparent source pixels so bicubic resampling keeps clean edges.
    $sourcePadding = 4
    $sourceRectangle = [System.Drawing.Rectangle]::FromLTRB(
        [Math]::Max(0, $visibleBounds.Left - $sourcePadding),
        [Math]::Max(0, $visibleBounds.Top - $sourcePadding),
        [Math]::Min($sourceBitmap.Width, $visibleBounds.Right + $sourcePadding),
        [Math]::Min($sourceBitmap.Height, $visibleBounds.Bottom + $sourcePadding))

    $canvasSize = [Math]::Max($sourceBitmap.Width, $sourceBitmap.Height)
    $longestSourceEdge = [Math]::Max($sourceRectangle.Width, $sourceRectangle.Height)
    $scale = ($canvasSize * $CanvasFill) / $longestSourceEdge
    $targetWidth = [int][Math]::Round($sourceRectangle.Width * $scale)
    $targetHeight = [int][Math]::Round($sourceRectangle.Height * $scale)
    $targetX = [int][Math]::Round(($canvasSize - $targetWidth) / 2.0)
    $targetY = [int][Math]::Round(($canvasSize - $targetHeight) / 2.0)
    $targetRectangle = [System.Drawing.Rectangle]::new($targetX, $targetY, $targetWidth, $targetHeight)

    $renderedBitmap = New-TransparentBitmap -Width $canvasSize -Height $canvasSize
    $graphics = [System.Drawing.Graphics]::FromImage($renderedBitmap)
    try {
        Set-HighQualityGraphics -Graphics $graphics
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage(
            $sourceBitmap,
            $targetRectangle,
            $sourceRectangle,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    $renderedBitmap.Save($temporaryPng, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-MultiSizeIcon -Source $renderedBitmap -Path $temporaryIco

    Move-Item -LiteralPath $temporaryPng -Destination $OutputPng -Force
    Move-Item -LiteralPath $temporaryIco -Destination $OutputIco -Force

    Write-Output ('Updated app icon: visible source {0}x{1}; output canvas {2}x{2}; target fill {3:P0}.' -f `
        $visibleBounds.Width,
        $visibleBounds.Height,
        $canvasSize,
        $CanvasFill)
}
finally {
    if ($null -ne $renderedBitmap) {
        $renderedBitmap.Dispose()
    }

    $sourceBitmap.Dispose()

    if (Test-Path -LiteralPath $temporaryPng) {
        Remove-Item -LiteralPath $temporaryPng -Force
    }

    if (Test-Path -LiteralPath $temporaryIco) {
        Remove-Item -LiteralPath $temporaryIco -Force
    }
}
