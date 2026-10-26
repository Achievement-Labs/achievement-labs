'use strict';
(()=>{
let prefs={},gameArt=null;
const colour=(v,d)=>/^#[a-f0-9]{6}$/i.test(v||'')?v:d;
const number=(v,d,min,max)=>Number.isFinite(Number(v))?Math.max(min,Math.min(max,Number(v))):d;
const secure=value=>{try{const u=new URL(value);return u.protocol==='https:'&&!u.username&&!u.password&&(u.hostname==='store-images.s-microsoft.com'||u.hostname.endsWith('.xboxlive.com')||u.hostname.endsWith('.microsoft.com'))?u.href:'';}catch{return '';}};
function artwork(){
 const layer=document.getElementById('labsBackdrop360');if(!layer)return;
 const source=prefs.backgroundSource||'game';layer.hidden=prefs.oledMode===true||source==='none';
 let url='';
 if(source==='custom'&&/^data:image\/jpeg;base64,/.test(prefs.customImage||''))url=prefs.customImage;
 else if(source==='game'){
  let candidates=gameArt?.images||prefs.images||[],ratio=window.innerWidth/window.innerHeight;
  const purposes=ratio<1?['Poster','BoxArt','SuperHeroArt','TitledHeroArt']:['SuperHeroArt','TitledHeroArt','BoxArt','Poster'];
  candidates=candidates.filter(i=>purposes.includes(i.purpose)&&i.width>0&&i.height>0&&secure(i.url)).sort((a,b)=>{
   const score=i=>{const r=i.width/i.height;return (ratio<1?r<1:r>=1.4)?Math.abs(Math.log(r/ratio)):100+Math.abs(Math.log(r/ratio));};return score(a)-score(b);
  });
  url=candidates[0]?.url||(gameArt?secure(gameArt.url):prefs.persistentGameArt!==false?secure(prefs.backgroundURL):'');
 }
 const image=layer.querySelector('img');if(url){if(image.src!==url)image.src=url;}else image.removeAttribute('src');
}
window.setLabsAppearance=value=>{
 prefs=value||{};const root=document.documentElement;root.classList.toggle('labs-oled',prefs.oledMode===true);root.classList.toggle('labs-solid',prefs.glassEnabled===false);
 root.style.setProperty('--labs-accent',colour(prefs.accentColour,'#80e69e'));root.style.setProperty('--labs-tint',colour(prefs.glassTint,'#34594c'));root.style.setProperty('--labs-opacity',String(1-number(prefs.glassTransparency,16,0,60)/100));root.style.setProperty('--labs-blur',number(prefs.glassBlur,24,0,40)+'px');root.style.setProperty('--labs-dim',String(number(prefs.backgroundDim,45,0,85)/100));root.style.setProperty('--labs-art-blur',number(prefs.backgroundBlur,0,0,40)+'px');root.style.setProperty('--labs-fit',prefs.artworkFit==='contain'?'contain':'cover');
 if(!document.getElementById('labsBackdrop360')){const layer=document.createElement('div');layer.id='labsBackdrop360';layer.setAttribute('aria-hidden','true');layer.append(document.createElement('img'));document.body.prepend(layer);}
 artwork();
};
window.setLabsGameArt=value=>{gameArt=value;artwork();};
window.addEventListener('resize',artwork);
})();
