#!/usr/bin/env python3
"""Inject defects into actual generated docs, frontend registry and quality proofs."""
from __future__ import annotations
import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--generation-root', type=Path, required=True,
                        help='Directory holding the four generation shard roots 0, 1, 2 and control')
    parser.add_argument('--frontend-tools', type=Path, required=True)
    parser.add_argument('--feed', type=Path)
    args = parser.parse_args()
    output = ROOT / '.tmp/documentation-mutations'
    output.mkdir(parents=True, exist_ok=True)
    project = output / 'project'
    shards = ['0', '1', '2', 'control']
    roots = {shard: args.generation_root.resolve() / shard for shard in shards}
    source = roots['0'] / 'generated/0'
    projects_file = output / 'projects.json'
    projects_file.write_text(json.dumps([str(project)]), encoding='utf-8')
    def reset():
        if project.exists(): shutil.rmtree(project)
        shutil.copytree(source, project)
    records = []
    def run(label, command, succeeds=True):
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
        (output / (label + '.log')).write_text(result.stdout + result.stderr, encoding='utf-8')
        assert (result.returncode == 0) == succeeds, (label, (result.stdout + result.stderr)[-1500:])
        records.append(dict(Label=label, ExitCode=result.returncode))
        print('PASS defect detection:', label, flush=True)
    checks = ['pwsh', 'scripts/test-generated-documentation.ps1', '-ProjectsPath', str(projects_file)]
    reset(); run('generated-documents-valid', checks)
    for name, path, content in [
        ('missing-link', 'README.md', '\n[missing](docs/missing.md)\n'),
        ('missing-anchor', 'README.md', '\n[missing](README.md#missing-anchor)\n'),
        ('template-residue', 'README.md', '\nCompanyName.ProjectName\n'),
        ('ai-entry', 'CLAUDE.md', '@missing.md\n')]:
        reset()
        target = project / path
        target.write_text(content if name == 'ai-entry' else target.read_text(encoding='utf-8') + content, encoding='utf-8')
        run(name, checks, False)
    reset()
    skill = project / '.agents/skills/leistd-project-workflow/SKILL.md'
    skill.write_text(skill.read_text(encoding='utf-8').replace('---\n', '---\nunknown-field: true\n', 1), encoding='utf-8')
    run('skill-metadata', [sys.executable, 'scripts/vendor/skill-creator/quick_validate.py', str(skill.parent)], False)
    reset()
    frontend = project / 'frontend/README.md'
    frontend.write_text(frontend.read_text(encoding='utf-8') + '\n\n\n', encoding='utf-8')
    pin = re.search(r'"node_modules/prettier"\s*:\s*\{\s*"version"\s*:\s*"([^"]+)"', (ROOT / 'template/frontend/package-lock.json').read_text(encoding='utf-8'))[1]
    formatter = ['node', 'scripts/check-generated-markdown.mjs', str(projects_file),
                 str(ROOT / '.tmp/documentation-tools/node_modules/prettier/index.mjs'), pin, str(output / 'format.json')]
    run('generated-frontend-format', formatter, False)
    reset(); run('generated-frontend-format-restored', formatter)
    spec = importlib.util.spec_from_file_location('using_guards', ROOT / 'scripts/check-using-guards.py')
    guards = importlib.util.module_from_spec(spec); spec.loader.exec_module(guards)
    fixture = output / 'empty-block'
    (fixture / 'template').mkdir(parents=True, exist_ok=True)
    (fixture / 'template/README.md').write_text('<!--#if (SpaFrontend) -->\n\n<!--#endif -->\n', encoding='utf-8')
    guards.ROOT = str(fixture)
    assert guards.check_no_empty_conditional_blocks() == 1
    (fixture / 'template/README.md').write_text('plain Markdown\n', encoding='utf-8')
    assert guards.check_no_empty_conditional_blocks() == 0
    records.append(dict(Label='markdown-empty-conditional-block', ExitCode=0))
    # Read actual pinned dependencies; mutate only a private copy of gate inputs.
    registry = output / 'registry'
    if registry.exists(): shutil.rmtree(registry)
    (registry / 'scripts').mkdir(parents=True)
    shutil.copy2(ROOT / 'scripts/check-spartan-customizations.mjs', registry / 'scripts/check-spartan-customizations.mjs')
    shutil.copytree(ROOT / 'template', registry / 'template', ignore=shutil.ignore_patterns('node_modules', 'bin', 'obj'))
    gate = ['node', str(registry / 'scripts/check-spartan-customizations.mjs'), str(args.frontend_tools.resolve())]
    run('spartan-registry-valid', gate)
    document = registry / 'template/docs/standards/frontend-spartan.md'
    text = document.read_text(encoding='utf-8')
    heading = text.index('已定制的 helm 组件')
    lines = text[heading:].splitlines(keepends=True)
    rows = [i for i, line in enumerate(lines) if line.lstrip().startswith('|')]
    assert len(rows) > 2
    del lines[rows[2]]
    document.write_text(text[:heading] + ''.join(lines), encoding='utf-8')
    run('markdown-only-spartan-registry-defect', gate, False)
    plan_file = output / 'quality-plan.json'
    run('proof-plan', [sys.executable, 'scripts/plan-quality-checks.py', '--tier', 'core', '--output', str(plan_file)])
    baseline = json.loads(plan_file.read_text(encoding='utf-8'))
    documentation_plan = dict(baseline, Tier='core', Mode='documentation', FrameworkTests=False, FrameworkTestProjects=[],
                              FrameworkTestSelection='none', ConsumerProjects=[], Scenarios=[], Slices=[], ContainerSmoke=False,
                              GeneratedDocumentation=True, PackageDocumentation=False,
                              Jobs={name:name == 'template-generation' for name in baseline['Jobs']})
    plan_file.write_text(json.dumps(documentation_plan), encoding='utf-8')
    proofs = output / 'proofs'
    receipts = {shard: json.loads((roots[shard] / 'documentation-results.json').read_text(encoding='utf-8')) for shard in shards}
    def write_proofs(values):
        if proofs.exists(): shutil.rmtree(proofs)
        for index, value in enumerate(values):
            (proofs / f'generated-documentation-proof-{index}').mkdir(parents=True)
            (proofs / f'generated-documentation-proof-{index}' / 'documentation-results.json').write_text(json.dumps(value), encoding='utf-8')
    def mutated(shard, field, value):
        values = json.loads(json.dumps(receipts))
        if value is None: values[shard].pop(field)
        else: values[shard][field] = value
        return list(values.values())
    checker = ['pwsh', 'scripts/check-quality-proof.ps1', '-ValidationPlanPath', str(plan_file), '-Kind', 'documentation', '-ResultsPath', str(proofs)]
    write_proofs(receipts.values()); run('documentation-proof-valid', checker)
    for shard, field, value in [('control', 'CandidateSha', 'a' * 40), ('0', 'Version', 2), ('1', 'Result', 'cancelled'), ('2', 'Result', 'failure'),
                                ('control', 'EffectiveShapes', 0), ('control', 'CiVariants', 3), ('0', 'CiVariants', 1), ('1', 'Projects', 0),
                                ('2', 'MarkdownFiles', 0), ('control', 'Skills', 0), ('0', 'FrontendMarkdownFiles', 0), ('1', 'FormattedContents', 0),
                                ('2', 'ShapeIndexes', []), ('control', 'ShapeIndexes', [0]), ('0', 'Shard', '1'), ('1', 'Shard', None)]:
        write_proofs(mutated(shard, field, value))
        run(f'invalid-proof-{shard}-{field}-{value}', checker, False)
    duplicated = json.loads(json.dumps(receipts)); duplicated['1']['ShapeIndexes'] = duplicated['1']['ShapeIndexes'][1:] + duplicated['0']['ShapeIndexes'][:1]
    write_proofs(duplicated.values()); run('invalid-proof-duplicate-shape-index', checker, False)
    for shard in shards:
        write_proofs([value for name, value in receipts.items() if name != shard]); run(f'missing-proof-shard-{shard}', checker, False)
    write_proofs([*receipts.values(), receipts['0']]); run('duplicate-proof', checker, False)
    # The actual generation proofs of the same shards satisfy the release summary's generation check.
    generation_proofs = output / 'generation-proofs'
    if generation_proofs.exists(): shutil.rmtree(generation_proofs)
    for shard in shards:
        (generation_proofs / shard).mkdir(parents=True)
        shutil.copy2(roots[shard] / 'generation-results.json', generation_proofs / shard / 'generation-results.json')
    run('generation-proof-valid', ['pwsh', 'scripts/check-quality-proof.ps1', '-ValidationPlanPath', str(plan_file),
                                   '-Kind', 'generation', '-ResultsPath', str(generation_proofs)])
    if args.feed:
        fixture = output / 'package-fixture'
        if fixture.exists(): shutil.rmtree(fixture)
        shutil.copytree(ROOT / 'framework', fixture / 'framework', ignore=shutil.ignore_patterns('bin', 'obj', 'artifacts'))
        (fixture / 'scripts').mkdir()
        for name in ['quality-validation-plan.ps1', 'template-matrix-scenarios.ps1', 'extract-doc-snippets.py']:
            shutil.copy2(ROOT / 'scripts' / name, fixture / 'scripts' / name)
        feed = fixture / '.tmp/feed'; shutil.copytree(args.feed, feed)
        document = fixture / 'framework/docs/components/email.md'
        text = document.read_text(encoding='utf-8')
        assert 'builder.Services.AddNullEmailSender();' in text
        document.write_text(text.replace('builder.Services.AddNullEmailSender();', 'builder.Services.MissingDocumentationFixture();', 1), encoding='utf-8')
        for package in feed.glob('*.nupkg'):
            with zipfile.ZipFile(package) as archive:
                if 'docs/email.md' not in archive.namelist(): continue
                entries = {name:archive.read(name) for name in archive.namelist()}
            entries['docs/email.md'] = document.read_bytes()
            with zipfile.ZipFile(package, 'w', zipfile.ZIP_DEFLATED) as archive:
                for name, data in entries.items(): archive.writestr(name, data)
        package_plan = dict(documentation_plan, GeneratedDocumentation=False, PackageDocumentation=True,
                            Jobs={name:name in ('framework-pack', 'package-consumption') for name in baseline['Jobs']})
        plan_file.write_text(json.dumps(package_plan), encoding='utf-8')
        run('uncompilable-packaged-markdown', ['pwsh', str(fixture / 'framework/build/test-package-consumption.ps1'),
                                             '-FeedPath', str(feed), '-ValidationPlanPath', str(plan_file)], False)
        log = (output / 'uncompilable-packaged-markdown.log').read_text(encoding='utf-8')
        assert 'Consumer build not applicable' in log and 'MissingDocumentationFixture' in log and 'error CS1061' in log
    (output / 'results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
