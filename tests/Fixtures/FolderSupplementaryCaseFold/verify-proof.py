"""Native child証拠の独立reader。source表抽出とnative観測を分けて照合する。"""
import sys
sys.dont_write_bytecode=True
import argparse,binascii,hashlib,io,json,pathlib,zipfile
BASE=pathlib.Path(__file__).resolve().parent
sha=lambda b:hashlib.sha256(b).hexdigest()
def req(v,m):
 if not v:raise ValueError(m)
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--repo-root',type=pathlib.Path,required=True);a=ap.parse_args()
 raw=(BASE/'native-casefold-proof.zip').read_bytes();proof=json.loads((BASE/'proof-receipt.json').read_bytes());req(sha(raw)==proof['sha256'] and len(raw)==proof['size'],'proof size/SHA')
 with zipfile.ZipFile(io.BytesIO(raw)) as z:
  names=z.namelist();index=json.loads(z.read('entry-index.json'));req(len(names)==len(set(names)) and set(names)==set(index)|{'entry-index.json'} and z.testzip() is None,'proof exactset/CRC')
  entries={n:z.read(n) for n in index}
  for n,m in index.items():
   b=entries[n];req(len(b)==m['size'] and sha(b)==m['sha256'] and z.getinfo(n).CRC==m['crc32']==binascii.crc32(b)&0xffffffff,'indexed '+n)
 repo=a.repo_root.resolve();base=repo/'tests/Fixtures/LineTransformReplaceLists';sm=json.loads((base/'source-manifest.json').read_bytes());prov=json.loads(entries['provenance.json']);req(prov['sourceCommit']==sm['commit'] and prov['sourceManifestSha256']==sha((base/'source-manifest.json').read_bytes()),'source manifest')
 data={}
 with zipfile.ZipFile(base.parent/'LineTransformRegex/reference-source.zip') as z:
  req(sha((base.parent/'LineTransformRegex/reference-source.zip').read_bytes())==sm['archiveSha256']==prov['sourceArchiveSha256'] and z.testzip() is None,'source ZIP')
  for item in sm['sources']:
   p=item['path'];b=(base/'additional-original'/p).read_bytes() if item['origin']=='additional-original' else z.read('original/'+p)
   req(sha(b)==item['sha256'] and len(b)==item['size'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==item['blob'],'original source '+p);data[p]=b
 req(prov['existingProvenance']==json.loads((base/'provenance.json').read_bytes()),'original list provenance')
 for s in prov['existingProvenance']['extractions']+[prov['newSpan']]:
  b=data[s['path']][s['startByte']:s['endByte']];req(sha(b)==s['sha256'],'unchanged native span')
  if s['file']:req(entries[s['file']]==b+b'\n','compiled unchanged native span')
 cp=json.loads(entries['compile-receipt.json']);req(cp['exitCode']==0 and not cp['timeout'] and cp['stdoutEof'] and cp['stderrEof'],'compile completed')
 req(cp['sdkEnvSha256']==sha(entries['sdk-env-used.cmd']) and len(cp['compilerSha256'])==64,'compiler/SDK')
 for n,d in cp['inputSha256'].items():req(sha(entries[n])==d,'compile input '+n)
 pcre={p.rsplit('/',1)[1]:sha(b) for p,b in data.items() if p.startswith('Externals/poco/dependencies/pcre2/src/')};req(pcre==prov['reusedPcreSources'],'reused fixed PCRE2 source hashes')
 e=json.loads(entries['fixed-ucd-evidence.json']);pairs=e['pairs'];req(len(pairs)==e['directions']==564 and e['evidenceKind']=='fixed-source-table-extraction; not native process observations','table extraction identity')
 req(e['nativeLfSourceSha256']==sha(data['Externals/poco/dependencies/pcre2/src/pcre2_ucd.c'])=='6ebdc9043dcb13832189a45818f767afe9c4d6df078fb37db6d55d80126dce92','native UCD source SHA')
 req(e['sourceSha256']=='0fcaf2440147c666a747b3fa3b108012ac30db84cc554d852dfb76b98893716b','CRLF source identity')
 mapping={p['scalar']:p for p in pairs};req(len(mapping)==564,'unique directions')
 for p in pairs:req(chr(p['scalar']).encode().hex()==p['scalarUtf8Hex'] and chr(p['other']).encode().hex()==p['otherUtf8Hex'] and mapping[p['other']]['other']==p['scalar'],'pair scalar identity')
 cases=json.loads(entries['cases.json']);direct=json.loads(entries['proposed-direct-cases.json']);req(len(direct)==1128 and cases[:1128]==direct and len(cases)==1132 and len({c['id'] for c in cases})==1132,'native input identities')
 for c in direct:
  p=mapping[int(c['id'].rsplit('-u',1)[1],16)];req(c['needleUtf8Hex']==p['scalarUtf8Hex'] and c['sourceUtf8Hex']==p['otherUtf8Hex'],'direct source/needle')
  req(c['expectedUtf8HexFromFixedUcd']==(c['sourceUtf8Hex'] if c['caseSensitive'] else '58'),'table prediction direct')
 low=[p for p in pairs if p['scalar']<p['other']];req(len(low)==282,'disjoint rule count')
 for i,name in enumerate(['low-needle','high-needle']):
  nk,sk=('scalarUtf8Hex','otherUtf8Hex') if i==0 else ('otherUtf8Hex','scalarUtf8Hex')
  rule=b''.join(bytes.fromhex(p[nk])+b'\tX\n' for p in low);source=b''.join(bytes.fromhex(p[sk]) for p in low)
  req(entries[name+'.tsv']==rule,'separate native list input')
  for c in cases[1128+i*2:1130+i*2]:req(c['listPath']==name+'.tsv' and c['fileUtf8Hex']==rule.hex() and c['sourceUtf8Hex']==source.hex() and c['expectedUtf8HexFromFixedUcd']==(source.hex() if c['caseSensitive'] else '58'*282),'batch table prediction')
 outputs=[];receipts=[]
 for run in ['run1','run2']:
  r=json.loads(entries[run+'-receipt.json']);out=entries[run+'-golden.ndjson'];rows=[json.loads(l) for l in out.splitlines()];req(len(rows)==1132 and [g['id'] for g in rows]==[c['id'] for c in cases],'native observed row count/IDs')
  req(r['exitCode']==0 and not r['timeout'] and r['stdoutEof'] and r['stderrEof'] and r['heldProcessHandleSignaled'] and r['pid']>0 and r['birthFileTime']>0,'native child completion')
  req(r['stdoutSha256']==sha(out) and r['stderrSha256']==sha(b'') and entries[run+'-reference.stderr']==b'' and r['casesSha256']==sha(entries['cases.json']),'native stdout/stderr bytes')
  for c,g in zip(cases,rows,strict=True):req(not g['errorUtf8Hex'] and not g['loaderLogs'] and not g['functionLogs'] and g['value']==dict(kind='string',utf8Hex=c['expectedUtf8HexFromFixedUcd']) and g['serializedUtf8Hex']==c['expectedUtf8HexFromFixedUcd'],'native observation vs table prediction '+c['id'])
  outputs.append(out);receipts.append(r)
 req(outputs[0]==outputs[1] and (receipts[0]['pid'],receipts[0]['birthFileTime'])!=(receipts[1]['pid'],receipts[1]['birthFileTime']),'independent children/stdout match')
 req(receipts[0]['executableSha256']==receipts[1]['executableSha256'],'same compiled executable')
 print(json.dumps(dict(passed=True,indexedEntries=len(index),sourceBlobs=len(data),sourceSpans=len(prov['existingProvenance']['extractions'])+1,directNativeRows=1128,nativeListRows=4,rulesPerSeparateBatch=282,observedRowsPerChild=1132,independentChildren=2,noThrow=True,allNativeObservedBytesMatchPredictions=True,productExecuted=False)))
if __name__=='__main__':main()