"""Independent C++17 edge reader; no producer import or numerical golden packing."""
import sys
sys.dont_write_bytecode=True
import pathlib,argparse,json,hashlib,zipfile,re
BASE=pathlib.Path(__file__).resolve().parent
def sha(b):return hashlib.sha256(b).hexdigest()
def receipt(get,phase):
 r=json.loads(get(phase+'-receipt.json'));assert r['pid']>0 and r['birth100ns']>0 and r['heldOsHandle']>0 and r['safeHandleType']=='Handle' and r['heldSignaled'] and r['exitCode']==r['osExitCode']==0 and r['stdoutEof'] and r['stderrEof'] and not r['timeout']
 for stream in ['stdout','stderr']:assert sha(get(phase+'.'+stream+'.raw'))==r[stream+'Sha256']
 if phase=='compile':assert pathlib.PureWindowsPath(r['command'][0]).name.lower()=='cl.exe' and len(r['command'])==2 and r['command'][1].startswith('@');assert b'/std:c++17' in get('compile.rsp') and b'/std:c++20' not in get('compile.rsp')
 if phase=='native':assert r['executableSha256']==sha(get('reference.exe')) and get('native.stderr.raw')==b''
 return r
def frozen(directory,manifestSha=None):
 m=(directory/'fixed-manifest.json').read_bytes()
 if manifestSha:assert sha(m)==manifestSha
 files=json.loads(m);assert {p.relative_to(directory).as_posix() for p in directory.rglob('*') if p.is_file()}=={f['path'] for f in files}|{'fixed-manifest.json'}
 for f in files:
  b=(directory/f['path']).read_bytes();assert len(b)==f['size'] and sha(b)==f['sha256'],str(directory/f['path'])
