<# Fixed Native141 reproduction and the same portable public-plan generation path. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$ReferenceZip,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$VcVars
)
$ErrorActionPreference='Stop'
$pythonPath=(Get-Item -LiteralPath $Python -ErrorAction Stop).FullName
$outputPath=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $outputPath){throw '再生成は新規の外部作業出力へ指定してください。'}
# reproduce.py/capture.validate rejects repository/.codex/dotwork/artifacts/reparse outputs before creation.
& $pythonPath -B (Join-Path $PSScriptRoot 'reproduce.py') --repo $Repository --reference-zip $ReferenceZip --output $outputPath --compiler $Compiler --vcvars $VcVars
if($LASTEXITCODE-ne 0){throw 'Native二回再生成または固定stdout照合が失敗しました。原本は変更しません。'}
$planOutput=Join-Path $outputPath 'public-plan'
[IO.Directory]::CreateDirectory($planOutput)|Out-Null
& $pythonPath -B (Join-Path $PSScriptRoot 'generate-public-plan.py') --fixture $PSScriptRoot --output $planOutput
if($LASTEXITCODE-ne 0){throw 'Public plan生成が失敗しました。'}
foreach($name in @('public-case-plan.json','probes.json','public-plan-metadata.json')){
    if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name) -Algorithm SHA256).Hash-ne (Get-FileHash -LiteralPath (Join-Path $planOutput $name) -Algorithm SHA256).Hash){throw "固定public plan bytes不一致: $name"}
}
& $pythonPath -B (Join-Path $PSScriptRoot 'verify-public-plan.py') --fixture $PSScriptRoot --plan-dir $planOutput --receipt (Join-Path $outputPath 'public-plan-static-receipt.json')
if($LASTEXITCODE-ne 0){throw 'Public plan静的照合が失敗しました。'}
# This wrapper does not Compile public expressions or execute App diagnostics.
