# Pruebas de grupos (API HTTP) contra el backend.
#
# Requisitos: haber compilado el backend (dotnet build en server/ChatServer).
# Qué hace: copia web/ y el binario a una carpeta temporal con su propia
# base de datos, arranca el servidor ahí, ejecuta las pruebas y lo detiene.
# No toca server/ChatServer/bin/Debug/net8.0/chat.db.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_grupos.ps1

$ErrorActionPreference = "Stop"

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_grupos"
$base = "https://localhost:5000"

# El servidor solo habla HTTPS: usa el certificado del proyecto y exige que
# la CA local esté instalada en el equipo (tools\generar_certificados.ps1).
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya está en uso. No se ejecuta la prueba para no tocar el servidor real."
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
$env:PIA_CARPETA_DATOS = "$raiz\datos"

$proc = Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden

function Llamar([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = "") {
    $cabeceras = @{}
    if ($token -ne "") { $cabeceras["Authorization"] = "Bearer $token" }

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
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Raw = $r.Content; Body = ($r.Content | ConvertFrom-Json) }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $lector = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $texto = $lector.ReadToEnd()
        $cuerpoRespuesta = $null
        try { $cuerpoRespuesta = $texto | ConvertFrom-Json } catch { }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Raw = $texto; Body = $cuerpoRespuesta }
    }
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{ Prueba = $nombre; Resultado = $(if ($ok) { "OK" } else { "FALLA" }) })
}

