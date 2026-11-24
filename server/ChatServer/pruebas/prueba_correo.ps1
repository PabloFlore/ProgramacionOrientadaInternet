# Pruebas del envio de correo electronico contra un SMTP real de pruebas.
#
# Requisitos:
#   - Haber compilado el backend (dotnet build en server/ChatServer).
#   - Tener disponibles los puertos 5000 (ChatServer), 2525 (SMTP) y 3000
#     (API web de smtp4dev).
#
# Que hace:
#   - Restaura la herramienta local smtp4dev (dotnet-tools.json).
#   - Arranca smtp4dev y el servidor en una carpeta temporal con su propia
#     base de datos.
#   - Comprueba que el correo llega realmente al buzon de smtp4dev.
#   - Prueba validaciones, limite de frecuencia, SMTP caido y falta de
#     configuracion.
#
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_correo.ps1

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_correo"
$base = "https://localhost:5000"
$smtpBase = "http://localhost:3000"
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"
$manifiesto = Join-Path $raizProyecto "dotnet-tools.json"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Generalo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (-not (Test-Path -LiteralPath $manifiesto)) {
    throw "No existe dotnet-tools.json. Restaura smtp4dev con: dotnet tool restore --tool-manifest dotnet-tools.json (desde la raiz)"
}

foreach ($puerto in @(5000, 2525, 3000)) {
    if (Get-NetTCPConnection -LocalPort $puerto -State Listen -ErrorAction SilentlyContinue) {
        throw "El puerto $puerto ya esta en uso. Deten el proceso y vuelve a intentar."
    }
}

