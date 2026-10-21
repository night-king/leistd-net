#!/usr/bin/env python3
"""Check documentation contracts on one generation shard's actual generated projects.

The four shards (three shape shards and the control shard) together cover the complete
generated input space; the summary checks that their receipts add up to it.
"""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--generation-root', type=Path, required=True)
    args = parser.parse_args()
    root = args.generation_root.resolve()
    assert root.is_relative_to((ROOT / '.tmp').resolve()), 'Generation proof must stay in .tmp'
    receipt = root / 'documentation-results.json'
    receipt.unlink(missing_ok=True)
    report = json.loads((root / 'generation-results.json').read_text(encoding='utf-8'))
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    assert report['CandidateSha'] == head
    sources = {p.relative_to(ROOT).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
               for p in sorted((ROOT / 'template').rglob('*')) if p.is_file()
               and not any(part in ('node_modules', 'bin', 'obj', '.cache') for part in p.relative_to(ROOT / 'template').parts)}
    assert sources == report['TemplateSourceDigests'], 'Generated proof differs from candidate template'
    spec = importlib.util.spec_from_file_location('generation', ROOT / 'scripts/test-template-generation.py')
    generation = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(generation)
    config = json.loads(generation.model.CONFIG_PATH.read_text(encoding='utf-8'))
    combinations = [v for v in generation.model.all_combinations(config) if v['Ci'] == config['symbols']['Ci']['defaultValue']]
    shapes = {generation.model.effective_shape(v) for v in combinations}
    assert report['EffectiveShapes'] == len(shapes) and report['ShapeShards'] == generation.SHAPE_SHARDS
    if report['Shard'] == 'control':
        shape_indexes = []
        ids = {item['Index'] for item in report['Representatives']}
        ids.update(item['variant'] for item in report['EquivalenceComparisons'])
        ids.update(item['variant'] for item in report['CiVariants'])
        assert len(report['CiVariants']) == 4 and report['RawInputs'] == len(combinations)
    else:
        shape_indexes = [item['Index'] for item in report['Shapes']]
        assert all(index % generation.SHAPE_SHARDS == int(report['Shard']) for index in shape_indexes) and shape_indexes
        ids = set(shape_indexes)
    assert {p.name for p in (root / 'generated').iterdir()} == {str(i) for i in ids}
    projects = [root / 'generated' / str(i) for i in sorted(ids)]
    projects_file = root / 'documentation-projects.json'
    projects_file.write_text(json.dumps([str(p) for p in projects]), encoding='utf-8')
    spec = importlib.util.spec_from_file_location('skill_validator', ROOT / 'scripts/vendor/skill-creator/quick_validate.py')
    validator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validator)
    skills, markdown = 0, 0
    for project in projects:
        docs = list(project.rglob('*.md'))
        assert docs, f'No documentation: {project}'
        markdown += len(docs)
        for skill in (project / '.agents/skills').iterdir():
            valid, message = validator.validate_skill(skill)
            assert valid, f'{skill}: {message}'
            skills += 1
    subprocess.run(['pwsh', str(ROOT / 'scripts/test-generated-documentation.ps1'), '-ProjectsPath', str(projects_file)], cwd=ROOT, check=True)
    pins = {re.search(r'"node_modules/prettier"\s*:\s*\{\s*"version"\s*:\s*"([^"]+)"', (ROOT / path).read_text(encoding='utf-8'))[1]
            for path in ('template/frontend/package-lock.json', 'template/.template.config/localization/frontend/package-lock.json')}
    assert len(pins) == 1
    version = pins.pop()
    tools = ROOT / '.tmp/documentation-tools'
    subprocess.run(['npm', 'install', '--prefix', str(tools), '--ignore-scripts', '--no-audit', '--no-fund', 'prettier@' + version], check=True, cwd=ROOT)
    formatting = root / 'markdown-format-results.json'
    subprocess.run(['node', str(ROOT / 'scripts/check-generated-markdown.mjs'), str(projects_file),
                    str(tools / 'node_modules/prettier/index.mjs'), version, str(formatting)], cwd=ROOT, check=True)
    stats = json.loads(formatting.read_text(encoding='utf-8'))
    assert markdown > 0 and skills > 0 and stats['Files'] > 0 and stats['UniqueContents'] > 0
    receipt.write_text(json.dumps(dict(Version=1, CandidateSha=head, Result='pass', Shard=report['Shard'], Projects=len(projects),
                                      ShapeIndexes=shape_indexes, EffectiveShapes=len(shapes),
                                      CiVariants=len(report['CiVariants']) if report['Shard'] == 'control' else 0, MarkdownFiles=markdown,
                                      Skills=skills, FrontendMarkdownFiles=stats['Files'], FormattedContents=stats['UniqueContents'])), encoding='utf-8')
    print(f"PASS generated documentation (shard {report['Shard']}): {len(projects)} projects, {markdown} Markdown files, {skills} skills")


if __name__ == '__main__':
    main()
