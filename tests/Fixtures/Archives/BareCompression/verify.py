# SPDX-License-Identifier: CC0-1.0
import argparse,hashlib,io,json,pathlib,subprocess,tarfile,zipfile
from generate import all_bzip,H,validate_reference
p=argparse.ArgumentParser();p.add_argument('--root',required=True);p.add_argument('--z-reference',required=True);p.add_argument('--reference-source');p.add_argument('--run');a=p.parse_args()
root=pathlib.Path(a.root);z,reference_proof=validate_reference(a.z_reference,a.reference_source)
expected=json.loads((root/'expected.json').read_bytes());verified=[]
with zipfile.ZipFile(root/'inputs.zip') as f:
    assert f.testzip() is None
    for row in expected['cases']:
        data=f.read(row['input']);assert H(data)==row['inputSHA256'] and len(data)==row['inputBytes']
        if row['valid'] or row['input']=='..bz2':
            raw=all_bzip(data) if data.startswith(b'BZh') else subprocess.run([str(z),'-d','-c'],input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True).stdout
            assert raw==bytes.fromhex(row['decodedHex'])
            if a.run and row['valid']:
                exported=pathlib.Path(a.run)/('export-'+row['input']+'.bin')
                assert exported.read_bytes()==raw
        elif row['input'].endswith('.bz2'):
            try:all_bzip(data)
            except (ValueError,OSError,EOFError):pass
            else:raise AssertionError('malformed BZip2 accepted '+row['input'])
        verified.append(row['input'])
    mixed=f.read('mixed.bz2');assert H(mixed)==expected['mixedSHA256']
    with tarfile.open(fileobj=io.BytesIO(all_bzip(all_bzip(mixed))),mode='r:') as t:
        assert t.getnames()==['inner.bz2'];assert all_bzip(t.extractfile('inner.bz2').read())==b'hello\n'
    assert all_bzip(all_bzip(all_bzip(f.read('chain.bz2'))))==b'hello\n'
if a.run:
    work=pathlib.Path(a.run)
    for name in ['ancestor-copy.json','ancestor-unpack/project.json','ancestor-binary-copy.json','ancestor-binary-unpack/project.json']:
        path=work/name;j=json.loads(path.read_bytes());assert j['formatVersion']==9
        for side in ['leftArchiveInput','rightArchiveInput']:
            parent=j['entries'][0][side];assert parent['containerCompressionPayloadKinds']==['File'];rows=parent['workingTexts'];assert len(rows)==2
            found=set()
            for row in rows:
                mode=row['containerCompressionPayloadKinds'][1];found.add(mode);raw=(path.parent/row['snapshotPath']).read_bytes()
                assert raw==('working '+mode+'\n').encode() and H(raw)==row['sha256']
            assert found=={'Auto','Tar'}
    for name in ['ancestor.zip','ancestor-binary.zip','v9.zip']:
        with zipfile.ZipFile(work/name) as f:assert f.testzip() is None
print(json.dumps({'verified':verified,'allBytes':True,'referenceQualification':reference_proof,'zBoundary':'no checksum, declared length or explicit EOF; no semantic-corruption claim'}))