def main():
 p=argparse.ArgumentParser();p.add_argument('--reference-zip',type=pathlib.Path,required=True);p.add_argument('--raw60-dir',type=pathlib.Path,required=True);p.add_argument('--supplement128-dir',type=pathlib.Path,required=True);p.add_argument('--current-project',type=pathlib.Path);p.add_argument('--actual1',type=pathlib.Path);p.add_argument('--actual2',type=pathlib.Path);a=p.parse_args();frozen(BASE)
 parents=json.loads((BASE/'immutable-parent-provenance.json').read_bytes());directories={'raw60':a.raw60_dir,'supplement128':a.supplement128_dir}
 for parent in parents:
  directory=directories[parent['label']];frozen(directory,parent['fixedManifestSha256']);assert sha((directory/'native-proof.zip').read_bytes())==parent['proofZipSha256']
 assert sha(a.reference_zip.read_bytes())=='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
 originals={};sm=json.loads((BASE/'source-manifest.json').read_bytes())
 assert sm['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6'
 with zipfile.ZipFile(a.reference_zip) as z:
  assert z.testzip() is None and not any(n.endswith('Src/Merge.vcxproj') for n in z.namelist())
  for s in sm['sources']:
   b=z.read('original/'+s['path']) if s['origin']=='fixed-reference-zip' else (BASE/'additional-original'/s['path']).read_bytes();assert len(b)==s['size'] and sha(b)==s['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['gitBlob'];originals[s['path']]=b
   if s['path'].endswith('FilterLexer.re'):assert s['gitBlob']=='6163d1a1f5433de3c81eb214530c5f825f694290'
   if s['path'].endswith('FilterLexer.h'):assert s['gitBlob']=='6891df502b9d7d8baa5a965ca52625ba7837e6cf'
 assert (BASE/'LICENSE-GPL-2.0.md').read_bytes()==originals['LICENSE.md']
 for e in json.loads((BASE/'extractions.json').read_bytes()):
  b=originals[e['path']];span=b[e['startByte']:e['endByte']];assert span.startswith(e['marker'].encode()) and sha(span)==e['sha256'] and span==(BASE/e['file']).read_bytes();assert b[:e['startByte']].count(b'\n')+1==e['startLine'] and b[:e['endByte']].count(b'\n')+1==e['endLine']
 evidence=json.loads((BASE/'SourceCppStandardEvidence.json').read_bytes());project=(BASE/evidence['retainedBytes']).read_bytes();assert sha(project)==evidence['sourceCurrentReadonlySha256']=='5dd1ec8efee4d6a4a1ac093093e6ae15af3803c1b386c81d35fe0feb61819186'
 projectLines=project.decode('utf-8-sig').splitlines();assert len(evidence['languageStandards'])==12
 for line in evidence['languageStandards']:assert projectLines[line['line']-1].strip()==line['text']=='<LanguageStandard>stdcpp17</LanguageStandard>'
 assert evidence['fixedReferenceZipContainsProject']==False and evidence['fixedReferenceZipProjectStandardQualified']==False and evidence['compileSwitch']=='/std:c++17'
 assert [x['url'] for x in evidence['primarySourceCitations']]==['https://timsong-cpp.github.io/cppwp/n4659/expr.shift#2','https://timsong-cpp.github.io/cppwp/n4659/conv.integral#3']
 if a.current_project:assert a.current_project.read_bytes()==project
 cases=json.loads((BASE/'cases.json').read_bytes());assert len(cases)==18 and len({c['id'] for c in cases})==18
 # Independent scanf-domain safety check only, never constructs expected packed values.
 for c in cases:
  text=bytes.fromhex(c['inputHex']).decode('ascii');cursor=0;parts=[0,0,0,0]
  for i in range(4):
   m=re.match(r'[ \t\r\n\v\f]*([+-]?[0-9]+)',text[cursor:])
   if not m:break
   parts[i]=int(m[1]);assert -2147483648<=parts[i]<=2147483647;cursor+=m.end()
   if i<3:
    if text[cursor:cursor+1]!='.':break
    cursor+=1
  assert 32768<=parts[0]<=65535 and parts[1]>=0 and parts[2]>=0
  if parts[3]<0:assert parts[0]>=32769 and parts[1]==parts[2]==0
  assert bytes.fromhex(c['quotedBodyHex'])==bytes.fromhex(c['inputHex']).replace(b'"',b'""')+b'"' and b'\0' not in bytes.fromhex(c['inputHex'])
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  assert z.testzip() is None;manifest=json.loads((BASE/'native-proof-manifest.json').read_bytes());assert set(z.namelist())=={f['path'] for f in manifest}
  for f in manifest:
   b=z.read(f['path']);assert len(b)==f['size'] and sha(b)==f['sha256'] and z.getinfo(f['path']).CRC==f['crc32']
  observed=z.read('run1/native.stdout.raw');assert observed==z.read('run2/native.stdout.raw')==(BASE/'native-observations.ndjson').read_bytes()
  native=[]
  for run in ['run1','run2']:
   get=lambda n:z.read(run+'/'+n)
   for name in ['adapter.cpp','cases.inc','expr-node.inc','version-node.inc','version-ctor.inc','value-type.inc','value-type2.inc','to-string-value.inc','filter-lexer-header.inc','unescape-quotes.inc','free-strings.inc']:assert get(name)==(BASE/name).read_bytes()
   for phase in ['compiler-env','compile','native']:
    r=receipt(get,phase)
    if phase=='native':native.append(r)
 assert native[0]['pid']!=native[1]['pid'] and native[0]['birth100ns']!=native[1]['birth100ns']
 records=[json.loads(l) for l in observed.splitlines()];assert len(records)==36
 serial={r['id']:(i,r) for i,r in enumerate(records) if r['kind']=='serialized'};quotes={r['id']:(i,r) for i,r in enumerate(records) if r['kind']=='quote-case'};assert len(serial)==len(quotes)==18
 edge=json.loads((BASE/'edge-public-plan.json').read_bytes());assert len(edge)==18
 for c,e in zip(cases,edge):
  si,s=serial[c['id']];qi,q=quotes[c['id']];assert s['inputHex']==c['inputHex'] and s['variantIndex']==3 and re.fullmatch('-?[0-9]+',s['value']) and -(1<<63)<=int(s['value'])<(1<<63)
  assert bytes.fromhex(s['serializedUtf8Hex']).decode('ascii')==s['value']
  assert q['bodyHex']==c['quotedBodyHex'] and q['decodedHex']==c['inputHex'] and q['errorCode']==0 and q['consumedBytes']==len(bytes.fromhex(c['quotedBodyHex']))
  assert e['id']==c['id'] and e['nativeValue']==s['value'] and e['serializedUtf8Hex']==s['serializedUtf8Hex'] and e['status']=='planned/unexecuted'
  assert e['provenance']['nativeSerializedRecordIndex']==si and e['provenance']['nativeQuoteRecordIndex']==qi and e['provenance']['edgeStdoutSha256']==sha(observed)
  body=bytes.fromhex(c['quotedBodyHex']);assert e['expressionUtf8HexCandidates']==[(b'v"'+body).hex(),(b'V"'+body).hex()]
 integration=json.loads((BASE/'formal-integration-proposal.json').read_bytes());old=json.loads((a.supplement128_dir/'public60classification.json').read_bytes())
 assert integration['status']=='planned/unexecuted' and integration['base60']==old and integration['edge18']==edge and integration['counts']=={'baseQuotedProposals':59,'baseConstructorOnlyNul':1,'newSignbitProposals':18,'totalQuotedProposals':77}
 assert all(x['status']=='excluded/no golden' for x in json.loads((BASE/'exclusions.json').read_bytes()))
 assert (a.actual1 is None)==(a.actual2 is None)
 if a.actual1:
  fresh=[]
  for d in [a.actual1,a.actual2]:
   get=lambda n:(d/n).read_bytes();assert get('native.stdout.raw')==observed
   for phase in ['compiler-env','compile','native']:
    r=receipt(get,phase)
    if phase=='native':fresh.append(r)
  assert fresh[0]['pid']!=fresh[1]['pid'] and fresh[0]['birth100ns']!=fresh[1]['birth100ns']
 print(json.dumps({'immutableRaw60AndSupplement128':True,'nativeCases':18,'nativeRecords':36,'standard':'C++17','signbitConversion':'MSVC implementation-defined observation','major32768ZeroNativeSerialized':bytes.fromhex(serial['major32768-zero'][1]['serializedUtf8Hex']).decode('ascii'),'stdoutSha256':sha(observed),'publicIntegration':'planned/unexecuted 59+NUL1+edge18','fixedZipProjectEvidence':False,'liveReadonlyProjectCompared':bool(a.current_project),'freshNativeCompared':bool(a.actual1),'fullASTParser':'unqualified'}))
if __name__=='__main__':main()
