// Synthetic CON/GPD fixtures only. No account, real profile or shipped signing key.
const assert=require('node:assert/strict'),crypto=require('node:crypto'),core=require('./profile-core.js');
const u32=(b,o,n)=>b.writeUInt32BE(n,o),u64=(b,o,n)=>b.writeBigUInt64BE(BigInt(n),o);
const sha=b=>crypto.createHash('sha1').update(b).digest();
function gpd(entries){const base=24+entries.length*18,b=Buffer.alloc(base+entries.reduce((n,e)=>n+e[2].length,0)+128);b.write('XDBF');u32(b,4,1);u32(b,8,entries.length);u32(b,12,entries.length);let pos=base;entries.forEach(([ns,id,data],i)=>{let o=24+i*18;b.writeUInt16BE(ns,o);u64(b,o+2,id);u32(b,o+10,pos-base);u32(b,o+14,data.length);data.copy(b,pos);pos+=data.length;});return b;}
function text(s){const b=Buffer.alloc((s.length+1)*2);for(let i=0;i<s.length;i++)b.writeUInt16BE(s.charCodeAt(i),i*2);return b;}
function achievement(id,name,score){const b=Buffer.alloc(28);u32(b,0,28);u32(b,4,id);u32(b,12,score);return Buffer.concat([b,text(name),text('Earned '+name),text('Do '+name)]);}
function sync(ids){const b=Buffer.alloc(ids.length*16);ids.forEach((n,i)=>u64(b,i*16,n));return b;}
function fixture(){
 const keys=crypto.generateKeyPairSync('rsa',{modulusLength:1024,publicExponent:65537}),jwk=keys.privateKey.export({format:'jwk'}),n=Buffer.from(jwk.n,'base64url'),identity=Buffer.alloc(556);identity.write('ALC1');identity.writeUInt16BE(424,4);identity.writeUInt32BE(65537,40);for(let i=0;i<128;i+=8)n.copy(identity,4+40+i,120-i,128-i);Buffer.from(jwk.p,'base64url').copy(identity,428);Buffer.from(jwk.q,'base64url').copy(identity,492);
 const game=gpd([[1,1,achievement(1,'First',10)],[1,2,achievement(2,'Second',20)],[1,0x100000000n,sync([1,2])],[1,0x200000000n,Buffer.from([0,0,0,0,0,0,0,1])]]);
 const title=Buffer.alloc(40);u32(title,0,1);u32(title,4,2);u32(title,12,30);
 const setting=id=>{const b=Buffer.alloc(20);u32(b,0,id);b[8]=1;return b;};
 const dashboard=gpd([[4,1,Buffer.concat([title,text('Synthetic game')])],[4,0x100000000n,sync([1])],[3,0x10040006,setting(0x10040006)],[3,0x10040013,setting(0x10040013)]]);
 const file=Buffer.alloc(core.blockOffset(170)+4096);file.write('CON ');u32(file,0x340,0x971a);u32(file,0x344,0x10000);file[0x371]=1;file.writeUInt16LE(1,0x37c);u32(file,0x395,171);u32(file,0x399,64);
 for(const [i,name,block,data] of [[0,'00000001.gpd',1,game],[1,'FFFE07D1.gpd',2,dashboard]]){const o=core.blockOffset(0)+i*64;file.write(name,o);file[o+40]=name.length;file.writeUIntLE(1,o+41,3);file.writeUIntLE(1,o+44,3);file.writeUIntLE(block,o+47,3);file.writeUInt16BE(65535,o+50);u32(file,o+52,data.length);data.copy(file,core.blockOffset(block));}
 for(let n=0;n<3;n++){const o=0xa000+n*24;sha(file.subarray(core.blockOffset(n),core.blockOffset(n)+4096)).copy(file,o);file[o+20]=128;file.writeUIntBE(0xffffff,o+21,3);}
 sha(file.subarray(0xa000,0xb000)).copy(file,0xb6000);sha(file.subarray(0xb8000,0xb9000)).copy(file,0xb6018);sha(file.subarray(0xb6000,0xb7000)).copy(file,0x381);sha(file.subarray(0x344,0xa000)).copy(file,0x32c);core.sign(file,core.signing(identity));return {file,identity,keys};
}
if(require.main!==module)module.exports={fixture,gpd,text,achievement,sync};else (async()=>{
 for(const n of [0,1,55,56,63,64,65,4096,1048576]){const b=crypto.randomBytes(n);assert.deepEqual(Buffer.from(core.sha1(b)),sha(b));}
 const {file,identity,keys}=fixture(); if(process.argv[2]==='--fixture'){require('node:fs').writeFileSync(process.argv[3],file);return;}
 if(process.argv[2]==='--gpd-fixtures'){const fs=require('node:fs'),path=require('node:path'),dir=process.argv[3];fs.mkdirSync(dir,{recursive:true});const used=gpd([[1,7,achievement(7,'Seventh',15)],[1,8,achievement(8,'Eighth',25)],[3,0x10040006,Buffer.alloc(20)],[5,0x8000,text('Catalog game')],[1,0x100000000n,sync([8,7])],[1,0x200000000n,Buffer.from([0,0,0,0,0,0,0,9])]]);used.writeUInt32BE(0x130000,core.records(used).find(e=>e.ns===1&&e.id===7n).start+16);const q=await core.Profile.open(file),added=await q.addGame('ABCD0001',used,identity);for(const [name,data] of [['input.gpd',used],['template.gpd',core.Gpd.template(used)],['added.con',added.bytes],['before.con',file]])fs.writeFileSync(path.join(dir,name),data);return;} const snapshot=Buffer.from(file),p=await core.Profile.open(file);assert.equal(p.games.length,1);assert.equal(p.list('00000001').length,2);
 const fingerprint=sha(p.working);for(const ids of [[],[1,1],[99]])await assert.rejects(p.unlock('00000001',ids,{mode:'offline'},identity));
 await assert.rejects(p.unlock('00000001',[1],{mode:'custom',start:'2099-01-01T00:00:00Z',stepMinutes:1},identity));assert.deepEqual(sha(p.working),fingerprint);
 await assert.rejects(p.unlock('00000001',[1],{mode:'xbox',references:[{id:1,score:10,titleId:'2',unlocked:true,online:false}]},identity));
 await assert.rejects(p.unlock('00000001',[1],{mode:'imported',references:[{name:'First',time:1609459200000},{name:'First',time:1609459200000}]},identity));
 const badKey=Buffer.from(identity);badKey[500]^=1;await assert.rejects(p.unlock('00000001',[1],{mode:'offline'},badKey));
 const out=await p.unlock('00000001',[1],{mode:'offline'},identity);assert.deepEqual(Buffer.from(out.backup),file);assert.deepEqual(file,snapshot);assert.equal(p.games[0].unlocked,1);assert.equal(p.games[0].score,10);assert.equal(p.list('00000001')[0].flags&0x30000,0x20000);assert.equal(p.list('00000001')[1].unlocked,false);
 // Verify the signature with Node's independent RSA implementation and SHA-1.
 assert.equal(crypto.verify('RSA-SHA1',out.bytes.subarray(0x22c,0x344),keys.publicKey,Buffer.from(out.bytes.subarray(0x1ac,0x22c)).reverse()),true);
 await assert.rejects(p.unlock('00000001',[1],{mode:'offline'},identity));
 await p.unlock('00000001',[2],{mode:'custom',start:'2021-01-01T00:00:00Z',stepMinutes:2},identity);assert.equal(p.games[0].unlocked,2);assert.equal(p.games[0].score,30);assert.equal(core.millis(BigInt(p.list('00000001')[1].time)),1609459200000);
 const corrupt=Buffer.from(file);corrupt[core.blockOffset(1)+100]^=1;await assert.rejects(core.Profile.open(corrupt));await assert.rejects(core.Profile.open(file.subarray(0,800)));
 p.working[100]^=1;await assert.rejects(p.unlock('00000001',[2],{mode:'offline'},identity));
 assert.deepEqual(core.referenceRows(JSON.stringify([{name:'First',timestamp:1609459200}])),[{name:'First',time:1609459200000}]);assert.throws(()=>core.referenceRows('[{"name":"First","timestamp":"2021-01-01T00:00:00"}]'));
 // Catalog GPDs: reset to locked, strip settings, insert as a new file, keep every existing file intact.
 const used=gpd([[1,7,achievement(7,'Seventh',15)],[1,8,achievement(8,'Eighth',25)],[3,0x10040006,Buffer.alloc(20)],[5,0x8000,text('Catalog game')],[2,0x8000,crypto.randomBytes(5000)],[1,0x100000000n,sync([8,7])],[1,0x200000000n,Buffer.from([0,0,0,0,0,0,0,9])]]);
 used.writeUInt32BE(0x130000,core.records(used).find(e=>e.ns===1&&e.id===7n).start+16);
 assert.equal(core.Gpd.inspect(used).unlocked,1);const usedCopy=Buffer.from(used),clean=core.Gpd.template(used),info=core.Gpd.inspect(clean);assert.deepEqual(used,usedCopy,'template must not modify its input');assert.deepEqual(info,{name:'Catalog game',achievements:2,gamerscore:40,unlocked:0});assert.deepEqual(core.Gpd.template(clean),clean);assert.equal(core.records(clean).some(e=>e.ns===3),false);
 assert.throws(()=>core.Gpd.template(gpd([[3,1,Buffer.alloc(20)]])));assert.throws(()=>core.Gpd.template(Buffer.from('nope')));
 const reshape=(allocated,free)=>{const b=Buffer.from(file);u32(b,0x395,allocated);u32(b,0x399,free);sha(b.subarray(0x344,0xa000)).copy(b,0x32c);core.sign(b,core.signing(identity));return b;};
 for(const [allocated,label] of [[171,'same group'],[340,'new hash group']]){const q=await core.Profile.open(reshape(allocated,40));const before=Buffer.from(q.working);await assert.rejects(q.addGame('00000001',used,identity));await assert.rejects(q.addGame('ABCD0001',used,badKey));assert.deepEqual(Buffer.from(q.working),before);
  const added=await q.addGame('abcd0001',used,identity);assert.equal(added.titleId,'ABCD0001',label);assert.deepEqual(Buffer.from(added.backup),before);assert.deepEqual(Buffer.from(q.package.read('ABCD0001.gpd')),Buffer.from(clean));assert.deepEqual(q.pending(),['ABCD0001']);assert.throws(()=>q.list('ABCD0001'));
  assert.equal(crypto.verify('RSA-SHA1',added.bytes.subarray(0x22c,0x344),keys.publicKey,Buffer.from(added.bytes.subarray(0x1ac,0x22c)).reverse()),true);const reopened=await core.Profile.open(added.bytes);assert.deepEqual(reopened.pending(),['ABCD0001']);assert.equal(reopened.list('00000001').length,2);assert.equal(Buffer.from(added.bytes).readInt32BE(0x395),allocated+Math.ceil(clean.length/4096));
  await assert.rejects(reopened.addGame('ABCD0001',used,identity));assert.deepEqual(Buffer.from(reopened.contribution('ABCD0001')),Buffer.from(clean));}
 await assert.rejects((await core.Profile.open(reshape(171,1))).addGame('ABCD0001',used,identity));
 console.log('PASS: synthetic GPD reset/strip/idempotence, add-game in existing and new hash groups, duplicate/insufficient-space/bad-key rejection, pending sync, contributions.');
 console.log('PASS: synthetic mobile 360 profile import, independent SHA-1/RSA verification, offline/custom signing, dashboard counts, unchanged input/unselected rows, duplicate/missing/already-unlocked selections, mismatched references, future/ambiguous dates, invalid signing data, corrupt/truncated/changed profiles.');
})().catch(e=>{console.error(e);process.exitCode=1;});
