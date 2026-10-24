// Exercise the shipped UI/bridge protocol with a synthetic profile, without a file picker/network.
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),crypto=require('node:crypto');
const core=require('./profile-core.js'),{fixture}=require('./core-checks.cjs');
const {file,identity}=fixture(),original=Buffer.from(file);let working=Buffer.from(file),pending=null,expected=null,passed=false;
const nodes={};for(const match of fs.readFileSync(__dirname+'/index.html','utf8').matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g))nodes[match[2]]={tagName:match[1],value:'',textContent:'',innerHTML:'',hidden:false,disabled:false,checked:false,prepend(){}};
for(const [id,value] of Object.entries({filter:'locked',sort:'id',gameSort:'az',mode:'offline',spacing:'1'}))nodes[id].value=value;
const hash=b=>crypto.createHash('sha1').update(b).digest('hex');
const context={console,TextEncoder,Uint8Array,Date,atob,btoa,URL,Number,Set,Map,BigInt,document:{getElementById:id=>nodes[id],createElement:()=>({}),querySelectorAll:()=>Object.values(nodes).filter(n=>n.tagName==='button')},window:{},Labs360:core};
context.window.Labs360Native={call(id,action,json){Promise.resolve().then(async()=>{const p=JSON.parse(json);let result={};switch(action){
 case 'testProfile':result={length:working.length};break;
 case 'pickProfile':throw Error('Synthetic UI check unexpectedly opened a second picker');
 case 'readChunk':result={base64:working.subarray(p.offset,p.offset+32768).toString('base64')};break;
 case 'identity':result={base64:identity.toString('base64')};break;
 case 'art':break;
 case 'beginSave':assert.equal(p.before,hash(working));pending=Buffer.alloc(0);expected=p;break;
 case 'saveChunk':assert.equal(p.offset,pending.length);pending=Buffer.concat([pending,Buffer.from(p.base64,'base64')]);break;
 case 'commitSave':assert.equal(pending.length,expected.length);assert.equal(hash(pending),expected.sha1);working=pending;pending=null;break;
 case 'abortSave':pending=null;break;
 case 'testDone':assert.equal(p.passed,true,p.error);assert.deepEqual(original,file);assert.notDeepEqual(original,working);assert.equal((await core.Profile.open(working)).list('00000001')[0].unlocked,true);passed=true;break;
 default:throw Error('Unexpected bridge action '+action);
 }context.window.native360Result(id,result,null);}).catch(e=>context.window.native360Result(id,null,e.message));}};
vm.createContext(context);vm.runInContext(fs.readFileSync(__dirname+'/ui.js','utf8'),context);
(async()=>{await context.window.mobile360SelfTest();assert.equal(passed,true,nodes.status.textContent);assert.match(nodes.status.textContent,/passed/);assert.equal(nodes.export.disabled,false);assert.match(nodes.achievements.innerHTML,/Second/);console.log('PASS: shipped 360 UI performs synthetic import, selection, unlock, signed chunk transfer and native commit; no second file picker or network.');})().catch(e=>{console.error(e);process.exitCode=1;});
