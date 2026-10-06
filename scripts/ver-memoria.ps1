<#
.SYNOPSIS
    Mostra o que a memória hierárquica gravou na última sessão da AIB.

.DESCRIPTION
    Lê ~/.AIB/memory/sessions/<id>/ e resume: turnos crus, artefatos literais e capítulos.
    Somente leitura — nunca apaga nem reescreve nada.

.PARAMETER Sessao
    Id da sessão. Omitido, usa a mais recente.

.PARAMETER Raiz
    Raiz alternativa da memória. Serve para inspecionar a pasta de um ensaio de integração.

.PARAMETER Completo
    Imprime também o texto de cada turno, e não só o cabeçalho.

.EXAMPLE
    .\scripts\ver-memoria.ps1
    .\scripts\ver-memoria.ps1 -Completo
    .\scripts\ver-memoria.ps1 -Sessao 20260821-143005-812
#>
[CmdletBinding()]
param(
    [string]$Sessao,
    [string]$Raiz = (Join-Path $env:USERPROFILE '.AIB\memory'),
    [switch]$Completo
)

$ErrorActionPreference = 'Stop'

$pastaSessoes = Join-Path $Raiz 'sessions'
if (-not (Test-Path $pastaSessoes)) {
    Write-Host "Nenhuma memória em $pastaSessoes." -ForegroundColor Yellow
    Write-Host "Converse uma vez com a AIB e rode de novo." -ForegroundColor DarkGray
    return
}

if ($Sessao) {
    $pasta = Join-Path $pastaSessoes $Sessao
    if (-not (Test-Path $pasta)) { throw "Sessão '$Sessao' não existe em $pastaSessoes." }
} else {
    $pasta = Get-ChildItem $pastaSessoes -Directory | Sort-Object Name -Descending |
             Select-Object -First 1 -ExpandProperty FullName
    if (-not $pasta) { Write-Host "Nenhuma sessão gravada ainda." -ForegroundColor Yellow; return }
}

Write-Host ""
Write-Host "Sessão: $(Split-Path $pasta -Leaf)" -ForegroundColor Cyan
Write-Host "Pasta:  $pasta" -ForegroundColor DarkGray

# Todas as sessões, para dar contexto de quantas existem.
$total = (Get-ChildItem $pastaSessoes -Directory).Count
Write-Host "($total sessão(ões) no total)" -ForegroundColor DarkGray

function Read-Jsonl {
    param([string]$Caminho)
    if (-not (Test-Path $Caminho)) { return @() }
    # Linha truncada por queda no meio de uma escrita é pulada, e não derruba a leitura.
    Get-Content $Caminho -Encoding utf8 | Where-Object { $_.Trim() } | ForEach-Object {
        try { $_ | ConvertFrom-Json } catch { Write-Host "  [linha ilegível, pulada]" -ForegroundColor DarkYellow }
    }
}

# ── Turnos crus ────────────────────────────────────────────────────────────────
$raw = Join-Path $pasta 'raw.jsonl'
$turnos = @(Read-Jsonl $raw)

Write-Host ""
Write-Host "── raw.jsonl : $($turnos.Count) turno(s) ──" -ForegroundColor Green
if (Test-Path $raw) {
    $kb = [math]::Round((Get-Item $raw).Length / 1KB, 1)
    Write-Host "  $kb KB em disco (nunca apagado)" -ForegroundColor DarkGray
}

