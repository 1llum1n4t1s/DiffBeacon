"""Prepare external test candidate only; no build, app, compiler, or Git commands."""
import argparse,collections,hashlib,json,pathlib,shutil,tempfile,zipfile
from projection import make_plan
R=pathlib.Path(__file__).resolve().parent
ROOT=R.parents[2]
# 入力となる過去runの作業フォルダー。既定は各ツールの一時フォルダー（tempfile.gettempdir()）配下の
# Codex/DiffBeacon/<run名> とし、別の場所に置いた場合は --base / --native で明示する。
# 特定ドライブ・特定フォルダーの固定パスへは依存しない。
WORK=pathlib.Path(tempfile.gettempdir())/'Codex'/'DiffBeacon'
BASE=WORK/'folder-provider-null-20261023-candidate01'
NATIVE=WORK/'folder-side-functions-20261023-native01'
sha=lambda b:hashlib.sha256(b).hexdigest()
def write(p,data):p.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
def main():
    global BASE,NATIVE
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base',type=pathlib.Path,default=BASE,help='folder-provider-null候補runのroot')
    parser.add_argument('--native',type=pathlib.Path,default=NATIVE,help='native side-functions証拠runのroot')
    args=parser.parse_args()
    BASE,NATIVE=args.base,args.native
    # repo（ROOT）は従来どおりDドライブ上の正本に限定し、入力runはドライブを問わない。
    # いずれも絶対パスで、祖先にsymlink・junctionを含まないことを確認する。
    assert ROOT.drive.upper()=='D:'
    for path in (ROOT,BASE,NATIVE):
        assert path.is_absolute()
        for p in (path,*path.parents):assert not p.is_symlink() and not p.is_junction()
    base=BASE/'tests/DiffBeacon.E2E/FolderPropertyFilterScenarios.cs';basebytes=base.read_bytes()
    assert sha(basebytes)=='8e9ffe81b7c92327ea304806df1b6874e9870381d94b5249d5bb639c20ce3564'
    proof=(NATIVE/'native-proof.zip').read_bytes();assert sha(proof)=='ee9f13ee2e8de0684b376d97f8a1467a2591919f9ab1e0d2a49693c5c4e54a21'
    (R/'native-proof.zip').write_bytes(proof)
    with zipfile.ZipFile(R/'native-proof.zip') as z:
        cases=json.loads(z.read('cases.json'));golden={g['id']:g for g in map(json.loads,z.read('run1-golden.ndjson').splitlines())}
        (R/'GPL-2.0.txt').write_bytes(z.read('COPYING'))
    plan,classification,gaps=make_plan(cases,golden)
    write(R/'public-plan.json',plan);write(R/'native-classification.json',classification);write(R/'required-unimplemented.json',gaps)
    write(R/'failure-matrix.json',dict(fixedBeforeProductExecution=True,nativeCaseCount=len(cases),plannedProductCommands=len(plan),
        classification=dict(collections.Counter(c['scope'] for c in classification)),dirs=[2,3],prefixes=['','left','middle','right'],
        failures=['arity constructor rejects','At guards skip all runtime children','Column typed/range validation and per-side reevaluation','Range grammar runtime or literal preparation rejection','None versus empty string and side arrays','exact argument log count/order','all row and side paths/kind/status/filter','process exit/EOF/cleanup','input/filter/list SHA retention'],
        publicInternalPhaseObservation=False,constantStringCacheTrace='native-only; public literal does not expose child trace',
        unimplemented=gaps,unexercised=['budget/cancellation injection','provider-present text context','native parser equivalence','readonly write paths: folder CLI does not write outputs']))
    text=basebytes.decode('utf8');anchor='        await FolderProviderNullScenarios.RunAsync(fixtures, run, check, skip, python);'
    assert text.count(anchor)==1
    text=text.replace(anchor,anchor+'\n        await FolderSideFunctionScenarios.RunAsync(fixtures, run, check, skip, python);')
    (ROOT/'tests/DiffBeacon.E2E/FolderPropertyFilterScenarios.cs').write_text(text,encoding='utf8',newline='')
    helper=(BASE/'tests/DiffBeacon.E2E/FolderProviderNullScenarios.cs').read_text(encoding='utf8')
    helper=helper.replace('FolderProviderNullScenarios','FolderSideFunctionScenarios').replace('FolderProviderNull','FolderSideFunctions').replace('native-folder-null-proof.zip','native-proof.zip').replace('provider-null','side-functions')
    helper=helper.replace('pid = result.Pid, creationUtc = result.CreationUtc, launchEvidence = result.LaunchEvidence,',
        'pid = result.Pid, creationUtc = result.CreationUtc, launchUtc = result.LaunchUtc, exitObservedUtc = result.ExitObservedUtc, durationMilliseconds = result.DurationMilliseconds,\n                    platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : "other", launchEvidence = result.LaunchEvidence,')
    start=helper.index('        using var archive = ZipFile.OpenRead(');end=helper.index('        using var plan =',start)
    helper=helper[:start]+helper[end:]
    helper=helper.replace('foreach (var item in plan.RootElement.EnumerateArray())','foreach (var item in plan.RootElement.EnumerateArray().Where(item => item.GetProperty("count").GetInt32() == count))')
    start=helper.index('                    var logCount =');end=helper.index('                    if (result.ExitCode == expectedExit)',start)
    helper=helper[:start]+'''                    var tokens = item.GetProperty("expectedLogTokens").EnumerateArray().Select(token => token.GetString()!).ToArray();
                    var lines = result.Stderr.Split('\\n', StringSplitOptions.RemoveEmptyEntries);
                    check("folder side-functions runtime logs " + count + " " + id,
                        lines.Length == tokens.Length && lines.Zip(tokens).All(pair => pair.First.StartsWith("info: " + pair.Second + " ", StringComparison.Ordinal)), result.Stderr);
'''+helper[end:]
    helper=helper.replace('skip("folder side-functions lexer/cancellation injection", "No public CLI injection for lexer/depth budgets or cancellation; shared 65,536 evaluation budget is unchanged. Native full constructor and child Optimize remain source-only.");',
        'skip("folder side-functions unobserved boundaries", "No public internal-phase observation, native literal cache trace, lexer/depth/cancellation injection, or provider-present execution. Required child-Optimize fold gap is retained in required-unimplemented.json and is not counted as passed.");')
    (ROOT/'tests/DiffBeacon.E2E/FolderSideFunctionScenarios.cs').write_text(helper,encoding='utf8',newline='')
    (R/'.gitattributes').write_text('native-proof.zip -text\nGPL-2.0.txt -text\n',encoding='utf8')
    files=[p for p in R.iterdir() if p.is_file() and p.name not in ('fixed-manifest.json',)]
    write(R/'fixed-manifest.json',dict(files={p.name:sha(p.read_bytes()) for p in files},productExecuted=False))
    write(ROOT/'e2e-input-manifest.json',dict(coreSha256='0379f8c12cf1028de24f4d812c3721719a178baabceb83884f7d1c4e0ee45565',
        nativeProofSha256=sha(proof),baseMainSha256=sha(basebytes),plannedCommands=len(plan),productBuildExecuted=False,
        outputs={str(p.relative_to(ROOT)).replace('\\','/'):sha(p.read_bytes()) for p in (ROOT/'tests').rglob('*') if p.is_file()},
        installation=dict(main='Compile Remove existing FolderPropertyFilterScenarios.cs; Include external replacement explicitly',
            helper='Compile Include external FolderSideFunctionScenarios.cs explicitly (outside project glob)',
            fixtures='Content Include external tests/Fixtures/FolderSideFunctions/**/*; Link tests/Fixtures/FolderSideFunctions/%(RecursiveDir)%(Filename)%(Extension); CopyToOutputDirectory PreserveNewest',
            msbuildItemGroup='<ItemGroup Condition="\'$(MSBuildProjectName)\' == \'DiffBeacon.E2E\'"><Compile Remove="FolderPropertyFilterScenarios.cs" /><Compile Include="'+str(ROOT/'tests/DiffBeacon.E2E/FolderPropertyFilterScenarios.cs')+'" /><Compile Include="'+str(ROOT/'tests/DiffBeacon.E2E/FolderSideFunctionScenarios.cs')+'" /><Content Include="'+str(R/'**/*')+'" Link="tests/Fixtures/FolderSideFunctions/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>',
            baseDependencies='Retain existing FolderProviderNullScenarios + FolderProviderNull fixture and FolderSupplementaryCaseFold helper/fixture. Do not replace lists-only selector.',
            repository='Source oracle tests/Fixtures/LineTransformAggregates resolved from repo working directory; binary fixture resolved from AppContext.BaseDirectory first'),
        nativeOptimizationModes='native case false => explicit @noopt; true => explicit @opt. Default absent prefix occurs only in labeled source-derived controls.',
        processEvidence='Windows LaunchEvidence is null by design; actual Pid/birth/launch/exit/duration and returned exit/stdout/stderr verified. Program.Run uses WaitForExitAsync followed by both stream awaits. macOS retains mandatory Terminal/ActualPid/EOF/pipe/disposal/cleanup evidence, bound to actual sys.platform.',
        requiredUnimplemented=gaps,newPlannedCommands=['build/Dev.ps1 isolated Release build with explicit Compile replacements and fixture Content Links','isolated E2E --folder-properties-only --python <absolute python> --app <candidate app>','full E2E after required unimplemented Core optimization gap resolved'],
        allowedExecution='static fixture Python reader and syntax only; no product build/app/native compile in this worker'))
    print(json.dumps(dict(prepared=True,plannedCommands=len(plan),classifications=collections.Counter(c['scope'] for c in classification))))
if __name__=='__main__':main()
