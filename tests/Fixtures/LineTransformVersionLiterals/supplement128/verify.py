"""Independent original bytes/process reader and raw60/nativequote/serialized joins."""
import sys
sys.dont_write_bytecode=True
import argparse,pathlib,json,hashlib,zipfile,re
BASE=pathlib.Path(__file__).resolve().parent
REFSHA='18ed25d74eba5e151bf5fd1a2e811b2490af2ce4c3e39b273a2d563abe8da4e7'
def sha(b):return hashlib.sha256(b).hexdigest()
def check_receipt(get,phase):
 r=json.loads(get(phase+'-receipt.json'))
 assert r['pid']>0 and r['birth100ns']>0 and r['heldOsHandle']>0 and r['safeHandleType']=='Handle' and r['heldSignaled'] and r['exitCode']==r['osExitCode']==0 and r['stdoutEof'] and r['stderrEof'] and not r['timeout']
 for stream in ['stdout','stderr']:assert sha(get(phase+'.'+stream+'.raw'))==r[stream+'Sha256']
 if phase=='compile':assert pathlib.PureWindowsPath(r['command'][0]).name.lower()=='cl.exe' and len(r['command'])==2 and r['command'][1].startswith('@')
 if phase=='native':assert r['executableSha256']==sha(get('reference.exe')) and not get('native.stderr.raw')
 return r
