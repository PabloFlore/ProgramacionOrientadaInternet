# Genera una CA local y el certificado HTTPS del servidor.
#
# Uso (desde server\ChatServer):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\generar_certificados.ps1 -Ip 192.168.101.105
#
# Genera en certs\ (carpeta ignorada por Git):
#   ca.key       clave privada de la CA. No la compartas.
#   ca.cer       certificado público de la CA. Este se instala en cada dispositivo.
#   servidor.pfx certificado del servidor que usa Kestrel (sin contraseña).
#
# Si la IP de la PC cambia, vuelve a ejecutar el script con la nueva IP y
# vuelve a instalar ca.cer solo si la CA cambió (con -RegenerarCA).

param(
    [Parameter(Mandatory = $true)]
    [string]$Ip,

    [string]$OpenSsl = "C:\Program Files\Git\usr\bin\openssl.exe",

    [switch]$RegenerarCA
)

$ErrorActionPreference = "Stop"

if ($Ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
    throw "La IP '$Ip' no tiene formato IPv4."
}

if (-not (Test-Path -LiteralPath $OpenSsl)) {
    throw "No se encontró openssl en '$OpenSsl'. Indica la ruta con -OpenSsl."
}

$certs = Join-Path $PSScriptRoot "..\certs"
New-Item -ItemType Directory -Path $certs -Force | Out-Null
$certs = (Resolve-Path $certs).Path
Push-Location $certs

try {
    function Ejecutar([string[]]$argumentos) {
        & $OpenSsl @argumentos
        if ($LASTEXITCODE -ne 0) { throw "openssl falló: $($argumentos -join ' ')" }
    }

    if ($RegenerarCA -or -not (Test-Path "ca.cer")) {
        Ejecutar @("req", "-x509", "-newkey", "rsa:2048", "-sha256", "-days", "3650", "-nodes",
            "-keyout", "ca.key", "-out", "ca.cer",
            "-subj", "/CN=Music Hub CA local",
            "-addext", "basicConstraints=critical,CA:TRUE",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign")
        Write-Host "CA nueva generada."
    } else {
        Write-Host "Se reutiliza la CA existente (usa -RegenerarCA para crear otra)."
    }

    # Extensiones del certificado del servidor: la IP y localhost deben
    # coincidir con la URL que escribe el navegador.
    Set-Content -LiteralPath "servidor.ext" -Encoding ASCII -Value @(
        "basicConstraints=CA:FALSE",
        "keyUsage=digitalSignature,keyEncipherment",
        "extendedKeyUsage=serverAuth",
        "subjectAltName=IP:$Ip,IP:127.0.0.1,DNS:localhost"
    )

    Ejecutar @("req", "-newkey", "rsa:2048", "-nodes", "-keyout", "servidor.key",
        "-out", "servidor.csr", "-subj", "/CN=$Ip")

    Ejecutar @("x509", "-req", "-in", "servidor.csr", "-CA", "ca.cer", "-CAkey", "ca.key",
        "-CAcreateserial", "-out", "servidor.cer", "-days", "825", "-sha256",
        "-extfile", "servidor.ext")

    # PFX sin contraseña y sin MAC: Kestrel lo carga sin pedir clave en claro.
    Ejecutar @("pkcs12", "-export", "-out", "servidor.pfx", "-inkey", "servidor.key",
        "-in", "servidor.cer", "-certfile", "ca.cer", "-passout", "pass:",
        "-keypbe", "NONE", "-certpbe", "NONE", "-nomac")

    Remove-Item "servidor.key", "servidor.csr", "servidor.ext", "ca.srl" -ErrorAction SilentlyContinue

    Copy-Item "ca.cer" "ca-music-hub.cer" -Force

    Write-Host ""
    Write-Host "Listo. Archivos en: $certs"
    Write-Host "  servidor.pfx  (Kestrel)"
    Write-Host "  ca-music-hub.cer  (instalar en cada dispositivo)"
    Write-Host "SAN del servidor: IP:$Ip, IP:127.0.0.1, DNS:localhost"
}
finally {
    Pop-Location
}
