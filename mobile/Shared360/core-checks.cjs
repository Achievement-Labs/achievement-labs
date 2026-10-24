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
 const file=Buffer.alloc(core.blockOffset(170)+4096);file.write('CON ');u32(file,0x340,0x971a);u32(file,0x344,0x10000);file[0x371]=1;file.writeUInt16LE(1,0x37c);u32(file,0x395,171);
 for(const [i,name,block,data] of [[0,'00000001.gpd',1,game],[1,'FFFE07D1.gpd',2,dashboard]]){const o=core.blockOffset(0)+i*64;file.write(name,o);file[o+40]=name.length;file.writeUIntLE(1,o+41,3);file.writeUIntLE(1,o+44,3);file.writeUIntLE(block,o+47,3);file.writeUInt16BE(65535,o+50);u32(file,o+52,data.length);data.copy(file,core.blockOffset(block));}
 for(let n=0;n<3;n++){const o=0xa000+n*24;sha(file.subarray(core.blockOffset(n),core.blockOffset(n)+4096)).copy(file,o);file[o+20]=128;file.writeUIntBE(0xffffff,o+21,3);}
 sha(file.subarray(0xa000,0xb000)).copy(file,0xb6000);sha(file.subarray(0xb8000,0xb9000)).copy(file,0xb6018);sha(file.subarray(0xb6000,0xb7000)).copy(file,0x381);sha(file.subarray(0x344,0xa000)).copy(file,0x32c);core.sign(file,core.signing(identity));return {file,identity,keys};
}
(async()=>{
 for(const n of [0,1,55,56,63,64,65,4096,1048576]){const b=crypto.randomBytes(n);assert.deepEqual(Buffer.from(core.sha1(b)),sha(b));}
 const {file,identity,keys}=fixture(); if(process.argv[2]==='--fixture'){require('node:fs').writeFileSync(process.argv[3],file);return;} const snapshot=Buffer.from(file),p=await core.Profile.open(file);assert.equal(p.games.length,1);assert.equal(p.list('00000001').length,2);
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
 console.log('PASS: synthetic mobile 360 profile import, independent SHA-1/RSA verification, offline/custom signing, dashboard counts, unchanged input/unselected rows, duplicate/missing/already-unlocked selections, mismatched references, future/ambiguous dates, invalid signing data, corrupt/truncated/changed profiles.');
})().catch(e=>{console.error(e);process.exitCode=1;});
