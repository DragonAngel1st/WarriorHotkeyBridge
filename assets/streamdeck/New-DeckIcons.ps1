<#
.SYNOPSIS
    Generates the Go and Stop Stream Deck button images.

.DESCRIPTION
    Kept as a generator rather than as checked-in binaries alone, so the icons can be recoloured
    or resized without redrawing them by hand.

    Design constraints, all deliberate:

      * No text. A deck key is read at a glance, in peripheral vision, while looking at charts.
        A glyph survives that; a word does not.
      * No red, pink or yellow - those carry meaning on the surrounding trading keys, and a
        session control that borrowed one of those colours would read as an order action.
      * Green for Go and cyan for Stop. Both are unambiguous against the excluded palette, and
        they differ in hue AND in shape, so the pair is still distinguishable to anyone with red
        or green colour blindness.
      * A filled triangle and a power glyph rather than triangle/square. Stop here does not pause
        anything - it ends the session and hands the keys back to Windows - and the power symbol
        says "off" where a square says "halted".

.PARAMETER OutputDirectory
    Where the PNGs are written. Defaults to the folder containing this script.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Not defaulted in the param block: $PSScriptRoot is not reliably populated there when the script
# is invoked by relative path, which produced an empty Path and a confusing binding error.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
}

Add-Type -AssemblyName System.Drawing

# Stream Deck hardware is 72x72 (original/Mini), 96x96 (MK.2/XL) and 200x200 (Plus touch).
# Rendering at 288 and downsampling gives clean edges on all of them; most deck software,
# including MiraBox and Soomfon, accepts the larger file and scales it itself.
$sizes = @(288, 144, 96, 72)

function New-ButtonBitmap {
    param(
        [int] $Size,
        [scriptblock] $DrawGlyph,
        [System.Drawing.Color] $Accent
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Background: a near-black rounded square with a slight vertical lift, so the key does not
    # read as a black hole next to lit neighbours.
    $radius = [int]($Size * 0.18)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $top = [System.Drawing.Color]::FromArgb(255, 26, 30, 38)
    $bottom = [System.Drawing.Color]::FromArgb(255, 14, 16, 21)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $Size)), $top, $bottom)
    $g.FillPath($bg, $path)

    # A soft halo in the accent colour. This is what makes the two keys distinguishable in the
    # dark, at the edge of vision, before the glyph itself resolves.
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $inset = [int]($Size * 0.12)
    $glowPath.AddEllipse($inset, $inset, $Size - ($inset * 2), $Size - ($inset * 2))
    $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $glow.CenterColor = [System.Drawing.Color]::FromArgb(70, $Accent.R, $Accent.G, $Accent.B)
    $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $Accent.R, $Accent.G, $Accent.B))
    $g.FillPath($glow, $glowPath)

    & $DrawGlyph $g $Size $Accent

    # A hairline rim lifts the key off the deck's black bezel.
    $rim = New-Object System.Drawing.Pen(
        [System.Drawing.Color]::FromArgb(38, $Accent.R, $Accent.G, $Accent.B),
        [Math]::Max(1, $Size / 96))
    $g.DrawPath($rim, $path)

    $rim.Dispose(); $glow.Dispose(); $glowPath.Dispose(); $bg.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# --- Go: a filled play triangle. The most universally understood "start" glyph there is. ---
$drawGo = {
    param($g, $Size, $Accent)

    $cx = $Size / 2.0
    $cy = $Size / 2.0
    $r = $Size * 0.235

    # Nudged right so the triangle's visual centre of mass sits on the key's centre; a
    # geometrically centred triangle always looks shifted left.
    $cx += $Size * 0.025

    $points = @(
        (New-Object System.Drawing.PointF(($cx + $r), $cy)),
        (New-Object System.Drawing.PointF(($cx - $r * 0.82), ($cy - $r * 0.95))),
        (New-Object System.Drawing.PointF(($cx - $r * 0.82), ($cy + $r * 0.95)))
    )

    $tri = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tri.AddPolygon($points)

    # Rounded corners: a razor-sharp triangle looks broken at 72 px.
    $pen = New-Object System.Drawing.Pen($Accent, ($Size * 0.085))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $brush = New-Object System.Drawing.SolidBrush($Accent)
    $g.FillPath($brush, $tri)
    $g.DrawPath($pen, $tri)

    $brush.Dispose(); $pen.Dispose(); $tri.Dispose()
}

# --- Stop: the IEC power glyph. Says "off", not "paused" - the session ends and the keys are
#     handed back to Windows. ---
$drawStop = {
    param($g, $Size, $Accent)

    $cx = $Size / 2.0
    $cy = $Size / 2.0
    $r = $Size * 0.215
    $thickness = $Size * 0.105

    $pen = New-Object System.Drawing.Pen($Accent, $thickness)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Arc open at the top, from 300 deg sweeping 300 deg, leaving a 60 deg gap for the bar.
    $g.DrawArc($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2), -60, 300)

    # The vertical bar, starting inside the gap and rising above the circle.
    $g.DrawLine($pen, $cx, ($cy - $r * 1.32), $cx, ($cy - $r * 0.18))

    $pen.Dispose()
}

$buttons = @(
    @{ Name = 'go';   Draw = $drawGo;   Accent = [System.Drawing.Color]::FromArgb(255, 45, 214, 122) }  # emerald
    @{ Name = 'stop'; Draw = $drawStop; Accent = [System.Drawing.Color]::FromArgb(255, 56, 189, 248) }  # cyan
)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

foreach ($button in $buttons) {
    foreach ($size in $sizes) {
        $bmp = New-ButtonBitmap -Size $size -DrawGlyph $button.Draw -Accent $button.Accent
        $file = Join-Path $OutputDirectory ("{0}-{1}.png" -f $button.Name, $size)
        $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host ("  {0,-14} {1}x{1}" -f (Split-Path $file -Leaf), $size)
    }
}

Write-Host "`nWritten to $OutputDirectory" -ForegroundColor Green
