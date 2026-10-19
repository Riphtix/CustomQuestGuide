#!/usr/bin/env python3
"""Offline CQG class/job catalog compiler.

Uses reviewed quest IDs, never inference from names/class affinity. The producer
MUST independently review progression membership and applicability; this tool
checks data integrity, not game-world truth.
"""
import argparse
import csv
import json
import pathlib
import sys

REQUIRED = ('classJobId', 'questId', 'order', 'referenceUrl', 'reviewedBy', 'reviewStatus')

def compile_rows(rows):
    groups = {}
    issues = []
    for line, row in enumerate(rows, 2):
        try:
            job = int(row['classJobId'])
            quest = int(row['questId'])
            order = int(row['order'])
        except (KeyError, ValueError, TypeError) as ex:
            issues.append(f'line {line}: invalid numeric identity: {ex}')
            continue
        if min(job, quest, order) <= 0:
            issues.append(f'line {line}: identifiers and order must be positive')
        if row.get('reviewStatus', '').strip().lower() != 'verified':
            issues.append(f'line {line}: quest {quest} not verified')
        if not row.get('referenceUrl', '').strip().startswith('https://'):
            issues.append(f'line {line}: quest {quest} missing HTTPS reference')
        if not row.get('reviewedBy', '').strip():
            issues.append(f'line {line}: quest {quest} missing reviewer')
        groups.setdefault(job, []).append((order, quest, line))
    output = []
    for job, entries in sorted(groups.items()):
        orders = [e[0] for e in entries]
        ids = [e[1] for e in entries]
        if len(orders) != len(set(orders)):
            issues.append(f'job {job}: duplicate order')
        if len(ids) != len(set(ids)):
            issues.append(f'job {job}: duplicate quest IDs')
        if sorted(orders) != list(range(1, len(orders) + 1)):
            issues.append(f'job {job}: order must be contiguous starting at 1')
        output.append({'classJobId': job, 'verifiedComplete': True,
                       'progressionQuestIds': [q for _, q, _ in sorted(entries)]})
    if issues:
        raise ValueError('\n'.join(issues))
    return {'schemaVersion': 1, 'jobs': output}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('csv', type=pathlib.Path)
    parser.add_argument('output', type=pathlib.Path)
    args = parser.parse_args()
    with args.csv.open(newline='', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        if not reader.fieldnames or any(k not in reader.fieldnames for k in REQUIRED):
            parser.error('CSV must have columns: ' + ', '.join(REQUIRED))
        try:
            result = compile_rows(list(reader))
        except ValueError as ex:
            parser.error(str(ex))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(f'Generated {len(result["jobs"])} verified job entries → {args.output}')

if __name__ == '__main__':
    main()
