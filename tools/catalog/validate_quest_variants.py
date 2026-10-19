#!/usr/bin/env python3
"""Validate staged class/job quest families, including alternative quest IDs.

This checks structure, unique IDs and source attribution; it never asserts that
community/official sites have been independently mapped to specific Lumina IDs.
"""
import argparse, csv, json, pathlib

VALID = {'unlock', 'progression', 'alternative', 'exclude'}

def validate(rows):
    errors = []
    ids = set()
    groups = {}
    for line, r in enumerate(rows, 2):
        try:
            job, quest = int(r['classJobId']), int(r['questId'])
            order = int(r['order']) if r.get('order') else 0
        except (KeyError, ValueError, TypeError):
            errors.append(f'line {line}: invalid identity/order')
            continue
        kind = r.get('kind', '')
        if job <= 0 or quest <= 0 or kind not in VALID:
            errors.append(f'line {line}: invalid ID or kind')
        if (job, quest) in ids:
            errors.append(f'line {line}: duplicate quest ID {quest}')
        ids.add((job, quest))
        if kind in ('progression', 'alternative') and order < 1:
            errors.append(f'line {line}: progression quests need positive order')
        if kind == 'alternative' and r.get('variantCondition') not in ('starting_class', 'not_starting_class'):
            errors.append(f'line {line}: alternative needs explicit starting-class condition')
        if kind != 'alternative' and r.get('variantCondition'):
            errors.append(f'line {line}: unexpected variant condition')
        if not r.get('referenceUrl', '').startswith('https://'):
            errors.append(f'line {line}: missing independent source URL')
        if kind in ('progression', 'alternative'):
            groups.setdefault((job, order), []).append(r)
    for (job, order), variants in groups.items():
        if len(variants) > 1:
            kinds = {r['kind'] for r in variants}
            conditions = {r.get('variantCondition', '') for r in variants}
            if kinds != {'alternative'} or conditions != {'starting_class', 'not_starting_class'} or len(variants) != 2:
                errors.append(f'job {job} step {order}: ambiguous alternatives')
    return errors

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('csv', type=pathlib.Path)
    args = p.parse_args()
    with args.csv.open(encoding='utf-8-sig', newline='') as f:
        rows = list(csv.DictReader(f))
    errors = validate(rows)
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Validated {len(rows)} staged records; eligibility branches are structurally explicit. NOT production-certified.')

if __name__ == '__main__': main()
