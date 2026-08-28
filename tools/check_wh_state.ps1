Get-ChildItem 'HKLM:\SOFTWARE\Windhawk\Engine\Mods' | ForEach-Object {
    $n = $_.PSChildName
    $v = Get-ItemProperty ('HKLM:\SOFTWARE\Windhawk\Engine\Mods\' + $n)
    $settings = Get-ItemProperty ('HKLM:\SOFTWARE\Windhawk\Engine\Mods\' + $n + '\Settings') -ErrorAction SilentlyContinue
    $theme = if ($settings) { $settings.Theme } else { '<none>' }
    $mw = Test-Path ('HKLM:\SOFTWARE\Windhawk\Engine\ModsWritable\' + $n)
    Write-Output ($n + ' | Disabled=' + $v.Disabled + ' | Settings\Theme=' + $theme + ' | stale ModsWritable=' + $mw)
}
