// devcheck 前端层：用 @vue/compiler-sfc 把 kachina 的所有 .vue 单文件组件编译一遍。
//
// 只装 @vue/compiler-sfc（不需要 kachina 的完整 node_modules，也不需要 vite/tauri），
// 就能抓住最常见的一类错误：template 语法错、<script setup> 里的语法错、
// 以及 bindings 解析失败。类型错误交给 tsc --noEmit（见 tsconfig.json）。
//
// 用法: node sfccheck.mjs [vue 文件所在的目录]
//       默认 <repo>/installer/kachina/src

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse, compileScript, compileTemplate } from '@vue/compiler-sfc';

const here = dirname(fileURLToPath(import.meta.url));
// tools/devcheck/front -> 仓库根
const repoRoot = resolve(here, '..', '..', '..');
const target = resolve(process.argv[2] ?? join(repoRoot, 'installer', 'kachina', 'src'));

/** 递归收集 .vue 文件 */
function collectVue(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === 'dist' || entry === '.cache') continue;
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) out.push(...collectVue(full));
    else if (entry.endsWith('.vue')) out.push(full);
  }
  return out;
}

const files = collectVue(target).sort();
let failed = 0;

for (const file of files) {
  const rel = file.slice(repoRoot.length + 1).replace(/\\/g, '/');
  const source = readFileSync(file, 'utf8');
  const problems = [];

  const { descriptor, errors } = parse(source, { filename: file });
  if (errors.length) {
    problems.push(...errors.map((e) => `parse: ${e.message ?? e}`));
  } else {
    const id = rel;
    let bindings;
    if (descriptor.script || descriptor.scriptSetup) {
      try {
        const script = compileScript(descriptor, { id });
        bindings = script.bindings;
      } catch (e) {
        problems.push(`script: ${e.message ?? e}`);
      }
    }
    if (descriptor.template) {
      try {
        const tpl = compileTemplate({
          source: descriptor.template.content,
          filename: file,
          id,
          compilerOptions: { bindingMetadata: bindings },
        });
        if (tpl.errors?.length) {
          problems.push(...tpl.errors.map((e) => `template: ${e.message ?? e}`));
        }
      } catch (e) {
        problems.push(`template: ${e.message ?? e}`);
      }
    }
  }

  if (problems.length) {
    failed++;
    console.log(`  FAIL ${rel}`);
    for (const p of problems) console.log(`       ${p}`);
  } else {
    console.log(`  ok   ${rel}`);
  }
}

console.log(`\nSFC 编译: ${files.length - failed}/${files.length} 通过`);
if (failed > 0 || files.length === 0) process.exit(1);
