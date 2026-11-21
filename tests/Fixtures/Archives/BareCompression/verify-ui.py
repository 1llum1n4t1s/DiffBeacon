"""Independent run evidence reader. No product libraries or reflection are used."""
import argparse, hashlib, io, itertools, json, pathlib, struct, tarfile, zipfile, zlib

def png(path):
    data=path.read_bytes(); assert data[:8]==b'\x89PNG\r\n\x1a\n',path
    pos=8; payload=b''; width=height=0; ended=False
    while pos<len(data):
        length=struct.unpack('>I',data[pos:pos+4])[0];kind=data[pos+4:pos+8];body=data[pos+8:pos+8+length]
        assert zlib.crc32(kind+body)&0xffffffff==struct.unpack('>I',data[pos+8+length:pos+12+length])[0],path
        if kind==b'IHDR':width,height=struct.unpack('>II',body[:8])
        if kind==b'IDAT':payload+=body
        if kind==b'IEND':ended=True
        pos+=12+length
    assert ended and pos==len(data) and width>0 and height>0 and zlib.decompress(payload),path
    return width,height

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--run',type=pathlib.Path,required=True);a=ap.parse_args();r=a.run
    proof=json.loads((r/'proof.json').read_text());oracle=json.loads((r/'expected.json').read_text())
    fixed_expected=(pathlib.Path(__file__).parent/'expected.json').read_bytes()
    assert hashlib.sha256(fixed_expected).hexdigest().upper()=='C606FD0E63C6D62AFD3AC220CC023D1E2D8ECC07ECF4E741AF55EF13E4022DA6'
    assert oracle==json.loads(fixed_expected)
    before=json.loads((r/'shared-project-before.json').read_text());after=json.loads((r/'shared-project-after.json').read_text())
    state=json.loads((r/'shared-state-proof.json').read_text())
    assert before==after and state['restoredExactly'] and state['primaryFailure'] is None
    for field in ('Width','Height','CompareEnabled','HasUnsavedChanges','LeftArchiveInput','RightArchiveInput'):
        assert state['before'+field]==state['after'+field],field
    assert state['afterCompareEnabled'] and not state['afterHasUnsavedChanges']
    for side in ('left','right'):
        assert state['after'+side.title()+'ArchiveInput']==(after.get(side+'ArchiveInput') is not None)
    assert proof['retainedStates'] and all(s==proof['baselineState'] for s in proof['retainedStates'])
    assert proof['adoptionGateCalls']==1 and proof['oldCandidateGateCalls']==1
    required={'preview-bad-tail-max1','choice-conflict','initial-retry-bz2','initial-retry-Z','cancel-keeps-display','stale-choice-reverted','failed-tar-keeps-display','cancel-at-adoption','old-candidate-discarded','workspace-v9','workspace-v9-reload','input-output-protected','child-gui-settings','child-gui-recompare','child-gui-working-modes','browser-owner','browser-layer-propagation','browser-sides-independent','working-prefix-ancestor','legacy-gzip-v8','identity-auto-legacy'}
    actual={row['id'] for row in proof['cases']};assert required<=actual,(required-actual)
    assert all(row['passed'] for row in proof['cases'])
    root=json.loads((r/'root-write-proof.json').read_text());matrix=set(itertools.product(('bz2','Z'),('left','right'),('File','Auto','Tar')))
    assert root['schemaVersion']==1 and root['matrixCases']==12 and root['producerChecks']==36 and len(root['cases'])==12
    assert {(item['codec'],item['side'],item['kind']) for item in root['cases']}==matrix
    def relative_path(value):
        path=pathlib.PurePosixPath(value)
        assert value and '\\' not in value and not path.is_absolute() and '..' not in path.parts and ':' not in value,value
        return r.joinpath(*path.parts)
    for item in root['cases']:
        codec,side,kind=item['codec'],item['side'],item['kind'];name='tar.'+codec
        spec=next(case for case in oracle['cases'] if case['input']==name);raw=bytes.fromhex(spec['decodedHex'])
        expected={'tar':raw}
        if kind!='File':
            with tarfile.open(fileobj=io.BytesIO(raw),mode='r:') as tar:
                members=tar.getmembers();assert all(member.isfile() for member in members)
                expected={member.name:tar.extractfile(member).read() for member in members}
                assert len(expected)==len(members)
        assert item['source']==name and item['adoptedKind']==kind
        assert item['beforeSha256']==item['afterSha256']==spec['inputSHA256']==hashlib.sha256(relative_path(name).read_bytes()).hexdigest().upper()
        assert item['rows']==item['relativePaths']==sorted(expected)
        for route in ('adopt','repack','extract'):assert 'root-'+route+'-'+codec+'-'+side+'-'+kind in actual
        with zipfile.ZipFile(relative_path(item['zip'])) as archive:
            assert archive.testzip() is None
            entries=archive.infolist();assert len(entries)==len(expected) and sorted(entry.filename for entry in entries)==sorted(expected)
            for entry in entries:
                content=archive.read(entry);assert content==expected[entry.filename]
                assert entry.file_size==len(content) and entry.CRC==zlib.crc32(content)&0xffffffff
        directory=relative_path(item['extract']);assert directory.is_dir() and not directory.is_symlink()
        files=list(directory.rglob('*'));assert all(not file.is_symlink() for file in files)
        assert sorted(file.relative_to(directory).as_posix() for file in files if file.is_file())==sorted(expected)
        for name,content in expected.items():assert directory.joinpath(*pathlib.PurePosixPath(name).parts).read_bytes()==content
    assert {'source-retry-owned-button','button-text-save-clean','button-text-modes-reload','button-binary-save-clean','button-binary-modes-reload','button-save-originals'} <= actual
    assert (r/'button-text-auto.bin').read_bytes()==b'GUI auto saved\n'
    assert (r/'button-text-file.bin').read_bytes()==b'GUI file saved\n'
    short=next(spec for spec in oracle['cases'] if spec['input']=='short.Z')
    expected_binary=bytearray.fromhex(short['decodedHex']);expected_binary[0]^=0xff
    assert (r/'button-binary.bin').read_bytes()==expected_binary
    saved=json.loads((r/'button-workspace-v9.json').read_text()); assert saved['formatVersion']==9
    for project in saved['entries']:
        assert project['leftReadOnly'] is True and project['rightReadOnly'] is True
        for side in ('leftArchiveInput','rightArchiveInput'):
            source=project.get(side)
            if not source:continue
            assert source.get('inheritedReadOnly') is False
            for snap in source.get('workingTexts',[]):
                asset=pathlib.Path(snap['snapshotPath'])
                assert not asset.is_absolute() and '..' not in asset.parts
                content=(r/asset).read_bytes(); assert hashlib.sha256(content).hexdigest().upper()==snap['sha256'].upper()
                if project['mode']=='Text':assert content==(b'GUI auto saved\n' if side=='leftArchiveInput' else b'GUI file saved\n')
                elif project['mode']=='Binary': assert side=='leftArchiveInput' and content==expected_binary

    for spec in oracle['cases']:
        original=(r/spec['input']).read_bytes();assert hashlib.sha256(original).hexdigest().upper()==spec['inputSHA256']
        if spec['valid']:assert (r/(spec['input']+'.decoded.bin')).read_bytes()==bytes.fromhex(spec['decodedHex'])
    assert hashlib.sha256((r/'chain.bz2').read_bytes()).hexdigest().upper()==oracle['chainSHA256']
    assert hashlib.sha256((r/'mixed.bz2').read_bytes()).hexdigest().upper()==oracle['mixedSHA256']
    for spec in proof['originals']:
        assert spec['beforeSha256']==spec['afterSha256']==hashlib.sha256((r/spec['file']).read_bytes()).hexdigest().upper()
    working=json.loads((r/'working-proof.json').read_text());assert working['autoRevision']!=working['fileRevision']
    assert (r/'working-auto.bin').read_bytes()==bytes.fromhex(working['autoHex'])==b'auto working\n'
    assert (r/'working-file.bin').read_bytes()==bytes.fromhex(working['fileHex'])==b'file working\n'
    assert (r/'chain-leaf.bin').read_bytes()==b'hello\n'
    browser=json.loads((r/'browser-proof.json').read_text());assert browser['leaf']=='inner' and browser['entryChain']==['mixed','inner.bz2'] and browser['compressionKinds']==['File','Tar','File'] and browser['readOnly']
    traces=[json.loads(line) for line in (r/'browser-operations.ndjson').read_text().splitlines()]
    opens=[row for row in traces if row['stage']=='before-open']
    opened=[row for row in traces if row['stage']=='after-open']
    assert len(opens)==len(opened)==2 and all(row['openEnabled'] for row in opens)
    assert len(opened[0]['rows'])==1 and opened[0]['rows'][0].startswith('inner.bz2 （')
    assert len(opened[1]['rows'])==1 and opened[1]['rows'][0].startswith('inner （')
    assert browser['parentDirtyBeforeCancel'] and browser['parentDirtyTextBefore']==browser['parentDirtyTextAfter'] and 'pending edit' in browser['parentDirtyTextBefore']
    workspace=json.loads((r/'workspace-v9.json').read_text());assert workspace['formatVersion']==9 and len(workspace['entries'])==1
    project=workspace['entries'][0];left=project['leftArchiveInput']
    assert left is not None and left['containerCompressionPayloadKinds']==['File'] and project['leftReadOnly'] is True
    assert pathlib.Path(left['rootPath']).resolve()==(r/'short.Z').resolve() and not project['leftPath']
    assert project.get('rightArchiveInput') is None and pathlib.Path(project['rightPath']).resolve()==(r/'short.Z').resolve()
    assert type(proof['baselineRightReadonly']) is bool and project['rightReadOnly'] is proof['baselineRightReadonly']
    legacy=(r/'legacy-gzip-v8.json').read_text();assert json.loads(legacy)['formatVersion']==8 and 'containerCompressionPayloadKinds' not in legacy
    for path in sorted(r.glob('*bounds-*.json')):
        layout=json.loads(path.read_text()); assert len(layout['controls'])>=5
        for c in layout['controls']:
            assert c['y']>=0 and c['y']+c['height']<=layout['height'] and c['width']>0,(path,c)
            if c.get('list') or c.get('name')=='archive-entries':assert c['height']>=100,(path,c)
            if 'png' in c:
                images=list(r.parents[2].rglob(c['png']));assert len(images)==1,images;png(images[0])
        if path.name.startswith('browser'):assert png(r/('browser-'+str(int(layout['width']))+'.png'))==(int(layout['width']),int(layout['height']))
        else:
            candidates=list(r.parents[2].rglob(layout['png']));assert len(candidates)==1,candidates;png(candidates[0])
    print(json.dumps({'passed':True,'cases':len(actual),'originals':len(proof['originals']),'decodedCases':sum(s['valid'] for s in oracle['cases']),'rootWriteMatrixCases':12,'rootWriteProducerChecks':36,'sharedProjectRestored':True,'sharedPhysicalInputs':not state['afterLeftArchiveInput'] and not state['afterRightArchiveInput']}))
if __name__=='__main__':main()
