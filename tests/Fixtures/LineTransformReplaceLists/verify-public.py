"""公開CLIの間接serialized-key観測を照合する。製品を実行しない。"""
import sys
sys.dont_write_bytecode = True
import argparse
import json
import pathlib

BASE = pathlib.Path(__file__).resolve().parent
PLAN_FIELDS = {'id', 'expression', 'sourceLineCount', 'expectedOutcome', 'expectedSerializedKeys'}
OBSERVATION_FIELDS = {
    'id', 'expression', 'expectedExpression', 'expectedSerializedKeys', 'expectedError', 'passed',
    'optimizedExpression', 'optimizedPassed', 'optimizedExitCode', 'optimizedStdout', 'optimizedStderr',
    'actualTypedValueObserved', 'contextEquivalent', 'nativeExceptionEquivalent', 'exitCode', 'stdout', 'stderr'
}
OUTCOMES = ('serialized-key-comparison', 'runtime-rejection', 'compile-rejection', 'native-only')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate JSON property: ' + key)
        result[key] = value
    return result


def invalid_constant(value):
    raise ValueError('invalid JSON constant: ' + value)


def parse(raw):
    return json.loads(raw, object_pairs_hook=unique_object, parse_constant=invalid_constant)


def successful_comparison(exit_code, stdout, stderr, label):
    require(type(exit_code) is int and exit_code == 0, label + ': expected exit 0')
    require(type(stdout) is str and type(stderr) is str and stderr == '', label + ': invalid output/stderr')
    value = parse(stdout)
    require(type(value) is dict and value.get('different') is False, label + ': expected CLI JSON different=false')


def verify(actual_path):
    plan = parse((BASE / 'public-case-plan.json').read_bytes())
    actual = parse(actual_path.read_bytes())
    require(type(plan) is list and type(actual) is list, 'plan and actual must be arrays')
    counts = dict.fromkeys(OUTCOMES, 0)
    ids = set()
    for item in plan:
        require(type(item) is dict and set(item) == PLAN_FIELDS, 'plan must contain exactly five fields')
        require(type(item['id']) is str and item['id'] and item['id'] not in ids, 'invalid/duplicate plan id')
        ids.add(item['id'])
        require(type(item['expression']) is str and 'ReplaceWithList(' in item['expression'], item['id'] + ': invalid expression')
        require(type(item['sourceLineCount']) is int and item['sourceLineCount'] >= 0, item['id'] + ': invalid line count')
        require(item['expectedOutcome'] in OUTCOMES, item['id'] + ': invalid outcome')
        counts[item['expectedOutcome']] += 1
        keys = item['expectedSerializedKeys']
        require(type(keys) is list and all(type(key) is str for key in keys), item['id'] + ': invalid keys')
        for key in keys:
            decoded = bytes.fromhex(key).decode('utf8')
            require(decoded.encode('utf8').hex() == key, item['id'] + ': noncanonical UTF-8 hex')
        if item['expectedOutcome'] == 'serialized-key-comparison':
            require(len(keys) == item['sourceLineCount'] and item['sourceLineCount'] > 0, item['id'] + ': invalid comparable key count')
        else:
            require(keys == [], item['id'] + ': noncomparable keys must be empty')
    public = [item for item in plan if item['expectedOutcome'] != 'native-only']
    require(len(actual) == len(public), 'observation count differs from public plan')
    for item, observed in zip(public, actual, strict=True):
        label = item['id']
        require(type(observed) is dict and set(observed) == OBSERVATION_FIELDS, label + ': invalid observation fields')
        for field in ('id', 'expression', 'expectedSerializedKeys'):
            require(observed[field] == item[field], label + ': mismatched ' + field)
        for field in ('actualTypedValueObserved', 'contextEquivalent', 'nativeExceptionEquivalent'):
            require(observed[field] is False, label + ': unsupported equivalence claim: ' + field)
        rejection = item['expectedOutcome'] in ['runtime-rejection', 'compile-rejection']
        require(observed['passed'] is True and observed['expectedError'] is rejection, label + ': wrong baseline outcome')
        require(type(observed['stdout']) is str and type(observed['stderr']) is str, label + ': output must be strings')
        if rejection:
            require(observed['expectedExpression'] == 'Line', label + ': wrong rejection reference expression')
            require(type(observed['exitCode']) is int and observed['exitCode'] == 2 and observed['stdout'] == '' and observed['stderr'].strip(), label + ': expected atomic runtime rejection')
            require(observed['optimizedExpression'] is None and observed['optimizedPassed'] is None and observed['optimizedExitCode'] is None and observed['optimizedStdout'] == '' and observed['optimizedStderr'] == '', label + ': rejection must not claim optimized observation')
        else:
            quoted = ['"' + bytes.fromhex(key).decode('utf8').replace('"', '""') + '"' for key in item['expectedSerializedKeys']]
            expected = '@noopt Choose(LineNumber-1,' + ','.join(quoted) + ')'
            require(observed['expectedExpression'] == expected, label + ': wrong serialized-key reference expression')
            successful_comparison(observed['exitCode'], observed['stdout'], observed['stderr'], label + ' baseline')
            require(observed['optimizedExpression'] == item['expression'].replace('@noopt ', '') and observed['optimizedPassed'] is True, label + ': wrong optimized expression/outcome')
            successful_comparison(observed['optimizedExitCode'], observed['optimizedStdout'], observed['optimizedStderr'], label + ' optimized')
    return dict(passed=True, typedPublicEquivalence=False, planCounts=counts,
                serializedKeyComparisons=counts['serialized-key-comparison'],
                optimizedSerializedKeyComparisons=counts['serialized-key-comparison'],
                runtimeRejections=counts['runtime-rejection'], compileRejections=counts['compile-rejection'], nativeOnly=counts['native-only'],
                actualTypedValueObserved=False, contextEquivalent=False, nativeExceptionEquivalent=False,
                actualSerializedKeysDirectlyObserved=False)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--actual', type=pathlib.Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.actual), separators=(',', ':')))
