const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const ts = require('typescript');
const path = require('node:path');
const code = ts.transpileModule(fs.readFileSync(path.join(__dirname, '../src/use-page-transition.ts'),'utf8'), {compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText;
function setup({reduced=false, supported=true, hidden=false}={}) {
  let state='home', cleanup;
  const transitions=[], preference={matches:reduced,addEventListener(){},removeEventListener(){}};
  const document={hidden,documentElement:{dataset:{}},querySelector(){return {scrollTo(){}}},addEventListener(){},removeEventListener(){}};
  if(supported) document.startViewTransition=commit=>{
    let finish;
    const transition={commit,skipped:false,skipTransition(){this.skipped=true},ready:Promise.resolve(),finished:new Promise(resolve=>finish=resolve),finish(){finish()}};
    transitions.push(transition);return transition;
  };
  const context={exports:{},document,window:{matchMedia:()=>preference},require(name){return name==='react'?{useRef:v=>({current:v}),useState:v=>[v,next=>state=next],useEffect:effect=>cleanup=effect()}:{flushSync:fn=>fn()}}};
  vm.runInNewContext(code,context);
  const [,navigate]=context.exports.usePageTransition('home');
  return {navigate,transitions,document,get state(){return state},cleanup:()=>cleanup()};
}
(async()=>{
  const rapid=setup();rapid.navigate('music');rapid.navigate('cards');rapid.navigate('home');
  assert.equal(rapid.transitions[0].skipped,true);
  rapid.transitions[2].commit();rapid.transitions[0].commit();rapid.transitions[1].commit();
  assert.equal(rapid.state,'home');
  rapid.navigate('home');assert.equal(rapid.transitions.length,3);
  rapid.transitions[2].finish();await Promise.resolve();
  assert.equal(rapid.document.documentElement.dataset.pageTransition,'native');
  for(const options of [{reduced:true},{supported:false},{hidden:true}]){
    const env=setup(options);env.navigate('settings');assert.equal(env.state,'settings');assert.equal(env.transitions.length,0);
  }
  const unmount=setup();unmount.navigate('music');unmount.cleanup();unmount.transitions[0].commit();assert.equal(unmount.state,'home');
  const failed=setup();failed.document.startViewTransition=()=>{throw Error('unavailable')};failed.navigate('cards');assert.equal(failed.state,'cards');
  console.log('PASS: latest navigation wins, duplicate clicks, no fallback replay, reduced motion, missing API, hidden window, unmount, synchronous API failure');
})().catch(error=>{console.error(error);process.exitCode=1});
