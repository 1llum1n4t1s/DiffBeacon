"""Independent bytes/source/process reader: imports no producer; computes no numeric golden."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,json,hashlib,zipfile,re
BASE=pathlib.Path(__file__).resolve().parent
REFSHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
def sha(b):return hashlib.sha256(b).hexdigest()
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--actual1',type=pathlib.Path);ap.add_argument('--actual2',type=pathlib.Path);a=ap.parse_args()
 fixed=json.loads((BASE/'fixed-manifest.json').read_bytes())
 actualFiles={p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()}
 assert actualFiles=={r['path'] for r in fixed}|{'fixed-manifest.json'}
 for f in fixed:
  b=(BASE/f['path']).read_bytes();assert len(b)==f['size'] and sha(b)==f['sha256'],f['path']
 ref=a.reference_zip.read_bytes();assert sha(ref)==REFSHA
 source=json.loads((BASE/'source-manifest.json').read_bytes());assert source['referenceArchiveSha256']==REFSHA and source['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 with zipfile.ZipFile(a.reference_zip) as z:
  assert z.testzip() is None
  originals={}
  for s in source['sources']:
   b=z.read('original/'+s['path']);assert len(b)==s['size'] and sha(b)==s['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['gitBlob'];originals[s['path']]=b
  assert (BASE/'LICENSE-GPL-2.0.md').read_bytes()==originals['LICENSE.md']
  for e in json.loads((BASE/'extractions.json').read_bytes()):
   b=originals[e['path']];span=b[e['startByte']:e['endByte']];assert span.startswith(e['marker'].encode()) and sha(span)==e['sha256'] and span==(BASE/e['file']).read_bytes()
   assert b[:e['startByte']].count(b'\n')+1==e['startLine'] and b[:e['endByte']].count(b'\n')+1==e['endLine']
 cases=json.loads((BASE/'cases.json').read_bytes());assert len(cases)==60 and len({c['id'] for c in cases})==60
 # Independent scanf-domain safety validation. No packed-number/expected-value calculation.
 for c in cases:
  text=bytes.fromhex(c['inputHex']).split(b'\0')[0].decode('ascii');cursor=0;parts=[0,0,0,0]
  for i in range(4):
   m=re.match(r'[ \t\r\n\v\f]*([+-]?[0-9]+)',text[cursor:])
   if not m:break
   parts[i]=int(m[1]);assert -2147483648<=parts[i]<=2147483647
   cursor+=m.end()
   if i<3:
    if text[cursor:cursor+1]!='.':break
    cursor+=1
  assert 0<=parts[0]<=32767 and parts[1]>=0 and parts[2]>=0
  # Out-of-16bit positive overlaps use small other terms; positive-max is nonoverlap.
  if parts[1]>65535:assert parts==[0,2147483647,0,0] or parts==[0,65536,0,0] or parts==[1,65536,0,0]
  if parts[2]>65535:assert parts[0]==0 and parts[1] in (0,1) and parts[3]==0
  if parts[3]>65535:assert parts[0]==0 and parts[1]==0 and parts[2] in (0,1)
  if parts[0]==32767:assert parts[1]<=65535 and parts[2]<=65535 and parts[3]<=65535
 proof=(BASE/'native-proof.zip').read_bytes();receipts=[]
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  assert z.testzip() is None
  pm=json.loads((BASE/'native-proof-manifest.json').read_bytes());assert set(z.namelist())=={x['path'] for x in pm}
  for f in pm:
   b=z.read(f['path']);assert len(b)==f['size'] and sha(b)==f['sha256'] and z.getinfo(f['path']).CRC==f['crc32']
  raw=z.read('run1/native.stdout.raw');assert raw==z.read('run2/native.stdout.raw')==(BASE/'golden.ndjson').read_bytes()
  for run in ['run1','run2']:
   for name in ['expr-node.inc','version-node.inc','version-ctor.inc','adapter.cpp','cases.inc']:assert z.read(run+'/'+name)==(BASE/name).read_bytes()
   for phase in ['compiler-env','compile','native']:
    r=json.loads(z.read(run+'/'+phase+'-receipt.json'));assert r['pid']>0 and r['birth100ns']>0 and r['heldOsHandle']>0 and r['safeHandleType']=='Handle' and r['heldSignaled'] and r['exitCode']==0 and r['osExitCode']==0 and not r['timeout'] and r['stdoutEof'] and r['stderrEof']
    for stream in ['stdout','stderr']:assert sha(z.read(run+'/'+phase+'.'+stream+'.raw'))==r[stream+'Sha256']
    if phase=='compile':assert pathlib.PureWindowsPath(r['command'][0]).name.lower()=='cl.exe' and len(r['command'])==2 and r['command'][1].startswith('@')
    if phase=='native':assert sha(z.read(run+'/reference.exe'))==r['executableSha256'];receipts.append(r)
 assert receipts[0]['pid']!=receipts[1]['pid'] and receipts[0]['birth100ns']!=receipts[1]['birth100ns']
 rows=[json.loads(l) for l in raw.splitlines()];assert len(rows)==60
 for c,r in zip(cases,rows):assert r['id']==c['id'] and r['inputHex']==c['inputHex'] and r['variantIndex']==3 and re.fullmatch(r'-?[0-9]+',r['value']) and -(1<<63)<=int(r['value'])<(1<<63)
 plan=json.loads((BASE/'public-case-plan.json').read_bytes());index={r['id']:r for r in rows}
 for p in plan:assert p['status']=='planned/unexecuted' and p['nativeValue']==index[p['nativeId']]['value'] and p['expression']=='v'+json.dumps(bytes.fromhex(index[p['nativeId']]['inputHex']).decode('ascii'))
 assert (a.actual1 is None)==(a.actual2 is None)
 if a.actual1:
  for fresh in [a.actual1,a.actual2]:assert (fresh/'native.stdout.raw').read_bytes()==raw
 print(json.dumps({'source':'fixed original zip CRC/hash/blob and exact spans verified','cases':60,'twoDistinctHeldNativeProcesses':True,'rawStdoutSha256':sha(raw),'publicBridges':len(plan),'publicStatus':'planned/unexecuted','numericGoldenOrigin':'two real native stdout observations only','freshStdoutCompared':bool(a.actual1)}))
if __name__=='__main__':main()
