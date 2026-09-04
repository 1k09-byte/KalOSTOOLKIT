Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Start-Sleep -Seconds 1
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$out = Join-Path $env:TEMP 'home_check.png'
$bmp.Save($out)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $out"
