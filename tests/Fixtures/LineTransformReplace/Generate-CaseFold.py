import argparse,pathlib,zipfile,hashlib,re,json
parser=argparse.ArgumentParser();parser.add_argument('--reference-zip',default=str(pathlib.Path(__file__).resolve().parent/'reference-source.zip'));parser.add_argument('--output',required=True);args=parser.parse_args()
output=pathlib.Path(args.output).absolute()
repo=pathlib.Path(__file__).resolve().parents[3]
import os
for ancestor in [output,*output.parents]:
 assert ancestor.name.lower()!='artifacts' and not ancestor.is_symlink() and not ancestor.is_junction(), 'unsafe output ancestor'
assert not output.is_relative_to(repo) and not output.is_relative_to(pathlib.Path.home()/'.codex'), 'output must be external'
assert not output.exists(), 'refuse overwrite'
assert output.parent.is_dir(), 'create explicit external parent first'
with zipfile.ZipFile(args.reference_zip) as z:raw=z.read('original/Externals/poco/dependencies/pcre2/src/pcre2_ucd.c')
sha=hashlib.sha256(raw).hexdigest().upper();assert sha=='6EBDC9043DCB13832189A45818F767AFE9C4D6DF078FB37DB6D55D80126DCE92'
t=raw.decode();clean=re.sub(r'/\*.*?\*/','',t,flags=re.S)
def body(name):return re.findall(r'PRIV\('+name+r'\)\[\]\s*=\s*\{(.*?)\};',clean,re.S)[-1]
def nums(s):return [int(x,0) if x!='NOTACHAR' else 0xffffffff for x in re.findall(r'NOTACHAR|-?0x[0-9a-fA-F]+|-?\d+',s)]
r=[nums(x) for x in re.findall(r'\{([^{}]*)\}',body('ucd_records'))];a=nums(body('ucd_stage1'));b=nums(body('ucd_stage2'));sets=nums(body('ucd_caseless_sets'));mp={};relations={}
for cp in range(0x110000):
 rec=r[b[a[cp//128]*128+cp%128]];off=rec[3];eq={cp,cp+rec[4]}
 if off:
  while sets[off]!=0xffffffff:eq.add(sets[off]);off+=1
 if len(eq)>1:mp[cp]=min(eq);relations[cp]=eq
for cp,eq in relations.items():assert eq==relations.get(mp[cp],{mp[cp]})
assert mp.get(0x130,0x130)==0x130 and mp.get(0x131,0x131)==0x131
pairs=sorted((c,v) for c,v in mp.items() if c!=v);license=t[:t.index('#ifndef PCRE2_PCRE2TEST')]
lines=['// 固定PCRE2 10.47 Unicode 16.0.0 UCDから生成。再生成は tests/Fixtures/LineTransformReplace/Generate-CaseFold.py。','// Source SHA256: '+sha,license,'namespace DiffBeacon.Core;','internal static class LineTransformCaseFold','{','    private static ReadOnlySpan<int> Pairs => [']
for i in range(0,len(pairs),6):lines.append('        '+', '.join(f'0x{c:X}, 0x{v:X}' for c,v in pairs[i:i+6])+',')
lines+=['    ];','    internal static int Fold(int scalar, LineTransformBudget budget, CancellationToken token)','    {','        var pairs = Pairs; var low = 0; var high = pairs.Length / 2 - 1;','        while (low <= high)','        {','            budget.Charge(1, token);','            var middle = low + (high - low) / 2; var key = pairs[middle * 2];','            if (key == scalar) return pairs[middle * 2 + 1];','            if (key < scalar) low = middle + 1; else high = middle - 1;','        }','        return scalar;','    }','}']
assert len(mp)==2938 and len(pairs)==1484
out=output;assert not out.exists();out.write_text('\n'.join(lines),encoding='utf8');print(json.dumps(dict(path=str(out.resolve()),sha256=hashlib.sha256(out.read_bytes()).hexdigest().upper(),relationScalars=len(mp),pairs=len(pairs))))
