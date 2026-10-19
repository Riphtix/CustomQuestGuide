#!/usr/bin/env python3
"""Offline-only candidate discovery from CQG's quest flag audit.

This DOES NOT certify progression membership or enable runtime catalog entries.
Quest row identities are never deduplicated by title: variant rows are preserved.
"""
import argparse
import csv
from pathlib import Path

FIELDS = ('classJobId','classJob','questId','questName','classLevel','cqgRole',
          'auditApplicable','questState','referenceUrl','reviewedBy','reviewStatus','notes')

def stage(reader, class_job_id: int):
    results=[]
    for row in reader:
        if int(row['ClassJob ID']) != class_job_id:
            continue
        results.append({
            'classJobId':str(class_job_id), 'classJob':row['ClassJob'],
            'questId':row['Quest ID'], 'questName':row['Quest Name'],
            'classLevel':row['Class Level'], 'cqgRole':row['Quest Role'],
            'auditApplicable':row['Applicable'], 'questState':row['CQG Quest State'],
            'referenceUrl':'','reviewedBy':'','reviewStatus':'candidate',
            'notes':'Audit candidate only; corroborate membership, variant and quest ID before verifying.'
        })
    return results

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('audit',type=Path)
    ap.add_argument('output',type=Path)
    ap.add_argument('--class-job-id',type=int,required=True)
    a=ap.parse_args()
    with a.audit.open(encoding='utf-8-sig',newline='') as f:
        rows=stage(csv.DictReader(f),a.class_job_id)
    if not rows: ap.error('no matching ClassJob found')
    a.output.parent.mkdir(parents=True,exist_ok=True)
    with a.output.open('w',encoding='utf-8',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=FIELDS);writer.writeheader();writer.writerows(rows)
    print(f'Staged {len(rows)} UNVERIFIED quest rows for class/job {a.class_job_id}. No runtime catalog activated.')

if __name__=='__main__':main()
