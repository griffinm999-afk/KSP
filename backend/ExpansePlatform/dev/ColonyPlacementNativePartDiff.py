"""Read-only comparison of a real loaded part witness and detached stock consumption.

Writes a bounded audit report under the workspace; never edits an installation.
The expected input is produced with native ConfigNode.LoadObjectFromConfig by the
package interoperability test runner. No substitutions are applied to resources,
MODULE terms, physics, attachment geometry, or economics in this report.
"""
import argparse, hashlib, json
from pathlib import Path
from ColonyPlacementTemplates import parse, children, val, serialize


def flatten(node, path=''):
    result = {}
    for key, values in node['v'].items(): result[path + '/' + key] = values
    count = {}
    for child in node['c']:
        if child['tag'] == 'MODULE' and val(child, 'name') == 'ColonyPlacementMarker':
            if child['v'] != {'name': ['ColonyPlacementMarker']} or child['c']:
                raise ValueError('Owned marker has unexpected terms')
            continue
        tag = child['tag'].split(' //', 1)[0]; index = count.get(tag, 0); count[tag] = index + 1
        result.update(flatten(child, f'{path}/{tag}[{index}]'))
    return result


def main():
    p = argparse.ArgumentParser(); p.add_argument('expected'); p.add_argument('actual'); p.add_argument('output'); args = p.parse_args()
    expected_bytes, actual_bytes = Path(args.expected).read_bytes(), Path(args.actual).read_bytes()
    expected = {val(n, 'requestedName'): n for n in children(parse(expected_bytes.decode('utf-8-sig')), 'AVAILABLE_PART')}
    root = parse(actual_bytes.decode('utf-8-sig'))
    witness = children(root, 'COLONY_LOADED_PART_DATABASE_WITNESS')[0]
    actual = {val(n, 'requestedName'): n for n in children(witness, 'AVAILABLE_PART')}
    if set(expected) != set(actual): raise ValueError('Expected and actual selected identities differ')
    rows = []
    for name in sorted(expected):
        left, right = flatten(children(expected[name], 'PART')[0]), flatten(children(actual[name], 'PART')[0])
        diffs = [{'Path': k, 'Expected': left.get(k), 'Actual': right.get(k)} for k in sorted(set(left) | set(right)) if left.get(k) != right.get(k)]
        economics = [{'Field': k, 'Expected': val(expected[name], k), 'Actual': val(actual[name], k)} for k in ('loadedName', 'techRequired', 'cost', 'rawEntryCost') if val(expected[name], k) != val(actual[name], k)]
        rows.append({'PartName': name, 'RetainedTermDifferences': diffs, 'MetadataDifferences': economics})
    report = {'OperationId': val(witness, 'operationId'), 'ObservedUt': val(witness, 'observedUt'),
              'ComparisonLimit': 'Expected economic cost is before stock resource-cost sanitation. Readable ConfigNode text can lose literal localized newlines; future canonical base64 witnesses retain exact native terms.',
              'ExpectedSha256': hashlib.sha256(expected_bytes).hexdigest(), 'NativeWitnessSha256': hashlib.sha256(actual_bytes).hexdigest(),
              'SelectedPartCount': len(rows), 'Parts': rows}
    output = Path(args.output).resolve(); workspace = Path(__file__).resolve().parents[2]
    if not output.is_relative_to(workspace): raise ValueError('Audit output leaves workspace')
    output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f"parts={len(rows)} changed={sum(bool(r['RetainedTermDifferences'] or r['MetadataDifferences']) for r in rows)} report={output}")
    for row in rows:
        if row['RetainedTermDifferences'] or row['MetadataDifferences']:
            print(row['PartName'], json.dumps(row['RetainedTermDifferences'] + row['MetadataDifferences']))


if __name__ == '__main__': main()
