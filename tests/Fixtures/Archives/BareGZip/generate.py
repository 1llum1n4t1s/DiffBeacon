# SPDX-License-Identifier: CC0-1.0
# Synthetic inputs and this deterministic generator are dedicated to CC0.
import argparse,gzip,hashlib,io,json,pathlib,struct,sys,tarfile,unicodedata,zlib
H=lambda b:hashlib.sha256(b).hexdigest().upper()
A=b'DiffBeacon own synthetic gzip payload A\n'+bytes(range(256));B=b'Payload B\x00\r\n'+bytes(range(31,-1,-1));C=b'Payload C\n\xff\x80\x00'
def member(payload,name=None,fhcrc=False,bad_fhcrc=False,flags_extra=0,method=8):
 flags=(8 if name is not None else 0)|(2 if fhcrc else 0)|flags_extra
 header=bytes([31,139,method,flags])+struct.pack('<I',0)+bytes([0,255])
 if name is not None:header+=name+b'\x00'
 if fhcrc:header+=struct.pack('<H',(zlib.crc32(header)&65535)^(1 if bad_fhcrc else 0))
 d=zlib.compressobj(9,zlib.DEFLATED,-15);body=d.compress(payload)+d.flush()
 return header+body+struct.pack('<II',zlib.crc32(payload)&0xffffffff,len(payload)&0xffffffff)
def flip_crc(b):return b[:-8]+bytes([b[-8]^1])+b[-7:]
def flip_size(b):return b[:-4]+bytes([b[-4]^1])+b[-3:]
def safe_path(name):
 # Evidence translation of existing ManagedArchive.ValidateEntryPath, not a new policy.
 if not name or name.isspace() or len(name)>4096 or any(unicodedata.category(c)=='Cc' or c in ':*?"<>|' for c in name):return False,None
 n=name.replace('\\','/')
 if n.startswith('/'):return False,None
 n=n.rstrip('/')
 for part in n.split('/'):
  if not part or part in ('.','..') or part.endswith((' ','.')):return False,None
  stem=part.split('.')[0].upper()
  if stem in ('CON','PRN','AUX','NUL') or len(stem)==4 and stem[:3] in ('COM','LPT') and stem[-1] in '123456789':return False,None
 return True,n
def metadata(first,physical):
 state='absent' if first is None else 'empty' if first==b'' else 'present'
 result={'firstNameState':state,'firstNameRawHex':None if first is None else first.hex().upper(),'physicalBasename':physical,'fallbackRequired':state!='present','fallbackDerivedEntryName':None,'defaultCodePage':28591,'hostACPInference':False,'perCodePage':{}}
 for cp,codec in [(28591,'iso8859-1'),(65001,'utf-8'),(932,'cp932')]:
  if state!='present':result['perCodePage'][str(cp)]={'decodeAccepted':None,'decodedFirstName':None,'safeEntryPathAccepted':None,'normalizedEntryPath':None,'reason':'fallback contract intentionally unresolved'};continue
  try:
   decoded=first.decode(codec,errors='strict');safe,normalized=safe_path(decoded);result['perCodePage'][str(cp)]={'decodeAccepted':True,'decodedFirstName':decoded,'safeEntryPathAccepted':safe,'normalizedEntryPath':normalized}
  except UnicodeDecodeError:result['perCodePage'][str(cp)]={'decodeAccepted':False,'decodedFirstName':None,'safeEntryPathAccepted':False,'normalizedEntryPath':None,'reason':'strict name decoding rejected'}
 return result
def gzip_check(data):
 try:b=gzip.decompress(data);return {'accepted':True,'fullBytesHex':b.hex().upper(),'bytes':len(b),'sha256':H(b)}
 except (OSError,EOFError,zlib.error) as e:return {'accepted':False,'errorType':type(e).__name__}
def zlib_concat_check(data):
 try:
  remain=data;decoded=[];members=0
  while remain:
   d=zlib.decompressobj(31);part=d.decompress(remain)+d.flush()
   if not d.eof:raise EOFError('incomplete gzip member')
   if len(d.unused_data)>=len(remain):raise ValueError('decoder made no progress')
   decoded.append(part);members+=1;remain=d.unused_data
  b=b''.join(decoded);return {'accepted':True,'members':members,'fullBytesHex':b.hex().upper(),'bytes':len(b),'sha256':H(b)}
 except (EOFError,zlib.error,ValueError) as e:return {'accepted':False,'errorType':type(e).__name__}
