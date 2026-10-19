#!/usr/bin/env python3
"""Verify actual candidate-package closure and full content protection.

Requires an already packed complete .tmp/local-feed. Run only when changing
consumer selection, not as a daily gate.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--feed', type=Path, default=ROOT / '.tmp/local-feed')
    parser.add_argument('--output', type=Path, default=ROOT / '.tmp/consumer-selection-evidence')
    parser.add_argument('--benchmark', action='store_true', help='Three alternating full/scoped consumer pairs')
    args = parser.parse_args()
    out = args.output.resolve(); out.mkdir(parents=True, exist_ok=True)
    graph = {}; packages = {}
    for path in args.feed.glob('*.nupkg'):
        with zipfile.ZipFile(path) as archive:
            xml = ET.fromstring(archive.read(next(name for name in archive.namelist() if name.endswith('.nuspec'))))
        metadata = next(node for node in xml if node.tag.split('}')[-1] == 'metadata')
        name = next(node.text for node in metadata if node.tag.split('}')[-1] == 'id')
        graph[name] = {node.attrib['id'] for node in metadata.iter() if node.tag.split('}')[-1] == 'dependency' and node.attrib['id'].startswith('Leistd.')}
        packages[name] = path
    assert len(graph) > 1, 'complete candidate feed required'
    plan_file = out / 'plan.json'
    subprocess.run([sys.executable,'scripts/plan-quality-checks.py','--tier','core','--output',str(plan_file)],cwd=ROOT,check=True,stdout=subprocess.DEVNULL)
    complete = json.loads(plan_file.read_text(encoding='utf-8'))
    assert complete['Tier'] == 'full' and complete['ConsumerProjects'] is None
    # Narrowed consumer plans exist only on the core tier: the core scenarios, one per execution group.
    spec = importlib.util.spec_from_file_location('planner', ROOT / 'scripts/plan-quality-checks.py')
    planner = importlib.util.module_from_spec(spec); spec.loader.exec_module(planner)
    scenarios = planner.coverage.load_scenarios()
    core = [name for name, info in scenarios.items() if 'core' in info['Slices']]
    def core_plan(**changes):
        value = dict(complete, Tier='core', Scenarios=core, ContainerSmoke=False, **changes)
        value['Slices'] = planner.execution_slices(scenarios, value['Scenarios'], 'core', value['Mode'], value['ContainerSmoke'])
        return value
    baseline = core_plan()
    records = []
    def run(label, plan, feed, success=True, diagnostic=None):
        plan_file.write_text(json.dumps(plan), encoding='utf-8')
        command = ['pwsh','-NoProfile','-File','framework/build/test-package-consumption.ps1','-FeedPath',str(feed),'-ValidationPlanPath',str(plan_file)]
        started=time.monotonic(); result=subprocess.run(command,cwd=ROOT,text=True, encoding='utf-8', errors='replace',capture_output=True)
        log=result.stdout+result.stderr; (out/(label+'.log')).write_text(log, encoding='utf-8')
        records.append(dict(label=label,seconds=round(time.monotonic()-started,3),exit_code=result.returncode))
        (out/'results.json').write_text(json.dumps(records,indent=2), encoding='utf-8')
        assert (result.returncode==0)==success,(label,log[-2500:])
        if diagnostic: assert diagnostic in log,(label,diagnostic,log[-2000:])
        print('PASS',label,flush=True)
        return log
    for seed in ['Leistd.Email.Smtp','Leistd.Email.Core']:
        expected={seed}
        while True:
            expanded=expected|{name for name,deps in graph.items() if deps&expected}
            if expanded==expected: break
            expected=expanded
        log=run(seed,core_plan(ConsumerProjects=[seed]),args.feed)
        assert f'selected {len(expected)} isolated consumers' in log
        assert f'Package consumption passed for {len(expected)} package(s)' in log
        (out/(seed+'-expected.json')).write_text(json.dumps(sorted(expected)), encoding='utf-8')
    content_only=core_plan(Mode='frontend',FrameworkTests=False,ConsumerProjects=[], FrameworkTestProjects=[], FrameworkTestSelection='none', PackageDocumentation=True, GeneratedDocumentation=False, Jobs=dict(complete['Jobs'], test=False))
    # Stage modes run the package job exactly when package documentation is in scope (quality-validation-plan.ps1).
    assert content_only['Jobs']['package-consumption'] == content_only['PackageDocumentation']
    log=run('template-inputs-content-only',content_only,args.feed)
    assert 'Consumer build not applicable' in log and 'Consumers.slnx' not in log
    documentation=dict(content_only,Mode='documentation',Scenarios=[],Slices=[],PackageDocumentation=True, Jobs={name:name in ('framework-pack','package-consumption') for name in baseline['Jobs']})
    log=run('documentation-without-empty-consumer-builds',documentation,args.feed)
    assert 'Consumer build not applicable' in log and 'Doc snippets compiled:' in log and 'Consumers.slnx' not in log
    proof=json.loads((ROOT/'.tmp/package-consumer/package-results.json').read_text(encoding='utf-8-sig'))
    assert proof['Consumers']==0 and proof['SnippetBlocks']>0 and proof['SnippetProjects']>0 and proof['Counterexamples']>0
    # Defects in an unselected package still block content-only and scoped modes.
    corrupt=out/'corrupt-feed'; corrupt.mkdir(exist_ok=True)
    for path in [*args.feed.glob('*.nupkg'), args.feed/'candidate.json']: shutil.copy2(path,corrupt/path.name)
    victim=corrupt/packages['Leistd.Core'].name
    original=victim.read_bytes()
    with zipfile.ZipFile(victim) as archive:
        entries={name:archive.read(name) for name in archive.namelist()}
    def rewrite(data):
        with zipfile.ZipFile(victim,'w',zipfile.ZIP_DEFLATED) as archive:
            for name,content in data.items(): archive.writestr(name,content)
    def restore():
        victim.write_bytes(original)
    rewrite({name:content for name,content in entries.items() if not (name.startswith('lib/') and name.endswith('.xml'))})
    run('unselected-xml-defect',content_only,corrupt,False,'missing XML documentation')
    restore()
    data=dict(entries); data['NuGet.md']=b'outdated readme'
    rewrite(data);run('stale-markdown',content_only,corrupt,False,'Markdown differs from candidate');restore()
    data=dict(entries); xml_name=next(name for name in data if name.startswith('lib/') and name.endswith('.xml'))
    data[xml_name]=b'<invalid'
    rewrite(data);run('malformed-xml',content_only,corrupt,False);restore()
    victim.unlink();run('missing-unselected-package',content_only,corrupt,False,'missing source packages');restore()
    data=dict(entries); nuspec=next(name for name in data if name.endswith('.nuspec'))
    text=data[nuspec].decode(); assert '<dependencies>' in text
    data[nuspec]=text.replace('<dependencies>','<dependencies><dependency id="Leistd.Missing.QualityFixture" version="[0.12.0]" />',1).encode()
    rewrite(data);run('missing-candidate-dependency',content_only,corrupt,False,'Candidate dependency missing');restore()
    run('unknown-consumer-seed',core_plan(ConsumerProjects=['Leistd.Unknown.QualityFixture']),args.feed,False,'Unknown consumer seed')
    run('full-cannot-narrow',dict(complete,ConsumerProjects=['Leistd.Email.Smtp']),args.feed,False,'Full-tier runtime plan may not narrow')
    run('restored-content-only',content_only,corrupt)
    if args.benchmark:
        for number in range(3):
            run(f'benchmark-full-{number+1}',complete,args.feed)
            run(f'benchmark-scoped-{number+1}',core_plan(ConsumerProjects=['Leistd.Email.Core']),args.feed)


if __name__ == '__main__': main()