try {
    $listo = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { $listo = $true; break }
        } catch { }
    }
    if (-not $listo) { throw "El servidor de prueba no arrancó." }

    $pass = "Clave#2026x"

    # Cinco cuentas: G1 creador, G2 y G3 integrantes, G4 ajeno, G5 sin uso.
    $tokens = @{}
    $ids = @{}
    foreach ($n in 1..5) {
        $correo = "grupo$n@mail.com"
        Llamar "POST" "$base/api/registrar" @{ Nombre = "Grupo$n"; Apellido = "Prueba"; Usuario = "g$n"; Correo = $correo; Contrasena = $pass } | Out-Null
        $l = Llamar "POST" "$base/api/login" @{ Correo = $correo; Contrasena = $pass }
        $tokens[$n] = $l.Body.Token
        $ids[$n] = $l.Body.Id
    }

    # Validaciones de creación
    $sinSesion = Llamar "POST" "$base/api/grupos" @{ Nombre = "X"; Miembros = @($ids[2], $ids[3]) }
    Afirmar "crear grupo sin sesion responde 401" ($sinSesion.Status -eq 401)

    $sinNombre = Llamar "POST" "$base/api/grupos" @{ Nombre = "   "; Miembros = @($ids[2], $ids[3]) } $tokens[1]
    Afirmar "nombre vacio responde 400" ($sinNombre.Status -eq 400)

    $largo = Llamar "POST" "$base/api/grupos" @{ Nombre = ("x" * 61); Miembros = @($ids[2], $ids[3]) } $tokens[1]
    Afirmar "nombre de 61 caracteres responde 400" ($largo.Status -eq 400)

    $pocos = Llamar "POST" "$base/api/grupos" @{ Nombre = "Dos"; Miembros = @($ids[2]) } $tokens[1]
    Afirmar "2 integrantes contando al creador responde 400" ($pocos.Status -eq 400)

    $duplicados = Llamar "POST" "$base/api/grupos" @{ Nombre = "Dup"; Miembros = @($ids[2], $ids[2]) } $tokens[1]
    Afirmar "miembros repetidos no cuentan dos veces (400)" ($duplicados.Status -eq 400)

    $creadorRepetido = Llamar "POST" "$base/api/grupos" @{ Nombre = "Yo"; Miembros = @($ids[1], $ids[2]) } $tokens[1]
    Afirmar "el creador listado en Miembros no suma (2 total, 400)" ($creadorRepetido.Status -eq 400)

    $inexistente = Llamar "POST" "$base/api/grupos" @{ Nombre = "Fantasma"; Miembros = @($ids[2], 99999) } $tokens[1]
    Afirmar "integrante que no existe responde 400" ($inexistente.Status -eq 400)

    $demasiados = @(); foreach ($k in 1..60) { $demasiados += (1000 + $k) }
    $muchos = Llamar "POST" "$base/api/grupos" @{ Nombre = "Muchos"; Miembros = $demasiados } $tokens[1]
    Afirmar "mas de 50 integrantes responde 400" ($muchos.Status -eq 400)

    # Creación válida
    $ok = Llamar "POST" "$base/api/grupos" @{ Nombre = "Tercia"; Miembros = @($ids[2], $ids[3]) } $tokens[1]
    Afirmar "grupo de 3 (creador + 2) responde 201" ($ok.Status -eq 201)
    Afirmar "respuesta trae ChatId grupo:{id} e Integrantes = 3" ($ok.Body.ChatId -eq "grupo:$($ok.Body.Id)" -and $ok.Body.Integrantes -eq 3)
    $grupoId = $ok.Body.Id

    $conCreador = Llamar "POST" "$base/api/grupos" @{ Nombre = "Incluyeme"; Miembros = @($ids[1], $ids[2], $ids[3]) } $tokens[1]
    Afirmar "creador incluido en Miembros cuenta una vez (Integrantes = 3)" ($conCreador.Status -eq 201 -and $conCreador.Body.Integrantes -eq 3)

    # Lectura y membresía
    $listaG2 = Llamar "GET" "$base/api/grupos" $null $tokens[2]
    Afirmar "integrante ve el grupo en GET /api/grupos" (@($listaG2.Body.Grupos | Where-Object { $_.Id -eq $grupoId }).Count -eq 1)

    $listaG4 = Llamar "GET" "$base/api/grupos" $null $tokens[4]
    Afirmar "ajeno no ve el grupo en GET /api/grupos" (@($listaG4.Body.Grupos | Where-Object { $_.Id -eq $grupoId }).Count -eq 0)

    $listaG5 = Llamar "GET" "$base/api/grupos" $null $tokens[5]
    Afirmar "quien nunca fue integrante no ve el grupo en su lista" (@($listaG5.Body.Grupos | Where-Object { $_.Id -eq $grupoId }).Count -eq 0)

    $detalle = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[2]
    Afirmar "integrante obtiene el detalle (200, 3 integrantes)" ($detalle.Status -eq 200 -and @($detalle.Body.Integrantes).Count -eq 3)

    $detalleAjeno = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[4]
    Afirmar "ajeno no ve el detalle (403)" ($detalleAjeno.Status -eq 403)

    $noExiste = Llamar "GET" "$base/api/grupos/99999" $null $tokens[1]
    Afirmar "grupo inexistente responde 404" ($noExiste.Status -eq 404)

    $historialAjeno = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[4]
    Afirmar "ajeno no lee el historial del grupo (403)" ($historialAjeno.Status -eq 403)

    $nuncaMiembro = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[5]
    Afirmar "usuario que nunca fue integrante no lee el historial (403)" ($nuncaMiembro.Status -eq 403)

    $historialMiembro = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[3]
    Afirmar "integrante lee el historial del grupo (200)" ($historialMiembro.Status -eq 200)

    $noCanonico = Llamar "GET" "$base/api/mensajes?chatId=grupo:0$grupoId" $null $tokens[1]
    Afirmar "chatId de grupo no canonico (grupo:0N) responde 400" ($noCanonico.Status -eq 400)

    $cero = Llamar "GET" "$base/api/mensajes?chatId=grupo:0" $null $tokens[1]
    Afirmar "grupo:0 responde 400" ($cero.Status -eq 400)

    $inexistenteChat = Llamar "GET" "$base/api/mensajes?chatId=grupo:99999" $null $tokens[1]
    Afirmar "grupo que no existe responde 403 (no se filtra existencia)" ($inexistenteChat.Status -eq 403)

    # Regresiones: canales publicos y mensajes privados
    $publico = Llamar "GET" "$base/api/mensajes?chatId=amigos" $null $tokens[4]
    Afirmar "canal publico 'amigos' sigue abierto para cualquiera (200)" ($publico.Status -eq 200)

    $dmMalo = Llamar "GET" "$base/api/mensajes?chatId=DM:1:2" $null $tokens[4]
    Afirmar "dm malformado se rechaza (400)" ($dmMalo.Status -eq 400)

    $conversaciones = Llamar "GET" "$base/api/conversaciones" $null $tokens[2]
    Afirmar "los grupos no aparecen como conversaciones privadas" (-not ($conversaciones.Raw -match "grupo:"))

    # ---- Agregar integrantes (grupo $grupoId: creador 1, miembros 2 y 3) ----
    $agregaAjeno = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[5]) } $tokens[4]
    Afirmar "ajeno no puede agregar integrantes (403)" ($agregaAjeno.Status -eq 403)

    $agregaVacio = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @() } $tokens[1]
    Afirmar "agregar lista vacia responde 400" ($agregaVacio.Status -eq 400)

    $agregaRepetido = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[2]) } $tokens[1]
    Afirmar "agregar a un integrante que ya esta responde 400" ($agregaRepetido.Status -eq 400)

    $agregaFantasma = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @(99999) } $tokens[1]
    Afirmar "agregar usuario inexistente responde 400" ($agregaFantasma.Status -eq 400)

    $agregaGrupoNo = Llamar "POST" "$base/api/grupos/99999/miembros" @{ Miembros = @($ids[4]) } $tokens[1]
    Afirmar "agregar a grupo inexistente responde 404" ($agregaGrupoNo.Status -eq 404)

    $agregaPorIntegrante = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[4]) } $tokens[2]
    Afirmar "un integrante (no creador) agrega a otro (200, 4 integrantes)" ($agregaPorIntegrante.Status -eq 200 -and $agregaPorIntegrante.Body.Integrantes -eq 4)

    $agregadoVe = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[4]
    Afirmar "el agregado ya ve el detalle (200)" ($agregadoVe.Status -eq 200)

    $agregadoLee = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[4]
    Afirmar "el agregado ya lee el historial (200)" ($agregadoLee.Status -eq 200)

    # ---- Quitar integrantes ----
    $quitaSinCreador = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[3])" $null $tokens[2]
    Afirmar "quien no es creador no puede quitar a otros (403)" ($quitaSinCreador.Status -eq 403)

    $quitaNoMiembro = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[5])" $null $tokens[1]
    Afirmar "quitar a alguien que no es integrante responde 404" ($quitaNoMiembro.Status -eq 404)

    $quitaAjeno = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[4])" $null $tokens[5]
    Afirmar "ajeno no puede quitar a nadie (403)" ($quitaAjeno.Status -eq 403)

    $quita = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[4])" $null $tokens[1]
    Afirmar "el creador quita a un integrante (200, queda 3)" ($quita.Status -eq 200 -and $quita.Body.Integrantes -eq 3)

    $quitadoLee = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[4]
    Afirmar "quitado por el creador conserva la lectura del historial (200)" ($quitadoLee.Status -eq 200)

    $quitadoDetalle = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[4]
    Afirmar "quitado no ve la lista de integrantes (403)" ($quitadoDetalle.Status -eq 403)

    $quitaBajoMinimo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[3])" $null $tokens[1]
    Afirmar "no se puede bajar de 3 integrantes al quitar (400)" ($quitaBajoMinimo.Status -eq 400)

    $saleBajoMinimo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[2])" $null $tokens[2]
    Afirmar "tampoco se puede salir si quedarian menos de 3 (400)" ($saleBajoMinimo.Status -eq 400)

    $agregaCinco = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[5]) } $tokens[1]
    Afirmar "agregar de nuevo al 5 (200, 4 integrantes)" ($agregaCinco.Status -eq 200 -and $agregaCinco.Body.Integrantes -eq 4)

    $sale2 = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[2])" $null $tokens[2]
    Afirmar "un integrante se sale por si mismo (200, queda 3, Salio=true)" ($sale2.Status -eq 200 -and $sale2.Body.Integrantes -eq 3 -and $sale2.Body.Salio -eq $true)

    $saleNoPuedeLeer = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[2]
    Afirmar "quien salio ya no ve el grupo (403)" ($saleNoPuedeLeer.Status -eq 403)

    $saleSiPuedeHistorial = Llamar "GET" "$base/api/mensajes?chatId=grupo:$grupoId" $null $tokens[2]
    Afirmar "quien salio conserva la lectura del historial (200)" ($saleSiPuedeHistorial.Status -eq 200)

    $saleDeNuevo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[2])" $null $tokens[2]
    Afirmar "quien ya salio no puede volver a salir (403)" ($saleDeNuevo.Status -eq 403)

    $listaSale = Llamar "GET" "$base/api/grupos" $null $tokens[2]
    $enLista = @($listaSale.Body.Grupos | Where-Object { $_.Id -eq $grupoId })
    Afirmar "el grupo sigue en su lista como solo lectura (Activo=false)" ($enLista.Count -eq 1 -and $enLista[0].Activo -eq $false)

    $listaMiembro = Llamar "GET" "$base/api/grupos" $null $tokens[3]
    $activoMiembro = @($listaMiembro.Body.Grupos | Where-Object { $_.Id -eq $grupoId })
    Afirmar "un integrante actual ve el grupo como activo (Activo=true)" ($activoMiembro.Count -eq 1 -and $activoMiembro[0].Activo -eq $true)


    # ---- Traspaso de creador al salir el creador ----
    # Miembros ahora: 1 (creador), 3, 5. Con 3 no puede salir el creador (quedarian 2).
    $saleCreadorBajo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[1])" $null $tokens[1]
    Afirmar "el creador no puede salir si quedarian menos de 3 (400)" ($saleCreadorBajo.Status -eq 400)

    $agregaCuatro = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[4]) } $tokens[1]
    Afirmar "el creador agrega a 4 (200, 4 integrantes)" ($agregaCuatro.Status -eq 200 -and $agregaCuatro.Body.Integrantes -eq 4)

    # Miembros: 1 (creador), 3, 4, 5. Sale el creador (1): pasa a 3 (menor id).
    $saleCreador = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[1])" $null $tokens[1]
    Afirmar "el creador se sale (200)" ($saleCreador.Status -eq 200 -and $saleCreador.Body.Salio -eq $true)

    $tras = Llamar "GET" "$base/api/grupos/$grupoId" $null $tokens[3]
    Afirmar "al salir el creador, el rol pasa al integrante de menor id" ($tras.Body.CreadorId -eq $ids[3])

    $antiguoNoQuita = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[4])" $null $tokens[1]
    Afirmar "el creador anterior ya no puede quitar (403)" ($antiguoNoQuita.Status -eq 403)

    $nuevoQuitaBajoMinimo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[5])" $null $tokens[3]
    Afirmar "el nuevo creador no puede bajar de 3 (400)" ($nuevoQuitaBajoMinimo.Status -eq 400)

    $agregaParaQuitar = Llamar "POST" "$base/api/grupos/$grupoId/miembros" @{ Miembros = @($ids[2]) } $tokens[3]
    Afirmar "el nuevo creador agrega a 2 (200, 4 integrantes)" ($agregaParaQuitar.Status -eq 200 -and $agregaParaQuitar.Body.Integrantes -eq 4)

    $quitaConNuevo = Llamar "DELETE" "$base/api/grupos/$grupoId/miembros/$($ids[5])" $null $tokens[3]
    Afirmar "el nuevo creador quita a 5 (200, queda 3)" ($quitaConNuevo.Status -eq 200 -and $quitaConNuevo.Body.Integrantes -eq 3)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
