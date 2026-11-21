"""固定原本・native proof・公開planの独立reader。製品を呼ばない。"""
import sys
sys.dont_write_bytecode=True
import pathlib,json,hashlib,zipfile,argparse,subprocess,importlib.util
BASE=pathlib.Path(__file__).resolve().parent
COMMIT='6780924ba35f7f55af0f6f14dd302561175830a6'
SHA=lambda b:hashlib.sha256(b).hexdigest()
def require(value,message):
 if not value:raise ValueError(message)
def verify(check_git=False):
 manifest=json.loads((BASE/'fixed-manifest.json').read_bytes());files={p.relative_to(BASE).as_posix():p for p in BASE.rglob('*') if p.is_file() and p.name!='fixed-manifest.json'}
 require(set(files)=={x['path'] for x in manifest['files']},'fixed fileset')
 for item in manifest['files']:
  b=files[item['path']].read_bytes();require(len(b)==item['size'] and SHA(b)==item['sha256'],item['path'])
 sm=json.loads((BASE/'source-manifest.json').read_bytes());require(sm['commit']==COMMIT,'commit');archive=BASE.parent/'LineTransformRegex/reference-source.zip';require(SHA(archive.read_bytes())==sm['archiveSha256'],'reused source archive SHA');data={}
 with zipfile.ZipFile(archive) as z:
  require(z.testzip() is None,'source archive CRC')
  for item in sm['sources']:
   path=item['path'];b=(BASE/'additional-original'/path).read_bytes() if item['origin']=='additional-original' else z.read('original/'+path)
   require(len(b)==item['size'] and SHA(b)==item['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==item['blob'],path);data[path]=b
   if check_git:require(subprocess.check_output(['git','cat-file','blob',COMMIT+':'+path],cwd=BASE)==b,'Git blob '+path)
 cases=json.loads((BASE/'cases.json').read_bytes());raw=(BASE/'golden.ndjson').read_bytes();golden=[json.loads(l) for l in raw.splitlines()]
 require(len(cases)==len(golden)==70 and len({c['id'] for c in cases})==70 and [c['id'] for c in cases]==[g['id'] for g in golden],'case IDs/count')
 prov=json.loads((BASE/'provenance.json').read_bytes());require(prov['commit']==COMMIT and prov['sourceManifestSha256']==SHA((BASE/'source-manifest.json').read_bytes()),'provenance')
 proof=json.loads((BASE/'proof-manifest.json').read_bytes());receipts=[]
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  require(z.testzip() is None and len(z.namelist())==len(set(z.namelist())) and set(z.namelist())==set(proof),'proof entry set/CRC')
  for name,item in proof.items():
   b=z.read(name);require(len(b)==item['size'] and SHA(b)==item['sha256'] and z.getinfo(name).CRC==item['crc32'],name)
  for run in ['run1','run2']:
   r=json.loads(z.read(run+'/receipt.json'));cp=json.loads(z.read(run+'/compile-receipt.json'));receipts.append(r)
   require(r['sourceCommit']==COMMIT and r['caseCount']==70 and r['casesSha256']==SHA((BASE/'cases.json').read_bytes()) and r['adapterSha256']==SHA((BASE/'adapter.cpp').read_bytes()),run+' identity')
   require(r['pid']>0 and r['birthFileTime']>0 and r['heldProcessHandleSignaled'],run+' real child')
   for item in [r,cp]:require(item['exitCode']==0 and not item['timeout'] and item['stdoutEof'] and item['stderrEof'],run+' process completion')
   require(z.read(run+'/golden.ndjson')==raw and z.read(run+'/reference.stderr')==b'' and r['stdoutSha256']==SHA(raw) and r['stderrSha256']==SHA(b''),run+' stdout bytes')
   require(z.read(run+'/adapter.cpp')==(BASE/'adapter.cpp').read_bytes() and json.loads(z.read(run+'/provenance.json'))==prov,run+' adapter/provenance')
   for name,digest in cp['inputSha256'].items():require(SHA(z.read(run+'/'+name))==digest,run+' compiler input '+name)
   pcre={p.rsplit('/',1)[1]:SHA(b) for p,b in data.items() if p.startswith('Externals/poco/dependencies/pcre2/src/')};require(cp['pcreSourceSha256']==pcre,run+' PCRE2 source input')
   for span in prov['extractions']:
    original=data[span['path']];b=original[span['startByte']:span['endByte']];require(SHA(b)==span['sha256'],'unchanged span');require(original[:span['startByte']].count(b'\n')+1==span['startLine'] and original[:span['endByte']].count(b'\n')+1==span['endLine'],'span lines')
    if span['file']:require(z.read(run+'/'+span['file'])==b+b'\n','compile unchanged span')
   for c in cases:
    if c['fileUtf8Hex'] is not None:require(z.read(run+'/inputs/'+c['id']+'.tsv')==bytes.fromhex(c['fileUtf8Hex']),'list input bytes '+c['id'])
  require(receipts[0]['pid']!=receipts[1]['pid'] or receipts[0]['birthFileTime']!=receipts[1]['birthFileTime'],'independent children')
 for c,g in zip(cases,golden,strict=True):
  if c['nullArgs'] or len(c['args'])!=2:require(g['evaluated']==[] and g['value']=={'kind':'none'} and not g['errorUtf8Hex'],c['id']+' arity')
  elif c['args'][0]['type']=='throw':require(g['evaluated']==[0] and g['errorUtf8Hex'] and g['value'] is None,c['id']+' first throw')
  elif c['args'][1]['type']=='throw':require(g['evaluated']==[0,1] and g['errorUtf8Hex'] and g['value'] is None,c['id']+' second throw')
  else:require(g['evaluated']==[0,1] and not g['errorUtf8Hex'] and type(g['serializedUtf8Hex']) is str,c['id']+' normal evaluation')
  if c['id'].endswith('open-failure'):require(g['loadedList']=={'kind':'array','items':[]} and len(g['loaderLogs'])==len(g['functionLogs'])==1,c['id']+' empty list/diagnostic')
  if c['id']=='regex-file-invalid-regex-cs':require(len(g['loadedList']['items'])==1 and len(g['loaderLogs'])==len(g['functionLogs'])==1,'invalid regex skipped and later rule retained')
 spec=importlib.util.spec_from_file_location('replace_lists_plan',BASE/'generate-public-plan.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);plan=json.loads((BASE/'public-case-plan.json').read_bytes());require(plan==module.plan(),'static public plan')
 counts={k:sum(x['expectedOutcome']==k for x in plan) for k in ['serialized-key-comparison','runtime-rejection','compile-rejection','native-only']};require(counts=={'serialized-key-comparison':18,'runtime-rejection':4,'compile-rejection':6,'native-only':42},'public counts')
 if check_git:
  attrs=subprocess.check_output(['git','check-attr','text','--',*[str(p) for p in files.values()],str(BASE/'fixed-manifest.json')],cwd=BASE).decode();require(all(line.endswith(': unset') for line in attrs.splitlines()),'fixed -text attributes')
 return dict(passed=True,nativeCases=70,sourceBlobs=len(data),sourceSpans=len(prov['extractions']),goldenSha256=SHA(raw),publicPlan=counts,typedPublicEquivalence=False,nativeFileApiEquivalent=False,gitChecked=check_git)
if __name__=='__main__':
 ap=argparse.ArgumentParser();ap.add_argument('--verify',action='store_true');ap.add_argument('--git',action='store_true');ap.add_argument('--actual',type=pathlib.Path);a=ap.parse_args();result=verify(a.git)
 if a.actual:require(a.actual.read_bytes()==(BASE/'golden.ndjson').read_bytes(),'actual stdout bytes')
 print(json.dumps(result,separators=(',',':')))
