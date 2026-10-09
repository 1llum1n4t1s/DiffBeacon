# SPDX-License-Identifier: CC0-1.0
# Synthetic fixture bytes and generator dedicated to CC0.
import argparse,bz2,hashlib,io,json,os,pathlib,stat,subprocess,tarfile,zipfile
H=lambda b:hashlib.sha256(b).hexdigest().upper()

SOURCE_HASHES={
    'compress.c':'29C5A78005921A7881D8C83EA711E826C8C87F354B4A2F47F8C474117F6646D8',
    'patchlevel.h':'458F2AF3AA2862B80E52D45F277FDA6D105A225DD0629ECA9153DD3B060B47A3',
    'UNLICENSE':'7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C'}

def require_unlinked(path):
    # resolve()の前に全祖先を調べ、Windows junctionも拒否する。
    path=pathlib.Path(os.path.abspath(path))
    for item in (path,*path.parents):
        info=item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info,'st_file_attributes',0)&0x400:
            raise ValueError('linked reference path: '+str(item))
    return path

def validate_reference(reference,source_root=None):
    z=require_unlinked(reference)
    proof_path=require_unlinked(z.parent/'build-proof.json')
    def unique_pairs(pairs):
        result={}
        for key,value in pairs:
            if key in result:raise ValueError('duplicate build-proof property: '+key)
            result[key]=value
        return result
    proof=json.loads(proof_path.read_text(encoding='utf-8-sig'),object_pairs_hook=unique_pairs)
    if not isinstance(proof,dict):raise ValueError('build-proof must be an object')
    source_root=require_unlinked(source_root or pathlib.Path(__file__).absolute().parent.parent/'TarZ'/'reference-source')
    for name,sha in SOURCE_HASHES.items():
        if H(require_unlinked(source_root/name).read_bytes())!=sha:
            raise ValueError('fixed reference source SHA mismatch: '+name)
    for key,path in [('decoder',z),('source',source_root/'compress.c')]:
        value=proof.get(key)
        if not isinstance(value,str) or not pathlib.Path(value).is_absolute() or os.path.normcase(os.path.abspath(value))!=os.path.normcase(str(path)):
            raise ValueError('build-proof '+key+' path mismatch')
    for key,sha in [('sourceSha256',SOURCE_HASHES['compress.c']),('patchlevelSha256',SOURCE_HASHES['patchlevel.h']),('decoderSha256',H(z.read_bytes()))]:
        if proof.get(key)!=sha:raise ValueError('build-proof '+key+' mismatch')
    if type(proof.get('compilerExitCode')) is not int or proof['compilerExitCode']!=0:
        raise ValueError('build-proof compiler did not succeed')
    if proof.get('adapter')!='none; original ncompress 5.1 has upstream MSVC/binary stdio support':
        raise ValueError('build-proof adapter mismatch')
    if proof.get('hostArchitecture') not in ('x64','arm64'):
        raise ValueError('build-proof host architecture invalid')
    arguments=proof.get('arguments')
    if not isinstance(arguments,list) or not arguments or any(not isinstance(x,str) for x in arguments) or proof['source'] not in arguments:
        raise ValueError('build-proof compiler arguments invalid')
    compiler=proof.get('compiler')
    if not isinstance(compiler,str) or not pathlib.Path(compiler).is_absolute():
        raise ValueError('build-proof compiler path invalid')
    # macOSのsystem compiler aliasは許容するが、探索・実行はしない。
    if proof.get('compilerSha256')!=H(pathlib.Path(compiler).read_bytes()):
        raise ValueError('build-proof compiler SHA mismatch')
    return z,{'buildProof':str(proof_path),'buildProofSha256':H(proof_path.read_bytes()),'sourceRoot':str(source_root),'sourceSha256':SOURCE_HASHES,'decoderSha256':proof['decoderSha256']}
def all_bzip(data):
    result=b''
    while data:
        d=bz2.BZ2Decompressor();result+=d.decompress(data)
        if not d.eof:raise ValueError('missing member footer')
        data=d.unused_data
    return result