foreach ($t in $turnos) {
    $pergunta = ($t.Messages | Where-Object Role -eq 'user' | Select-Object -First 1).Text
    if ($null -eq $pergunta) { $pergunta = '' }
    $curta = $pergunta -replace '\s+', ' '
    if ($curta.Length -gt 70) { $curta = $curta.Substring(0, 70) + '…' }

    $ferramentas = @($t.Messages | Where-Object { $_.ToolCalls }).Count
    $marca = if ($ferramentas -gt 0) { " [$ferramentas chamada(s)]" } else { '' }

    Write-Host ("  turno {0,2}: {1}{2}" -f $t.Index, $curta, $marca)

    foreach ($a in $t.Artifacts) {
        $cor = if ($a.Failed) { 'Red' } else { 'DarkCyan' }
        $detalhe = if ($a.Detail) { " ($($a.Detail))" } else { '' }
        Write-Host "           → $($a.Kind): $($a.Value)$detalhe" -ForegroundColor $cor
    }

    if ($Completo) {
        foreach ($m in $t.Messages) {
            $texto = ($m.Text -replace '\s+', ' ')
            if ($texto.Length -gt 160) { $texto = $texto.Substring(0, 160) + '…' }
            if ($texto) { Write-Host "           $($m.Role): $texto" -ForegroundColor DarkGray }
        }
    }
}

# ── Capítulos ──────────────────────────────────────────────────────────────────
$capitulos = @(Read-Jsonl (Join-Path $pasta 'chapters.jsonl'))

Write-Host ""
Write-Host "── chapters.jsonl : $($capitulos.Count) capítulo(s) ──" -ForegroundColor Green

if ($capitulos.Count -eq 0) {
    Write-Host "  Nenhum ainda. A compactação só dispara quando a conversa viva passa de 85%" -ForegroundColor DarkGray
    Write-Host "  da cota — conversas curtas nunca chegam lá, e é o comportamento correto." -ForegroundColor DarkGray
}

foreach ($c in $capitulos) {
    Write-Host ""
    Write-Host "  Capítulo $($c.Index) — turnos $($c.FirstTurn) a $($c.LastTurn)" -ForegroundColor Yellow
    Write-Host "  $($c.Summary)"
    if ($c.Artifacts.Count -gt 0) {
        Write-Host "  Artefatos preservados literalmente:" -ForegroundColor DarkGray
        foreach ($a in $c.Artifacts) {
            $cor = if ($a.Failed) { 'Red' } else { 'DarkCyan' }
            Write-Host "    $($a.Kind): $($a.Value)" -ForegroundColor $cor
        }
    }
}

# ── Atos ───────────────────────────────────────────────────────────────────────
$atos = @(Read-Jsonl (Join-Path $pasta 'acts.jsonl'))

Write-Host ""
Write-Host "── acts.jsonl : $($atos.Count) ato(s) ──" -ForegroundColor Green

if ($atos.Count -eq 0) {
    Write-Host "  Nenhum ainda. Um ato nasce a cada 4 capítulos soltos." -ForegroundColor DarkGray
}

foreach ($t in $atos) {
    Write-Host ""
    Write-Host "  Ato $($t.Index) — capítulos $($t.FirstChapter) a $($t.LastChapter)" -ForegroundColor Magenta
    Write-Host "  $($t.Summary)"
    if ($t.Artifacts.Count -gt 0) {
        Write-Host "  Artefatos condensados:" -ForegroundColor DarkGray
        foreach ($a in $t.Artifacts) {
            $cor = if ($a.Failed) { 'Red' } else { 'DarkCyan' }
            Write-Host "    $($a.Kind): $($a.Value)" -ForegroundColor $cor
        }
    }
}

# ── Fatos duráveis ─────────────────────────────────────────────────────────────
# Ficam na RAIZ da memória, e não na sessão: é a única faixa que atravessa conversas.
$fatosPath = Join-Path $Raiz 'facts.md'

Write-Host ""
Write-Host "── facts.md (todas as sessões) ──" -ForegroundColor Green

if (-not (Test-Path $fatosPath)) {
    Write-Host "  Ainda não existe. Nasce na primeira promoção de ato." -ForegroundColor DarkGray
} else {
    $linhas = @(Get-Content $fatosPath -Encoding utf8 | Where-Object { $_ -like '- *' })
    Write-Host "  $($linhas.Count) fato(s) — $fatosPath" -ForegroundColor DarkGray
    Write-Host "  (é seu: pode editar, reordenar e apagar; o que apagar não volta)" -ForegroundColor DarkGray
    foreach ($l in $linhas) { Write-Host "  $l" -ForegroundColor DarkCyan }
}

Write-Host ""