def main():
 ap=argparse.ArgumentParser();ap.add_argument('--reference-zip',type=pathlib.Path,required=True);ap.add_argument('--raw60-dir',type=pathlib.Path,required=True);ap.add_argument('--actual1',type=pathlib.Path);ap.add_argument('--actual2',type=pathlib.Path);a=ap.parse_args()
 fixed=json.loads((BASE/'fixed-manifest.json').read_bytes());assert {p.relative_to(BASE).as_posix() for p in BASE.rglob('*') if p.is_file()}=={f['path'] for f in fixed}|{'fixed-manifest.json'}
 for f in fixed:
  b=(BASE/f['path']).read_bytes();assert len(b)==f['size'] and sha(b)==f['sha256'],f['path']
 provenance=json.loads((BASE/'raw60-provenance.json').read_bytes());raw=a.raw60_dir
 assert sha((raw/'fixed-manifest.json').read_bytes())==provenance['fixedManifestSha256']
 for f in json.loads((raw/'fixed-manifest.json').read_bytes()):
  b=(raw/f['path']).read_bytes();assert len(b)==f['size'] and sha(b)==f['sha256']
 for name,key in [('native-proof.zip','nativeProofSha256'),('golden.ndjson','rawStdoutSha256'),('cases.json','casesSha256')]:assert sha((raw/name).read_bytes())==provenance[key]
 assert sha(a.reference_zip.read_bytes())==REFSHA
 sources=json.loads((BASE/'source-manifest.json').read_bytes());assert sources['commit']=='6780924ba35f7f55af0f6f14dd302561175830a6' and sources['referenceArchiveSha256']==REFSHA
 originals={}
 with zipfile.ZipFile(a.reference_zip) as z:
  assert z.testzip() is None
  for s in sources['sources']:
   b=z.read('original/'+s['path']) if s['origin']=='fixed-reference-zip' else (BASE/'additional-original'/s['path']).read_bytes()
   assert len(b)==s['size'] and sha(b)==s['sha256'] and hashlib.sha1(b'blob '+str(len(b)).encode()+b'\0'+b).hexdigest()==s['gitBlob']
   if s['path'].endswith('FilterLexer.re'):assert s['gitBlob']=='6163d1a1f5433de3c81eb214530c5f825f694290'
   if s['path'].endswith('FilterLexer.h'):assert s['gitBlob']=='6891df502b9d7d8baa5a965ca52625ba7837e6cf'
   originals[s['path']]=b
 assert (BASE/'LICENSE-GPL-2.0.md').read_bytes()==originals['LICENSE.md']
 for e in json.loads((BASE/'extractions.json').read_bytes()):
  b=originals[e['path']];span=b[e['startByte']:e['endByte']];assert span.startswith(e['marker'].encode()) and sha(span)==e['sha256'] and span==(BASE/e['file']).read_bytes()
  assert b[:e['startByte']].count(b'\n')+1==e['startLine'] and b[:e['endByte']].count(b'\n')+1==e['endLine']
 assert b're2c:flags:case-insensitive = 1;' in (BASE/'re2c-flags-static.inc').read_bytes()
 assert b'TK_VERSION_LITERAL' in (BASE/'version-token-static.inc').read_bytes()
 with zipfile.ZipFile(BASE/'native-proof.zip') as z:
  assert z.testzip() is None;pm=json.loads((BASE/'native-proof-manifest.json').read_bytes());assert set(z.namelist())=={f['path'] for f in pm}
  for f in pm:
   b=z.read(f['path']);assert len(b)==f['size'] and sha(b)==f['sha256'] and z.getinfo(f['path']).CRC==f['crc32']
  observed=z.read('run1/native.stdout.raw');assert observed==z.read('run2/native.stdout.raw')==(BASE/'native-observations.ndjson').read_bytes()
  receipts=[]
  for run in ['run1','run2']:
   get=lambda p:z.read(run+'/'+p)
   for f in ['adapter.cpp','cases.inc','quote-probes.inc','expr-node.inc','version-node.inc','version-ctor.inc','value-type.inc','value-type2.inc','to-string-value.inc','filter-lexer-header.inc','unescape-quotes.inc','free-strings.inc']:assert get(f)==(BASE/f).read_bytes()
   for phase in ['compiler-env','compile','native']:
    r=check_receipt(get,phase)
    if phase=='native':receipts.append(r)
 assert receipts[0]['pid']!=receipts[1]['pid'] and receipts[0]['birth100ns']!=receipts[1]['birth100ns']
 records=[json.loads(l) for l in observed.splitlines()];assert len(records)==128
 serialized={r['id']:(i,r) for i,r in enumerate(records) if r['kind']=='serialized'};quotes={r['id']:(i,r) for i,r in enumerate(records) if r['kind']=='quote-case'};probes={r['id']:r for r in records if r['kind']=='quote-probe'}
 assert len(serialized)==len(quotes)==60 and len(probes)==8
 rawcases=json.loads((raw/'cases.json').read_bytes());cases=json.loads((BASE/'cases.json').read_bytes());rawrows={r['id']:(i,r) for i,r in enumerate(json.loads(l) for l in (raw/'golden.ndjson').read_bytes().splitlines())}
 assert [c['id'] for c in cases]==[c['id'] for c in rawcases]
 classifications=json.loads((BASE/'public60classification.json').read_bytes());assert len(classifications)==60 and [p['id'] for p in classifications]==[c['id'] for c in cases]
 accepted=rejected=0
 for c,p,rc in zip(cases,classifications,rawcases):
  input=bytes.fromhex(c['inputHex']);body=bytes.fromhex(c['quotedBodyHex']);ri,rr=rawrows[c['id']];si,sr=serialized[c['id']];qi,qr=quotes[c['id']]
  assert c['inputHex']==rc['inputHex']==rr['inputHex']==sr['inputHex'] and sr['variantIndex']==rr['variantIndex']==3 and sr['value']==rr['value']
  assert bytes.fromhex(sr['serializedUtf8Hex']).decode('ascii')==rr['value']
  assert body==input.replace(b'"',b'""')+b'"' and qr['bodyHex']==c['quotedBodyHex']
  assert p['status']=='planned/unexecuted' and p['provenance']['raw60RecordIndex']==ri and p['provenance']['nativeQuoteRecordIndex']==qi and p['provenance']['nativeSerializedRecordIndex']==si
  assert p['provenance']['raw60StdoutSha256']==provenance['rawStdoutSha256'] and p['provenance']['supplementStdoutSha256']==sha(observed)
  assert p['nativeValue']==rr['value'] and p['serializedUtf8Hex']==sr['serializedUtf8Hex'] and p['quotedBodyHex']==qr['bodyHex'] and p['decodedHex']==qr['decodedHex']
  if b'\0' not in input:
   accepted+=1;assert qr['errorCode']==0 and bytes.fromhex(qr['decodedHex'])==input and qr['consumedBytes']==len(body) and p['classification']=='public-proposal'
   assert p['expressionUtf8HexCandidates']==[(b'v"'+body).hex(),(b'V"'+body).hex()]
  else:
   rejected+=1;assert qr['errorCode']==-2 and bytes.fromhex(qr['decodedHex'])==input.split(b'\0')[0] and p['classification']=='constructor-only-public-rejected' and p['expressionUtf8HexCandidates']==[]
 assert accepted==59 and rejected==1
 # Independently written expected quote-method results; no numeric packing expected values.
 expected={'doublequote':(b'a"b',0,5),'backslash-n':(b'a\\nb',0,5),'backslash-quote':(b'a\\',0,3),'ASCII-newlines':(b'a\r\nb\tc',0,7),'unterminated':(b'abc',-2,3),'NUL-before-quote':(b'a',-2,1),'empty-quoted':(b'',0,1),'doublequote-no-close':(b'"',-2,2)}
 for p in json.loads((BASE/'quote-probes.json').read_bytes()):
  r=probes[p['id']];assert r['bodyHex']==p['bodyHex'];assert (bytes.fromhex(r['decodedHex']),r['errorCode'],r['consumedBytes'])==expected[p['id']]
 assert (a.actual1 is None)==(a.actual2 is None)
 if a.actual1:
  fresh=[]
  for actual in [a.actual1,a.actual2]:
   get=lambda p:(actual/p).read_bytes();assert get('native.stdout.raw')==observed
   for phase in ['compiler-env','compile','native']:
    r=check_receipt(get,phase)
    if phase=='native':fresh.append(r)
  assert fresh[0]['pid']!=fresh[1]['pid'] and fresh[0]['birth100ns']!=fresh[1]['birth100ns']
 print(json.dumps({'raw60Immutable':True,'nativeSerialized':60,'nativeQuotes':60,'quoteProbes':8,'quotedPublicProposals':accepted,'constructorOnlyPublicRejected':rejected,'twoRealNativeReceipts':True,'stdoutSha256':sha(observed),'staticVPprefixCaseInsensitive':True,'lexerParserDirectives':'unexecuted','fullSerializerTypedContextAST':'unqualified','freshCaptureCompared':bool(a.actual1)}))
if __name__=='__main__':main()