Write-Host "Restaurando smtp4dev..."
Push-Location $raizProyecto
try {
    & dotnet tool restore --tool-manifest $manifiesto | Out-Null
}
finally {
    Pop-Location
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
New-Item -ItemType Directory -Path "$raiz\smtp4dev" -Force | Out-Null

Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
Remove-Item "$raiz\datos" -Recurse -Force -ErrorAction SilentlyContinue

$env:PIA_CARPETA_DATOS = "$raiz\datos"
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path
$env:Correo__Host = "127.0.0.1"
$env:Correo__Port = "2525"
$env:Correo__EnableSsl = "false"
$env:Correo__Usuario = ""
$env:Correo__Password = ""
$env:Correo__Remitente = "no-reply@musichub.local"
$env:Correo__RemitenteNombre = "Music Hub"

$script:servidor = $null
$script:smtp4dev = $null

function Iniciar-Smtp4dev {
    $smtpDir = "$raiz\smtp4dev"
    $argumentos = @(
        "tool", "run", "smtp4dev", "--",
        "--smtpport=2525",
        "--urls=http://localhost:3000",
        "--tlsmode=None",
        "--nousersettings",
        "--imapport=1143",
        "--pop3port=1110",
        "--messagestokeep=50",
        "--baseappdatapath=$smtpDir"
    )

    $script:smtp4dev = Start-Process dotnet -ArgumentList $argumentos `
        -WorkingDirectory $raizProyecto -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$smtpDir\out.log" `
        -RedirectStandardError "$smtpDir\err.log"
}

function Detener-Smtp4dev {
    if ($script:smtp4dev -and -not $script:smtp4dev.HasExited) {
        Stop-Process -Id $script:smtp4dev.Id -Force -ErrorAction SilentlyContinue
    }

    foreach ($proceso in @(Get-Process Rnwood.Smtp4dev -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $proceso.Id -Force -ErrorAction SilentlyContinue
    }
}

function Iniciar-Servidor {
    $script:servidor = Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput "$raiz\server.out.log" `
        -RedirectStandardError "$raiz\server.err.log"
}

function Detener-Servidor {
    if ($script:servidor -and -not $script:servidor.HasExited) {
        Stop-Process -Id $script:servidor.Id -Force -ErrorAction SilentlyContinue
    }

    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 200
    }
}

function Llamar([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = "") {
    $cabeceras = @{}
    if ($token -ne "") {
        $cabeceras["Authorization"] = "Bearer $token"
    }

    $params = @{
        Method = $metodo
        Uri = $url
        Headers = $cabeceras
        UseBasicParsing = $true
        ContentType = "application/json; charset=utf-8"
    }

    if ($null -ne $cuerpo) {
        $json = $cuerpo | ConvertTo-Json -Depth 5 -Compress
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
    }

    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{
            Status = [int]$r.StatusCode
            Raw = $r.Content
            Body = ($r.Content | ConvertFrom-Json)
        }
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) {
            throw
        }

        $lector = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $texto = $lector.ReadToEnd()
        $cuerpoRespuesta = $null

        try {
            $cuerpoRespuesta = $texto | ConvertFrom-Json
        }
        catch {}

        return [pscustomobject]@{
            Status = [int]$resp.StatusCode
            Raw = $texto
            Body = $cuerpoRespuesta
        }
    }
}

function SmtpGet([string]$ruta) {
    return Invoke-RestMethod -Uri "$smtpBase$ruta" -Method Get
}

function Esperar-Smtp {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = SmtpGet "/api/Messages?pageSize=1&page=1"
            if ($null -ne $x) {
                return
            }
        }
        catch {}
    }

    throw "smtp4dev no arranco. Revisa $raiz\smtp4dev\out.log"
}

function Esperar-Servidor {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) {
                return
            }
        }
        catch {}
    }

    throw "El servidor de prueba no arranco. Revisa $raiz\server.err.log"
}

function Enviar([string]$token, [int]$destinatarioId, [string]$asunto, [string]$mensaje) {
    return Llamar "POST" "$base/api/correo/enviar" @{
        DestinatarioId = $destinatarioId
        Asunto = $asunto
        Mensaje = $mensaje
    } $token
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{
        Prueba = $nombre
        Resultado = $(if ($ok) { "OK" } else { "FALLA" })
    })
}

try {
    Iniciar-Smtp4dev
    Esperar-Smtp

    Iniciar-Servidor
    Esperar-Servidor

    $pass = "Clave#2026x"

    $a = Llamar "POST" "$base/api/registrar" @{
        Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"
        Correo = "ana.correo@mail.com"; Contrasena = $pass
    }
    $b = Llamar "POST" "$base/api/registrar" @{
        Nombre = "Beto"; Apellido = "Dos"; Usuario = "beto"
        Correo = "beto.correo@mail.com"; Contrasena = $pass
    }
    $c = Llamar "POST" "$base/api/registrar" @{
        Nombre = "Caro"; Apellido = "Tres"; Usuario = "caro"
        Correo = "caro.correo@mail.com"; Contrasena = $pass
    }

    Afirmar "se registran las 3 cuentas" (
        $a.Status -eq 201 -and $b.Status -eq 201 -and $c.Status -eq 201
    )

    $la = Llamar "POST" "$base/api/login" @{
        Correo = "ana.correo@mail.com"; Contrasena = $pass
    }
    $lb = Llamar "POST" "$base/api/login" @{
        Correo = "beto.correo@mail.com"; Contrasena = $pass
    }
    $lc = Llamar "POST" "$base/api/login" @{
        Correo = "caro.correo@mail.com"; Contrasena = $pass
    }

    $ta = $la.Body.Token
    $tb = $lb.Body.Token
    $tc = $lc.Body.Token
    $idA = [int]$la.Body.Id
    $idB = [int]$lb.Body.Id
    $idC = [int]$lc.Body.Id

    Afirmar "las 3 cuentas inician sesion" (
        $la.Status -eq 200 -and $lb.Status -eq 200 -and $lc.Status -eq 200
    )

    $sinSesion = Enviar "" $idB "Prueba" "Cuerpo"
    Afirmar "sin sesion responde 401" ($sinSesion.Status -eq 401)

    $inexistente = Llamar "POST" "$base/api/correo/enviar" @{
        DestinatarioCorreo = "nadie@mail.com"
        Asunto = "Prueba"
        Mensaje = "Cuerpo"
    } $ta
    Afirmar "destinatario no registrado responde 404" ($inexistente.Status -eq 404)

    $auto = Enviar $ta $idA "Prueba" "Cuerpo"
    Afirmar "autoenvio responde 400" ($auto.Status -eq 400)

    $vacioAsunto = Enviar $ta $idB "   " "Cuerpo"
    Afirmar "asunto vacio responde 400" ($vacioAsunto.Status -eq 400)

    $vacioMensaje = Enviar $ta $idB "Prueba" "   "
    Afirmar "mensaje vacio responde 400" ($vacioMensaje.Status -eq 400)

    $largoAsunto = Enviar $ta $idB ("x" * 201) "Cuerpo"
    Afirmar "asunto de mas de 200 caracteres responde 400" ($largoAsunto.Status -eq 400)

    $largoMensaje = Enviar $ta $idB "Prueba" ("y" * 5001)
    Afirmar "mensaje de mas de 5000 caracteres responde 400" ($largoMensaje.Status -eq 400)

    $conSalto = Enviar $ta $idB "Hola`nMundo" "Cuerpo"
    Afirmar "asunto con salto de linea responde 400" ($conSalto.Status -eq 400)

    $envio = Enviar $ta $idB "Prueba inicial" "Cuerpo de prueba"
    Afirmar "envio real responde 200" ($envio.Status -eq 200)
    Afirmar "la respuesta confirma el envio" ($envio.Body.Enviado -eq $true)
    Afirmar "la respuesta identifica al destinatario" (
        [int]$envio.Body.DestinatarioId -eq $idB
    )

    Start-Sleep -Milliseconds 500
    $mensajes = SmtpGet "/api/Messages?pageSize=50&page=1"
    $recibido = $mensajes.results |
        Where-Object { $_.subject -eq "Prueba inicial" } |
        Select-Object -First 1

    Afirmar "smtp4dev recibio el correo" ($null -ne $recibido)

    if ($null -ne $recibido) {
        Afirmar "el destinatario es el correo registrado de B" (
            @($recibido.to) -contains "beto.correo@mail.com"
        )
        Afirmar "el remitente es el configurado" (
            $recibido.from -eq "no-reply@musichub.local"
        )
        Afirmar "el asunto llego intacto" ($recibido.subject -eq "Prueba inicial")

        $cuerpo = Invoke-WebRequest `
            -Uri "$smtpBase/api/Messages/$($recibido.id)/plaintext" `
            -UseBasicParsing
        Afirmar "el cuerpo llego intacto" (
            $cuerpo.Content.Trim() -eq "Cuerpo de prueba"
        )
    }

    $recibidosA = @($mensajes.results | Where-Object {
        @($_.to) -contains "ana.correo@mail.com" -and $_.subject -eq "Prueba inicial"
    })
    Afirmar "el correo no se envio al remitente" ($recibidosA.Count -eq 0)

    for ($i = 1; $i -le 4; $i++) {
        $relleno = Enviar $ta $idB "Relleno $i" "Cuerpo $i"
        Afirmar "envio adicional $i responde 200" ($relleno.Status -eq 200)
    }

    $limite = Enviar $ta $idB "Sexto correo" "Cuerpo"
    Afirmar "el sexto envio responde 429" ($limite.Status -eq 429)
    Afirmar "el limite explica el motivo" (
        $null -ne $limite.Body.Error
    )

    # --- SMTP caido: B todavia no alcanzo el limite ---
    Detener-Smtp4dev
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-NetTCPConnection -LocalPort 2525 -State Listen -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 200
    }

    $caido = Enviar $tb $idA "SMTP caido" "Cuerpo"
    Afirmar "si SMTP esta caido responde 502" ($caido.Status -eq 502)
    Afirmar "el error SMTP no expone detalles internos" (
        $caido.Body.Error -match "correo" -and
        $caido.Body.Error -notmatch "SMTP|Exception|smtp4dev"
    )

    # --- Falta de configuracion: reiniciar con Host vacio ---
    Detener-Servidor
    $env:Correo__Host = " "
    Iniciar-Servidor
    Esperar-Servidor

    $sinConfig = Enviar $ta $idB "Sin configuracion" "Cuerpo"
    Afirmar "sin configuracion SMTP responde 503" ($sinConfig.Status -eq 503)
    Afirmar "el 503 explica que falta configuracion" (
        $sinConfig.Body.Error -match "correo" -and
        $sinConfig.Body.Error -notmatch "Exception|smtp4dev"
    )
}
finally {
    Detener-Servidor
    Detener-Smtp4dev
}

$resultados | Format-Table -AutoSize

if ($resultados | Where-Object { $_.Resultado -ne "OK" }) {
    throw "Hay pruebas de correo fallidas."
}

Write-Host "Todas las pruebas de correo pasaron: $($resultados.Count)."
