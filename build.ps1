# Builds HallowRemote.exe with the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'HallowRemote.exe'
$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Core.dll", "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Xml.dll", "$fw\System.Xml.Linq.dll", "$fw\System.Security.dll", "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll", "$env:WINDIR\System32\WinMetadata\Windows.Media.winmd", "$env:WINDIR\System32\WinMetadata\Windows.Foundation.winmd"
$args = @('/nologo', '/target:winexe', '/optimize+', "/out:$out", "/win32icon:$src\hallow.ico", "/resource:$src\Theme.xaml,HallowRemote.Theme.xaml", "/resource:$src\web\index.html,HallowRemote.index.html", "/resource:$src\web\icon.png,HallowRemote.icon.png") +
    ($refs | ForEach-Object { "/reference:$_" }) + (Get-ChildItem $src -Filter *.cs | ForEach-Object FullName)
& "$fw\csc.exe" @args
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
