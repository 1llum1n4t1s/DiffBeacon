"""Windowsの独立OS API全256入力、macOSは明示1252/437互換値。clipboardは操作しない。"""
import ctypes, json, sys
from pathlib import Path
root=Path(sys.argv[1]); root.mkdir(exist_ok=True)
cases=[]; api=[]
if sys.platform=='win32':
 kernel=ctypes.WinDLL('kernel32'); native=ctypes.WinDLL('user32').CharToOemBuffA
 native.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_uint]; native.restype=ctypes.c_int
 for value in range(256):
  source=ctypes.create_string_buffer(bytes([value,0])); destination=(ctypes.c_ubyte*1)(0)
  result=native(ctypes.addressof(source),ctypes.addressof(destination),1)
  api.append(dict(input=value,result=result,output=int(destination[0])))
 def mapped(value):
  assert api[value]['result']!=0
  return api[value]['output']
 source=bytes(range(256)); valid=all(x['result']!=0 for x in api if x['input']!=92)
 cases.append(('all256',source,bytes(x['output'] if x['input']!=92 else 92 for x in api),0 if valid else 2))
 cases.append(('literal-token-escape',b'\xe9<bh:e9>\\<\\\\',bytes([mapped(233),233,60,92]),0))
 ansi=kernel.GetACP(); oem=kernel.GetOEMCP(); oracle='independent ctypes user32 CharToOemBuffA, 1byte+NUL bounded source/exact1byte destination; no Frhed UB'
else:
 cases=[('latin',b'caf\xe9',b'caf\x82',0),('literal-token-escape',b'\xe9<bh:e9>\\<\\\\',b'\x82\xe9<\\',0),('undefined-1252',b'\x81',b'',2)]
 ansi=1252; oem=437; oracle='Python stdlib fixed CP1252/CP437 compatibility; not Windows OS API evidence'
result=[]
for name,source,expected,exitcode in cases:
 (root/(name+'.input')).write_bytes(source)
 (root/(name+'.expected')).write_bytes(expected)
 # 拒否ケースは既存出力を独立固定値として保持させる。
 if exitcode!=0: (root/(name+'.actual')).write_bytes(b'KEEP')
 result.append(dict(name=name,input=str(root/(name+'.input')),actual=str(root/(name+'.actual')),expected=str(root/(name+'.expected')),expectedExit=exitcode))
receipt=dict(oracle=oracle,ansiCodePage=ansi,oemCodePage=oem,api=api,cases=result,osClipboardExecuted=False)
(root/'reference.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
print(json.dumps(receipt))
