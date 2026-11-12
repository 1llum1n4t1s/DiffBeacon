"""固定Frhed原本・抽出・全goldenと製品NDJSONを独立照合する。出力を書かない。"""
import argparse
import base64
import hashlib
import json
from pathlib import Path


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', nargs='?', type=Path, help='製品のcaseId付きNDJSON。省略時は固定原本だけ照合')
    parser.add_argument('--fixture', type=Path, default=Path(__file__).resolve().parent)
    args = parser.parse_args()
    root = args.fixture.resolve()
    provenance = json.loads((root / 'provenance.json').read_bytes())
    manifest = json.loads((root / 'manifest.json').read_bytes())
    for source in provenance['sourceFiles']:
        data = (root / source['file']).read_bytes()
        require(len(data) == source['size'] and sha(data) == source['sha256'], source['file'])
    extracted = (root / 'original-functions.inc').read_bytes()
    require(sha(extracted) == provenance['extractedSha256'], 'extracted SHA')
    chunks = []
    for function in provenance['functions']:
        lines = (root / function['file']).read_bytes().splitlines(keepends=True)
        chunk = b''.join(lines[function['startLine'] - 1:function['endLine']])
        require(sha(chunk) == function['sha256'], str(function))
        chunks.append(chunk)
    require(b'\r\n'.join(chunks) == extracted, 'exact original extraction')
    actual = {}
    if args.output:
        for line in args.output.read_text(encoding='utf-8-sig').splitlines():
            if not line.strip():
                continue
            row = json.loads(line)
            ident = row['caseId']
            require(ident not in actual, 'duplicate case: ' + ident)
            actual[ident] = row
        require(set(actual) == {case['caseId'] for case in manifest['cases']}, 'case ID set')
    for case in manifest['cases']:
        ident = case['caseId']
        source = (root / case['inputFile']).read_bytes()
        expected = (root / case['expectedFile']).read_bytes()
        require(len(source) == case['inputLength'] and sha(source) == case['inputSha256'], ident + ' input')
        require(len(expected) == case['outputLength'] and sha(expected) == case['outputSha256'], ident + ' golden')
        process = case['process']
        require(process['exitCode'] == 0, ident + ' reference process')
        output = base64.b64decode(process['stdoutBase64'], validate=True).decode('ascii').splitlines()
        require(bytes.fromhex(output[0]) == expected, ident + ' stdout bytes')
        positions = list(map(int, output[1].split()))
        require(positions == [case['successfulCount'], case['legacyReportedCount'], case['expectedSelectionStart'], case['expectedSelectionLength'], case['expectedCaret']], ident + ' reference positions/count')
        if args.output:
            row = actual[ident]
            require(base64.b64decode(row['bytesBase64'], validate=True) == expected, ident + ' full bytes')
            require([row['replacementCount'], row['selectionStart'], row['selectionLength'], row['caret']] == [case['successfulCount'], case['expectedSelectionStart'], case['expectedSelectionLength'], case['expectedCaret']], ident + ' product positions/count')
    print(json.dumps(dict(success=True,referenceCases=len(manifest['cases']),productExecuted=bool(args.output),oracle='fixed Frhed MSVC process/full bytes/positions/successful count')))


if __name__ == '__main__':
    main()
