"""固定原本goldenをgzipへ圧縮し、UI自己検証の同梱容量を抑える。"""
import gzip,hashlib
from pathlib import Path
root=Path(__file__).resolve().parent.parent
path=root/'tests/Fixtures/ImageTransforms/winimerge-transforms-golden.json'
data=path.read_bytes()
assert hashlib.sha256(data).hexdigest().upper()=='5383447EA3FF48F5C1F1A99F6CFFF8438568ED68445F5BB59FD4C2BC31FEFD82'
output=path.with_name('ui-golden.json.gz')
packed=gzip.compress(data,compresslevel=9,mtime=0)
assert gzip.decompress(packed)==data
output.write_bytes(packed)
print('sourceBytes=',len(data),'embeddedBytes=',len(packed),'sha256=',hashlib.sha256(packed).hexdigest())
