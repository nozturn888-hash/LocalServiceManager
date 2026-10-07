$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot=Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
[IO.Directory]::CreateDirectory($assetRoot) | Out-Null
$frames=@()
foreach($size in @(16,24,32,48,64,128,256)) {
 $bitmap=New-Object Drawing.Bitmap $size,$size
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $brush=New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(39,116,182))
 $unit=$size/32.0
 $pen=New-Object Drawing.Pen ([Drawing.Color]::White),([single](1.8*$unit))
 $stream=New-Object IO.MemoryStream
 try {
  $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([Drawing.Color]::Transparent)
  $graphics.FillEllipse($brush,1,1,$size-2,$size-2)
  $graphics.DrawRectangle($pen,[single](8*$unit),[single](8*$unit),[single](16*$unit),[single](7*$unit))
  $graphics.DrawRectangle($pen,[single](8*$unit),[single](18*$unit),[single](16*$unit),[single](7*$unit))
  $graphics.DrawLine($pen,[single](11*$unit),[single](11.5*$unit),[single](13*$unit),[single](11.5*$unit))
  $graphics.DrawLine($pen,[single](11*$unit),[single](21.5*$unit),[single](13*$unit),[single](21.5*$unit))
  $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
  $frames+=,[pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
  if($size -eq 256){$bitmap.Save((Join-Path $assetRoot 'application.png'),[Drawing.Imaging.ImageFormat]::Png)}
 } finally {$stream.Dispose();$pen.Dispose();$brush.Dispose();$graphics.Dispose();$bitmap.Dispose()}
}
$ico=[IO.File]::Create((Join-Path $assetRoot 'application.ico'))
$writer=New-Object IO.BinaryWriter $ico
try {
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
 $offset=6+16*$frames.Count
 foreach($frame in $frames) {
  $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
  $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
  $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
  $offset+=$frame.Bytes.Length
 }
 foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
} finally {$writer.Dispose();$ico.Dispose()}
Write-Output 'Generated original server icon: 16/24/32/48/64/128/256 px.'