def main():
    p=argparse.ArgumentParser();p.add_argument('--output');p.add_argument('--z-reference',required=True);p.add_argument('--reference-source');p.add_argument('--validate-reference-only',action='store_true');a=p.parse_args()
    z,proof=validate_reference(a.z_reference,a.reference_source)
    if a.validate_reference_only:
        print(json.dumps(proof));return
    if not a.output:p.error('--output is required unless --validate-reference-only is selected')
    out=pathlib.Path(a.output);out.mkdir(parents=True,exist_ok=True)
    tar=io.BytesIO()
    with tarfile.open(fileobj=tar,mode='w',format=tarfile.USTAR_FORMAT) as t:
        i=tarfile.TarInfo('leaf.txt');i.size=5;t.addfile(i,io.BytesIO(b'leaf\n'))
    archive=io.BytesIO()
    with zipfile.ZipFile(archive,'w',zipfile.ZIP_STORED) as f:
        i=zipfile.ZipInfo('leaf.txt',(1980,1,1,0,0,0));f.writestr(i,b'leaf\n')
    rows=[];files={}
    def add(name,data,decoded,valid=True,auto=0):
        files[name]=data;rows.append(dict(input=name,inputSHA256=H(data),inputBytes=len(data),valid=valid,autoExit=auto,decodedHex=decoded.hex(),decodedSHA256=H(decoded),decodedBytes=len(decoded)))
    payloads={'empty':b'','short':b'hello\n','zero512':bytes(512),'zero1024':bytes(1024),'tar':tar.getvalue(),'zip':archive.getvalue()}
    for id,raw in payloads.items():
        for ext in ('bz2','Z'):
            if ext=='bz2':data=bz2.compress(raw)
            else:
                compressed=subprocess.run([str(z),'-c'],input=raw,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
                assert compressed.returncode in (0,2) # official compress: 2 means no size saving
                data=compressed.stdout
            oracle=all_bzip(data) if ext=='bz2' else subprocess.run([str(z),'-d','-c'],input=data,stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True).stdout
            assert oracle==raw;add(id+'.'+ext,data,raw,auto=2 if id=='zero512' else 0)
    raw=b'hello\n';normal=bz2.compress(raw)
    add('concat.bz2',bz2.compress(b'')+normal+bz2.compress(b''),raw)
    bad=bytearray(normal);bad[-5]^=1
    for name,data in [('bad-tail.bz2',normal+bytes(bad)),('truncated.bz2',normal[:-4]),('invalid-header.bz2',b'BZh0invalid'),('invalid.Z',bytes.fromhex('1f9d88')+b'\xff\xff')]:add(name,data,b'',False,2)
    add('magic-mismatch.Z',normal,raw)
    add('no-extension',normal,raw)
    add('..bz2',normal,raw,False,2)
    inner=bz2.compress(raw);middle=bz2.compress(inner);files['chain.bz2']=bz2.compress(middle)
    mixed_tar=io.BytesIO()
    with tarfile.open(fileobj=mixed_tar,mode='w',format=tarfile.USTAR_FORMAT) as t:
        i=tarfile.TarInfo('inner.bz2');i.size=len(inner);t.addfile(i,io.BytesIO(inner))
    files['mixed.bz2']=bz2.compress(bz2.compress(mixed_tar.getvalue()))
    # root entry chain -> chain, next -> noname, final -> noname; each layer explicitly File.
    expected=json.dumps({'license':'CC0-1.0','cases':rows,'chainSHA256':H(files['chain.bz2']),'mixedSHA256':H(files['mixed.bz2'])},ensure_ascii=False,indent=2).encode()+b'\n'
    with zipfile.ZipFile(out/'inputs.zip','w',zipfile.ZIP_STORED) as f:
        for name,data in files.items():
            i=zipfile.ZipInfo(name,(1980,1,1,0,0,0));f.writestr(i,data)
    (out/'expected.json').write_bytes(expected)
    print(json.dumps({'inputs.zip':H((out/'inputs.zip').read_bytes()),'expected.json':H(expected),'cases':len(rows)}))
if __name__=='__main__':main()
