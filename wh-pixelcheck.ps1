Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile('C:\Users\Administrator\AppData\Local\Temp\home_check.png')
# Sample the center-left area where the module tiles render
$colors = New-Object System.Collections.Generic.HashSet[string]
$samples = 0; $sum = 0.0
for ($x = 100; $x -lt 1000; $x += 20) {
    for ($y = 100; $y -lt 500; $y += 20) {
        $c = $bmp.GetPixel($x, $y)
        [void]$colors.Add(('{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B))
        $sum += ($c.R + $c.G + $c.B) / 3.0
        $samples++
    }
}
$avg = [math]::Round($sum / $samples, 1)
Write-Host ("region: {0} samples, {1} distinct colors, avg brightness {2}/255" -f $samples, $colors.Count, $avg)
$bmp.Dispose()