def cases():
 out=[]
 def add(id,parts,first=None,names=None,valid=True,classification='bare-gzip payload metadata only',physical=None):
  data=b''.join(parts);payload=A if names is None else names # names argument supplies own expected full payload, never decoder output.
  out.append((id,physical or id+'.gz',data,payload,first,valid,classification))
 add('first-name-differs',[member(A,b'ChosenName.TXT')],b'ChosenName.TXT',physical='physical-label.gz')
 add('absent-first-later-name',[member(A),member(B,b'later.txt')],None,A+B)
 add('empty-first-later-name',[member(A,b''),member(B,b'later.txt')],b'',A+B)
 add('leading-empty-member',[member(b'',b'FirstEmpty.TXT'),member(B,b'later.txt')],b'FirstEmpty.TXT',B)
 add('mixed-later-names',[member(A,b'FirstCase.txt'),member(B,b'../ignored.txt'),member(C,b'\x81')],b'FirstCase.txt',A+B+C)
 for id,name in [('first-latin1',b'caf\xe9.txt'),('first-utf8','日本.txt'.encode('utf-8')),('first-cp932',b'\x93\xfa\x96\x7b.txt'),('invalid-cp932',b'\x81'),('invalid-utf8',b'\xc3('),('unsafe-parent',b'../escape.txt'),('unsafe-absolute',b'/root.txt'),('unsafe-drive',b'C:\\escape.txt'),('unsafe-empty-separator',b'dir//leaf.txt'),('unsafe-control',b'bad\x01name.txt'),('safe-relative-slash',b'Dir/Leaf.TXT'),('safe-relative-backslash',b'Dir\\Leaf.TXT')]:add(id,[member(A,name)],name)
 add('valid-fhcrc',[member(A,b'HeaderCRC.txt',True)],b'HeaderCRC.txt')
 add('bad-fhcrc',[member(A,b'HeaderCRC.txt',True,True)],b'HeaderCRC.txt',valid=False)
 add('bad-crc',[flip_crc(member(A,b'CRC.txt'))],b'CRC.txt',valid=False)
 add('bad-isize',[flip_size(member(A,b'ISIZE.txt'))],b'ISIZE.txt',valid=False)
 add('truncated-footer',[member(A,b'Footer.txt')[:-3]],b'Footer.txt',valid=False)
 add('trailing-garbage',[member(A,b'Trailing.txt')+b'not-a-member'],b'Trailing.txt',valid=False)
 add('trailing-zero-padding',[member(A,b'Padding.txt')+b'\0\0\0'],b'Padding.txt',valid=False)
 add('reserved-flags',[member(A,b'Reserved.txt',flags_extra=32)],b'Reserved.txt',valid=False)
 add('invalid-method',[member(A,b'Method.txt',method=9)],b'Method.txt',valid=False)
 add('misleading-tar-gz',[member(A,b'NotTar.txt')],b'NotTar.txt',classification='gzip valid / non-TAR payload; TAR classification must reject, product classification not executed',physical='misleading.tar.gz')
 add('later-bad-crc',[member(A,b'First.txt'),flip_crc(member(B,b'Later.txt'))],b'First.txt',A+B,False)
 add('later-bad-isize',[member(A,b'First.txt'),flip_size(member(B,b'Later.txt'))],b'First.txt',A+B,False)
 add('later-bad-fhcrc',[member(A,b'First.txt'),member(B,b'Later.txt',True,True)],b'First.txt',A+B,False)
 add('truncated-first-header',[bytes([31,139,8,8,0,0,0,0,0,255])+b'unterminated-name'],b'unterminated-name',b'',False)
 add('unsafe-device',[member(A,b'CON.txt')],b'CON.txt')
 assert len(out)==32;return out
def generate(destination):
 root=pathlib.Path(destination);root.mkdir(exist_ok=False);inputs=root/'inputs';inputs.mkdir();expected=[]
 for id,physical,data,payload,first,valid,classification in cases():
  (inputs/physical).write_bytes(data);g=gzip_check(data);z=zlib_concat_check(data)
  if valid:assert g['accepted'] and z['accepted'] and g['fullBytesHex']==z['fullBytesHex']==payload.hex().upper() and g['sha256']==z['sha256']==H(payload),id
  else:assert not z['accepted'],id
  row={'id':id,'input':physical,'inputBytes':len(data),'inputSHA256':H(data),'expectedGzipStructurallyValid':valid,'expectedPayloadBytes':len(payload),'expectedPayloadFullHex':payload.hex().upper(),'expectedPayloadSHA256':H(payload),'firstMemberMetadata':metadata(first,physical),'classification':classification,'pythonGzipObserved':g,'pythonZlibConcatObserved':z}
  if id=='misleading-tar-gz':
   try:
    with tarfile.open(fileobj=io.BytesIO(payload),mode='r:'):pass
    raise AssertionError('synthetic non-TAR unexpectedly parsed')
   except tarfile.TarError:row['pythonTarClassificationRejected']=True
  expected.append(row)
 result={'license':'CC0-1.0','origin':'own synthetic payloads and own raw gzip header/trailer generator, no imported fixture','encodingSelection':'default RFC1952 Latin1; explicit 28591/65001/932 only, no host ACP','nameSafetyEvidence':'ManagedArchive.cs246-265; preserve case, normalize backslash, allow safe relative subpaths','firstNameSelectionContract':'first member FNAME only; later names ignored; absent/empty records fallbackRequired without guessed product-derived name','PythonValidationLimits':{'gzipFHCRC':'stdlib gzip does not verify header CRC; observed malformed FHCRC acceptance is not RFC success','gzipReservedFlags':'stdlib gzip can ignore reserved flags; observed success is not RFC success','gzipTrailingZero':'stdlib gzip can accept zero padding; not proof of current strict member-boundary policy','zlib':'separate per-member wbits31 decoder verifies checksum/header rules in this runtime; no product-codec/WinMerge equivalence claim'},'cases':expected}
 (root/'expected.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
 (root/'LICENSE.txt').write_text('SPDX-License-Identifier: CC0-1.0\nThese original synthetic fixture bytes and generator are dedicated to the public domain under CC0 1.0. No upstream fixture or WinMerge binary was used.\n',encoding='utf-8',newline='\n')
 print(json.dumps({'cases':len(expected),'inputBytes':sum(len(x[2]) for x in cases()),'expectedSHA256':H((root/'expected.json').read_bytes()),'python':sys.version.split()[0],'zlib':zlib.ZLIB_RUNTIME_VERSION}))
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--output',required=True);a=p.parse_args();generate(a.output)