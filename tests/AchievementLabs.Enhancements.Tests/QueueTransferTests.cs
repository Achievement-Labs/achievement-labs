using AchievementLabs.Core;
using Newtonsoft.Json;
public static class QueueTransferTests
{
 public static void Run()
 {
  var value=new QueueTransfer { OwnerXuid="42",Games=[new TransferGame { TitleId="7",Name="Game",Items=[new TransferAchievement { AchievementId="1",Name="Example",DelaySeconds=120,RemainingDelaySeconds=23 }] }] };
  var text=JsonConvert.SerializeObject(value);var restored=QueueTransfer.Parse(text,"42");
  if(restored.Games[0].Items[0].RemainingDelaySeconds!=23||text.Contains("authorization"))throw new Exception("Portable queue round trip failed");
  bool rejected=false;try{QueueTransfer.Parse(text,"43");}catch(System.IO.InvalidDataException){rejected=true;}if(!rejected)throw new Exception("Wrong account accepted");
  restored.Games[0].Items[0].DelaySeconds=-1;rejected=false;try{restored.Validate("42");}catch(System.IO.InvalidDataException){rejected=true;}if(!rejected)throw new Exception("Invalid delay accepted");
  var many=QueueTransfer.Parse(text,"42");for(var i=2;i<30;i++)many.Games[0].Items.Add(new TransferAchievement { AchievementId=i.ToString(),Name="Unicode 🎮 achievement "+i,DelaySeconds=120 });
  var manyFrames=QueueQrTransfer.Frames(JsonConvert.SerializeObject(many),"42");if(manyFrames.Length<2)throw new Exception("Expected multiple QR frames");
  using(var generator=new QRCoder.QRCodeGenerator())using(var qrData=generator.CreateQrCode(manyFrames[0],QRCoder.QRCodeGenerator.ECCLevel.M))using(var qr=new QRCoder.PngByteQRCode(qrData))using(var png=new System.IO.MemoryStream(qr.GetGraphic(6))) {
   if(AchievementLabs.Desktop.MainWindow.ReadQueueQrImage(png)!=manyFrames[0])throw new Exception("QR PNG decode did not recover the actual frame");
  }
  var multiple=new QueueQrTransfer.Collector();string? multiResult=null;foreach(var frame in manyFrames.Reverse()){multiResult=multiple.Add(frame,"42");multiple.Add(frame,"42");}
  if(multiResult==null||QueueTransfer.Parse(multiResult,"42").Games[0].Items.Count!=29)throw new Exception("Out-of-order or duplicate QR frames lost data");
  var frames=QueueQrTransfer.Frames(text,"42");var collector=new QueueQrTransfer.Collector();string? assembled=null;
  foreach(var frame in frames.Reverse())assembled=collector.Add(frame,"42");
  if(assembled==null||QueueTransfer.Parse(assembled,"42").Games[0].Items[0].DelaySeconds!=120)throw new Exception("Queue QR reassembly failed");
  rejected=false;try{new QueueQrTransfer.Collector().Add(frames[0],"43");}catch(System.IO.InvalidDataException){rejected=true;}if(!rejected)throw new Exception("QR owner mismatch accepted");
  rejected=false;try{new QueueQrTransfer.Collector().Add("ALQ1|"+new string('0',64)+"|0|1|e30=","42");}catch(System.IO.InvalidDataException){rejected=true;}if(!rejected)throw new Exception("QR checksum failure accepted");
  var signin=MobileTransfer.Create("XBL3.0 x=123;synthetic-account","42","must-not-transfer",null,signInOnly:true);
  using(var envelope=System.Text.Json.JsonDocument.Parse(signin.Bytes)) {
   var root=envelope.RootElement;var clear=new byte[Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!).Length];
   var key=Convert.FromBase64String(signin.PairingKey.Replace('-','+').Replace('_','/')+"=");
   using var aes=new System.Security.Cryptography.AesGcm(key,16);aes.Decrypt(Convert.FromBase64String(root.GetProperty("nonce").GetString()!),Convert.FromBase64String(root.GetProperty("ciphertext").GetString()!),Convert.FromBase64String(root.GetProperty("tag").GetString()!),clear,System.Text.Encoding.UTF8.GetBytes("AchievementLabs-PC-Transfer-v1"));
   using var payload=System.Text.Json.JsonDocument.Parse(clear);if(payload.RootElement.GetProperty("kind").GetString()!="signIn"||payload.RootElement.GetProperty("eventToken").GetString()!=""||payload.RootElement.GetProperty("authorization").GetString()!="XBL3.0 x=123;synthetic-account")throw new Exception("Sign-in-only data leaked event token or lost account authorization");
  }
  Console.WriteLine("PASS: portable queue format, remaining delay, account validation and invalid-delay rejection. No network.");
 }
}
