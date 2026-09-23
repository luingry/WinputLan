[CmdletBinding()]
param(
    [string]$PrivateKeyPath = "$env:LOCALAPPDATA\WinputLan\release\ota-private.pem",
    [string]$PublicKeyPath = "$env:LOCALAPPDATA\WinputLan\release\ota-public.xml"
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required to create the OTA RSA key.' }
if ((Test-Path -LiteralPath $PrivateKeyPath) -or (Test-Path -LiteralPath $PublicKeyPath)) { throw 'Refusing to overwrite an existing OTA key pair.' }

$directory = Split-Path -Parent $PrivateKeyPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    [System.IO.File]::WriteAllText($PrivateKeyPath, $rsa.ExportRSAPrivateKeyPem(), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($PublicKeyPath, $rsa.ToXmlString($false), [System.Text.UTF8Encoding]::new($false))
    $acl = Get-Acl -LiteralPath $PrivateKeyPath
    $acl.SetAccessRuleProtection($true, $false)
    $current = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new($current, 'FullControl', 'Allow')
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $PrivateKeyPath -AclObject $acl
    Write-Host "OTA signing key created. Keep the private PEM outside the repository and store its content only in GitHub secret OTA_SIGNING_PRIVATE_KEY_PEM." -ForegroundColor Green
    Write-Host "Public key file: $PublicKeyPath" -ForegroundColor Green
}
finally { $rsa.Dispose() }
